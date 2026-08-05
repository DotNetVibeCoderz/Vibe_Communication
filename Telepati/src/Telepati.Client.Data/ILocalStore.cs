using Telepati.Shared.Contracts;

namespace Telepati.Client.Data;

/// <summary>
/// On-device cache of everything the client has already pulled from the server. Desktop and
/// mobile keep one so a cold start paints from disk instead of waiting on the network, and so
/// scrollback that was read once never has to be fetched again.
///
/// It is a cache, not a source of truth: the server always wins on conflict, and losing the
/// file costs nothing but a re-fetch.
/// </summary>
public interface ILocalStore : IAsyncDisposable
{
    string ProviderName { get; }
    bool IsReady { get; }

    /// <summary>Opens (or creates) the store for one account. Each account gets its own file.</summary>
    Task InitializeAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<ChatDto>> GetChatsAsync(int take = 50, CancellationToken ct = default);
    Task SaveChatsAsync(IEnumerable<ChatDto> chats, CancellationToken ct = default);
    Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default);

    /// <summary>
    /// Newest-first page of a conversation. <paramref name="before"/> is a cursor, matching how
    /// the server pages, so local and remote scrollback behave identically.
    /// </summary>
    Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid chatId, int take = 50, DateTimeOffset? before = null, CancellationToken ct = default);
    Task SaveMessagesAsync(IEnumerable<MessageDto> messages, CancellationToken ct = default);
    Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default);
    Task<int> CountMessagesAsync(Guid chatId, CancellationToken ct = default);

    Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default);
    Task SaveContactsAsync(IEnumerable<ContactDto> contacts, CancellationToken ct = default);

    /// <summary>When this chat was last reconciled with the server.</summary>
    Task<DateTimeOffset?> GetSyncedAtAsync(string key, CancellationToken ct = default);
    Task SetSyncedAtAsync(string key, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Trims history so the file cannot grow without bound.</summary>
    Task<int> PruneAsync(int keepPerChat, TimeSpan maxAge, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<LocalStoreStats> GetStatsAsync(CancellationToken ct = default);
}

public record LocalStoreStats(string Provider, string Location, int ChatCount, int MessageCount, int ContactCount, long SizeBytes);

/// <summary>Used by the web app, which persists in the browser rather than in a database file.</summary>
public sealed class NullLocalStore : ILocalStore
{
    public string ProviderName => "None";
    public bool IsReady => false;

    public Task InitializeAsync(Guid userId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<ChatDto>> GetChatsAsync(int take = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ChatDto>>([]);
    public Task SaveChatsAsync(IEnumerable<ChatDto> chats, CancellationToken ct = default) => Task.CompletedTask;
    public Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default) => Task.FromResult<ChatDto?>(null);
    public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid chatId, int take = 50, DateTimeOffset? before = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MessageDto>>([]);
    public Task SaveMessagesAsync(IEnumerable<MessageDto> messages, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteMessageAsync(Guid messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CountMessagesAsync(Guid chatId, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContactDto>>([]);
    public Task SaveContactsAsync(IEnumerable<ContactDto> contacts, CancellationToken ct = default) => Task.CompletedTask;
    public Task<DateTimeOffset?> GetSyncedAtAsync(string key, CancellationToken ct = default) => Task.FromResult<DateTimeOffset?>(null);
    public Task SetSyncedAtAsync(string key, DateTimeOffset at, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> PruneAsync(int keepPerChat, TimeSpan maxAge, CancellationToken ct = default) => Task.FromResult(0);
    public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<LocalStoreStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new LocalStoreStats("None", "-", 0, 0, 0, 0));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
