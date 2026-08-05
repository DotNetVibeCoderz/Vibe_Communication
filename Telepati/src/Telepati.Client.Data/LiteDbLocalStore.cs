using LiteDB;
using Microsoft.Extensions.Logging;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Client.Data;

/// <summary>
/// Alternative local store. LiteDB is a single-file embedded document database with no native
/// dependency, which makes it the easier option on platforms where shipping the SQLite native
/// library is awkward — and it stores the DTOs as documents without a schema step.
///
/// LiteDB's API is synchronous, so calls are wrapped rather than faked as async: the work really
/// does happen on the calling thread, and pretending otherwise would only hide that.
/// </summary>
public sealed class LiteDbLocalStore(ClientOptions options, ILogger<LiteDbLocalStore> logger) : ILocalStore
{
    private LiteDatabase? _database;
    private ILiteCollection<ChatRecord>? _chats;
    private ILiteCollection<MessageRecord>? _messages;
    private ILiteCollection<ContactRecord>? _contacts;
    private ILiteCollection<SyncRecord>? _sync;
    private string _path = string.Empty;

    public string ProviderName => "LiteDB";
    public bool IsReady => _database is not null;

    public Task InitializeAsync(Guid userId, CancellationToken ct = default)
    {
        if (_database is not null) return Task.CompletedTask;

        _path = LocalStorePaths.Resolve(options.LocalDatabase, userId, "litedb");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        _database = new LiteDatabase(new ConnectionString
        {
            Filename = _path,
            Connection = ConnectionType.Shared
        });

        _chats = _database.GetCollection<ChatRecord>("chats");
        _messages = _database.GetCollection<MessageRecord>("messages");
        _contacts = _database.GetCollection<ContactRecord>("contacts");
        _sync = _database.GetCollection<SyncRecord>("sync");

        // Same access pattern as the SQLite store: newest page of one conversation.
        _messages.EnsureIndex(m => m.ChatId);
        _messages.EnsureIndex(m => m.CreatedAtTicks);
        _chats.EnsureIndex(c => c.LastMessageAtTicks);

        logger.LogInformation("Local LiteDB store ready at {Path}", _path);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatDto>> GetChatsAsync(int take = 50, CancellationToken ct = default)
    {
        if (_chats is null) return Task.FromResult<IReadOnlyList<ChatDto>>([]);

        var results = _chats.FindAll()
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastMessageAtTicks)
            .Take(take)
            .Select(c => c.Chat)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChatDto>>(results);
    }

