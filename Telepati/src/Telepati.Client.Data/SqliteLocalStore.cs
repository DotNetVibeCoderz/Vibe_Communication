using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Client.Data;

/// <summary>
/// Default local store. Rows keep their identifiers and sort keys in real columns and the DTO
/// itself as a JSON blob — the client never queries inside a message, so a full relational model
/// would cost schema-migration pain for no read benefit.
///
/// WAL mode plus <c>synchronous=NORMAL</c> is the standard client-side trade: writes stop
/// blocking reads, and the worst case of a hard power cut is losing the last few cached rows,
/// which the next sync re-fetches anyway.
/// </summary>
public sealed class SqliteLocalStore(ClientOptions options, ILogger<SqliteLocalStore> logger) : ILocalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private SqliteConnection? _connection;
    private string _path = string.Empty;

    public string ProviderName => "SQLite";
    public bool IsReady => _connection is not null;

    public async Task InitializeAsync(Guid userId, CancellationToken ct = default)
    {
        if (_connection is not null) return;

        _path = LocalStorePaths.Resolve(options.LocalDatabase, userId, "db");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());

        await _connection.OpenAsync(ct);

        await ExecuteAsync("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA mmap_size=67108864;

            CREATE TABLE IF NOT EXISTS Chats (
                Id            TEXT PRIMARY KEY,
                LastMessageAt INTEGER,
                IsPinned      INTEGER NOT NULL DEFAULT 0,
                Payload       TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id        TEXT PRIMARY KEY,
                ChatId    TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                Payload   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Contacts (
                Id      TEXT PRIMARY KEY,
                Name    TEXT,
                Payload TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SyncState (
                Key      TEXT PRIMARY KEY,
                SyncedAt INTEGER NOT NULL
            );

            -- The only hot read is "newest page of one chat", so one composite index serves it.
            CREATE INDEX IF NOT EXISTS IX_Messages_Chat_Created ON Messages (ChatId, CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Chats_LastMessageAt   ON Chats (IsPinned DESC, LastMessageAt DESC);
            """, ct);

        logger.LogInformation("Local SQLite store ready at {Path}", _path);
    }

    // -- chats ----------------------------------------------------------------

    public async Task<IReadOnlyList<ChatDto>> GetChatsAsync(int take = 50, CancellationToken ct = default)
    {
        if (_connection is null) return [];

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Chats ORDER BY IsPinned DESC, LastMessageAt DESC LIMIT $take";
        command.Parameters.AddWithValue("$take", take);

        return await ReadAllAsync<ChatDto>(command, ct);
    }

    public async Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default)
    {
        if (_connection is null) return null;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Chats WHERE Id = $id";
        command.Parameters.AddWithValue("$id", chatId.ToString());

        var payload = await command.ExecuteScalarAsync(ct) as string;
        return payload is null ? null : JsonSerializer.Deserialize<ChatDto>(payload, Json);
    }

    public async Task SaveChatsAsync(IEnumerable<ChatDto> chats, CancellationToken ct = default)
    {
        if (_connection is null) return;

        await using var transaction = await _connection.BeginTransactionAsync(ct);
        await using var command = _connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Chats (Id, LastMessageAt, IsPinned, Payload) VALUES ($id, $at, $pinned, $payload)
            ON CONFLICT(Id) DO UPDATE SET LastMessageAt = $at, IsPinned = $pinned, Payload = $payload
            """;

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Integer);
        var pinned = command.Parameters.Add("$pinned", SqliteType.Integer);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);

        foreach (var chat in chats)
        {
            id.Value = chat.Id.ToString();
            at.Value = chat.LastMessageAt?.UtcTicks ?? 0L;
            pinned.Value = chat.IsPinned ? 1 : 0;
            payload.Value = JsonSerializer.Serialize(chat, Json);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    // -- messages -------------------------------------------------------------

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid chatId, int take = 50, DateTimeOffset? before = null, CancellationToken ct = default)
    {
        if (_connection is null) return [];

        await using var command = _connection.CreateCommand();
        command.CommandText = before is null
            ? "SELECT Payload FROM Messages WHERE ChatId = $chat ORDER BY CreatedAt DESC LIMIT $take"
            : "SELECT Payload FROM Messages WHERE ChatId = $chat AND CreatedAt < $before ORDER BY CreatedAt DESC LIMIT $take";

        command.Parameters.AddWithValue("$chat", chatId.ToString());
        command.Parameters.AddWithValue("$take", take);
        if (before is not null) command.Parameters.AddWithValue("$before", before.Value.UtcTicks);

        var messages = await ReadAllAsync<MessageDto>(command, ct);

        // Stored newest-first for the cursor; handed back oldest-first for rendering.
        return messages.OrderBy(m => m.CreatedAt).ToList();
    }

    public async Task SaveMessagesAsync(IEnumerable<MessageDto> messages, CancellationToken ct = default)
    {
        if (_connection is null) return;

        await using var transaction = await _connection.BeginTransactionAsync(ct);
        await using var command = _connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Messages (Id, ChatId, CreatedAt, Payload) VALUES ($id, $chat, $at, $payload)
            ON CONFLICT(Id) DO UPDATE SET Payload = $payload
            """;

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var chat = command.Parameters.Add("$chat", SqliteType.Text);
        var at = command.Parameters.Add("$at", SqliteType.Integer);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);

        foreach (var message in messages)
        {
            id.Value = message.Id.ToString();
            chat.Value = message.ChatId.ToString();
            at.Value = message.CreatedAt.UtcTicks;
            payload.Value = JsonSerializer.Serialize(message, Json);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        if (_connection is null) return;

        await using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM Messages WHERE Id = $id";
        command.Parameters.AddWithValue("$id", messageId.ToString());
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountMessagesAsync(Guid chatId, CancellationToken ct = default)
    {
        if (_connection is null) return 0;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Messages WHERE ChatId = $chat";
        command.Parameters.AddWithValue("$chat", chatId.ToString());

        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    // -- contacts -------------------------------------------------------------

    public async Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default)
    {
        if (_connection is null) return [];

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Contacts ORDER BY Name";
        return await ReadAllAsync<ContactDto>(command, ct);
    }

    public async Task SaveContactsAsync(IEnumerable<ContactDto> contacts, CancellationToken ct = default)
    {
        if (_connection is null) return;

        await using var transaction = await _connection.BeginTransactionAsync(ct);
        await using var command = _connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Contacts (Id, Name, Payload) VALUES ($id, $name, $payload)
            ON CONFLICT(Id) DO UPDATE SET Name = $name, Payload = $payload
            """;

        var id = command.Parameters.Add("$id", SqliteType.Text);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);

        foreach (var contact in contacts)
        {
            id.Value = contact.Id.ToString();
            name.Value = contact.Alias ?? contact.User.DisplayName;
            payload.Value = JsonSerializer.Serialize(contact, Json);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    // -- sync state -----------------------------------------------------------

    public async Task<DateTimeOffset?> GetSyncedAtAsync(string key, CancellationToken ct = default)
    {
        if (_connection is null) return null;

        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT SyncedAt FROM SyncState WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);

        var ticks = await command.ExecuteScalarAsync(ct);
        return ticks is null or DBNull ? null : new DateTimeOffset(Convert.ToInt64(ticks), TimeSpan.Zero);
    }

    public async Task SetSyncedAtAsync(string key, DateTimeOffset at, CancellationToken ct = default)
    {
        if (_connection is null) return;

        await using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SyncState (Key, SyncedAt) VALUES ($key, $at)
            ON CONFLICT(Key) DO UPDATE SET SyncedAt = $at
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$at", at.UtcTicks);
        await command.ExecuteNonQueryAsync(ct);
    }

    // -- maintenance ----------------------------------------------------------

    public async Task<int> PruneAsync(int keepPerChat, TimeSpan maxAge, CancellationToken ct = default)
    {
        if (_connection is null) return 0;

        var cutoff = (DateTimeOffset.UtcNow - maxAge).UtcTicks;

        // Drop anything old, but never the newest keepPerChat rows of a conversation — those
        // are what make the next cold start instant.
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            DELETE FROM Messages
            WHERE CreatedAt < $cutoff
              AND Id NOT IN (
                SELECT Id FROM (
                  SELECT Id, ROW_NUMBER() OVER (PARTITION BY ChatId ORDER BY CreatedAt DESC) AS rn
                  FROM Messages
                ) WHERE rn <= $keep
              )
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$keep", keepPerChat);

        var removed = await command.ExecuteNonQueryAsync(ct);
        if (removed > 0) await ExecuteAsync("PRAGMA incremental_vacuum;", ct);

        return removed;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (_connection is null) return;
        await ExecuteAsync("DELETE FROM Messages; DELETE FROM Chats; DELETE FROM Contacts; DELETE FROM SyncState; VACUUM;", ct);
    }

    public async Task<LocalStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        if (_connection is null) return new LocalStoreStats(ProviderName, "-", 0, 0, 0, 0);

        var chats = await ScalarAsync("SELECT COUNT(*) FROM Chats", ct);
        var messages = await ScalarAsync("SELECT COUNT(*) FROM Messages", ct);
        var contacts = await ScalarAsync("SELECT COUNT(*) FROM Contacts", ct);
        var size = File.Exists(_path) ? new FileInfo(_path).Length : 0;

        return new LocalStoreStats(ProviderName, _path, chats, messages, contacts, size);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null) return;

        await _connection.CloseAsync();
        await _connection.DisposeAsync();
        _connection = null;
    }

    // -- helpers --------------------------------------------------------------

    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<int> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteCommand command, CancellationToken ct)
    {
        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<T>(reader.GetString(0), Json);
            if (item is not null) results.Add(item);
        }

        return results;
    }
}

internal static class LocalStorePaths
{
    /// <summary>
    /// One file per account under the platform's app-data folder, so switching users never
    /// mixes two people's cached conversations.
    /// </summary>
    public static string Resolve(LocalDatabaseOptions options, Guid userId, string extension)
    {
        var root = string.IsNullOrWhiteSpace(options.Path)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Telepati")
            : options.Path;

        if (!Path.IsPathRooted(root)) root = Path.Combine(AppContext.BaseDirectory, root);

        return Path.Combine(root, $"telepati-{userId:N}.{extension}");
    }
}