    public Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default) =>
        Task.FromResult(_chats?.FindById(chatId)?.Chat);

    public Task SaveChatsAsync(IEnumerable<ChatDto> chats, CancellationToken ct = default)
    {
        if (_chats is null) return Task.CompletedTask;

        _chats.Upsert(chats.Select(c => new ChatRecord
        {
            Id = c.Id,
            LastMessageAtTicks = c.LastMessageAt?.UtcTicks ?? 0,
            IsPinned = c.IsPinned,
            Chat = c
        }));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid chatId, int take = 50, DateTimeOffset? before = null, CancellationToken ct = default)
    {
        if (_messages is null) return Task.FromResult<IReadOnlyList<MessageDto>>([]);

        var cursor = before?.UtcTicks ?? long.MaxValue;

        var results = _messages
            .Find(m => m.ChatId == chatId && m.CreatedAtTicks < cursor)
            .OrderByDescending(m => m.CreatedAtTicks)
            .Take(take)
            .Select(m => m.Message)
            .OrderBy(m => m.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<MessageDto>>(results);
    }

    public Task SaveMessagesAsync(IEnumerable<MessageDto> messages, CancellationToken ct = default)
    {
        if (_messages is null) return Task.CompletedTask;

        _messages.Upsert(messages.Select(m => new MessageRecord
        {
            Id = m.Id,
            ChatId = m.ChatId,
            CreatedAtTicks = m.CreatedAt.UtcTicks,
            Message = m
        }));

        return Task.CompletedTask;
    }

    public Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        _messages?.Delete(messageId);
        return Task.CompletedTask;
    }

    public Task<int> CountMessagesAsync(Guid chatId, CancellationToken ct = default) =>
        Task.FromResult(_messages?.Count(m => m.ChatId == chatId) ?? 0);

    public Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default)
    {
        if (_contacts is null) return Task.FromResult<IReadOnlyList<ContactDto>>([]);

        var results = _contacts.FindAll().OrderBy(c => c.Name).Select(c => c.Contact).ToList();
        return Task.FromResult<IReadOnlyList<ContactDto>>(results);
    }

    public Task SaveContactsAsync(IEnumerable<ContactDto> contacts, CancellationToken ct = default)
    {
        if (_contacts is null) return Task.CompletedTask;

        _contacts.Upsert(contacts.Select(c => new ContactRecord
        {
            Id = c.Id,
            Name = c.Alias ?? c.User.DisplayName,
            Contact = c
        }));

        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetSyncedAtAsync(string key, CancellationToken ct = default)
    {
        var record = _sync?.FindById(key);
        return Task.FromResult(record is null ? null : (DateTimeOffset?)new DateTimeOffset(record.SyncedAtTicks, TimeSpan.Zero));
    }

    public Task SetSyncedAtAsync(string key, DateTimeOffset at, CancellationToken ct = default)
    {
        _sync?.Upsert(new SyncRecord { Id = key, SyncedAtTicks = at.UtcTicks });
        return Task.CompletedTask;
    }

    public Task<int> PruneAsync(int keepPerChat, TimeSpan maxAge, CancellationToken ct = default)
    {
        if (_messages is null) return Task.FromResult(0);

        var cutoff = (DateTimeOffset.UtcNow - maxAge).UtcTicks;

        // LiteDB has no window functions, so the "keep the newest N per chat" exemption is
        // computed per conversation before deleting.
        var doomed = _messages.Find(m => m.CreatedAtTicks < cutoff)
            .GroupBy(m => m.ChatId)
            .SelectMany(group => group.OrderByDescending(m => m.CreatedAtTicks).Skip(keepPerChat))
            .Select(m => m.Id)
            .ToList();

        foreach (var id in doomed) _messages.Delete(id);

        if (doomed.Count > 0) _database?.Rebuild();
        return Task.FromResult(doomed.Count);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _messages?.DeleteAll();
        _chats?.DeleteAll();
        _contacts?.DeleteAll();
        _sync?.DeleteAll();
        _database?.Rebuild();
        return Task.CompletedTask;
    }

    public Task<LocalStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        var size = File.Exists(_path) ? new FileInfo(_path).Length : 0;

        return Task.FromResult(new LocalStoreStats(
            ProviderName, _path,
            _chats?.Count() ?? 0,
            _messages?.Count() ?? 0,
            _contacts?.Count() ?? 0,
            size));
    }

    public ValueTask DisposeAsync()
    {
        _database?.Dispose();
        _database = null;
        return ValueTask.CompletedTask;
    }

    // Wrapper documents. LiteDB maps the DTOs directly; these only add the indexed sort keys.
    private sealed class ChatRecord
    {
        public Guid Id { get; set; }
        public long LastMessageAtTicks { get; set; }
        public bool IsPinned { get; set; }
        public ChatDto Chat { get; set; } = default!;
    }

    private sealed class MessageRecord
    {
        public Guid Id { get; set; }
        public Guid ChatId { get; set; }
        public long CreatedAtTicks { get; set; }
        public MessageDto Message { get; set; } = default!;
    }

    private sealed class ContactRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ContactDto Contact { get; set; } = default!;
    }

    private sealed class SyncRecord
    {
        public string Id { get; set; } = string.Empty;
        public long SyncedAtTicks { get; set; }
    }
}
