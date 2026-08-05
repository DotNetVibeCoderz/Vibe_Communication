using Microsoft.Extensions.Logging;
using Telepati.Client.Core;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Client.Data;

/// <summary>
/// Puts the local store in front of whichever transport is active.
///
/// The rule is cache-first with an API fallback: anything already pulled is served from disk,
/// and anything missing or stale goes to the server. Because this is a decorator over
/// <see cref="ITelepatiClient"/>, all three transports get it without knowing it exists, and
/// switching transport at runtime keeps the same cache.
///
/// Realtime pushes are written straight through, so the cache stays warm while the app runs
/// rather than only being filled on explicit reads.
/// </summary>
public sealed class CachedTelepatiClient : ITelepatiClient
{
    private readonly ITelepatiClient _inner;
    private readonly ILocalStore _store;
    private readonly LocalDatabaseOptions _options;
    private readonly ILogger<CachedTelepatiClient> _logger;

    private bool _storeInitialized;

    public CachedTelepatiClient(
        ITelepatiClient inner,
        ILocalStore store,
        ClientOptions options,
        ILogger<CachedTelepatiClient> logger)
    {
        _inner = inner;
        _store = store;
        _options = options.LocalDatabase;
        _logger = logger;

        // Anything the server pushes is cached immediately, then forwarded to the UI.
        _inner.MessageReceived += OnMessageReceived;
        _inner.MessageEdited += OnMessageEdited;
        _inner.MessageDeleted += OnMessageDeleted;
        _inner.ChatUpdated += OnChatUpdated;

        _inner.TypingChanged += n => TypingChanged?.Invoke(n);
        _inner.PresenceChanged += n => PresenceChanged?.Invoke(n);
        _inner.CallSignalReceived += s => CallSignalReceived?.Invoke(s);
        _inner.ConnectionStateChanged += c => ConnectionStateChanged?.Invoke(c);
    }

    public string TransportName => $"{_inner.TransportName} + {_store.ProviderName}";
    public bool IsConnected => _inner.IsConnected;
    public UserDto? CurrentUser => _inner.CurrentUser;

    /// <summary>True when the last read was answered from disk rather than the network.</summary>
    public bool LastReadWasLocal { get; private set; }

    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageEdited;
    public event Action<Guid, Guid>? MessageDeleted;
    public event Action<TypingNotification>? TypingChanged;
    public event Action<PresenceNotification>? PresenceChanged;
    public event Action<CallSignalDto>? CallSignalReceived;
    public event Action<ChatDto>? ChatUpdated;
    public event Action<bool>? ConnectionStateChanged;

    // -- auth -----------------------------------------------------------------

    public async Task<LoginResponse> LoginAsync(string usernameOrEmail, string password, string? twoFactorCode = null, CancellationToken ct = default)
    {
        var result = await _inner.LoginAsync(usernameOrEmail, password, twoFactorCode, ct);
        if (result is { Success: true, User: not null }) await EnsureStoreAsync(result.User.Id, ct);
        return result;
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var result = await _inner.RegisterAsync(request, ct);
        if (result is { Success: true, User: not null }) await EnsureStoreAsync(result.User.Id, ct);
        return result;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        // The cache holds one account's conversations; it must not survive into the next login.
        try { await _store.ClearAsync(ct); }
        catch (Exception e) { _logger.LogWarning(e, "Could not clear the local store on logout."); }

        await _inner.LogoutAsync(ct);
    }

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);

    // -- reads ----------------------------------------------------------------

    public async Task<PagedResult<ChatDto>> GetChatsAsync(int page = 1, int pageSize = 30, bool includeArchived = false, CancellationToken ct = default)
    {
        if (!Enabled || page > 1) return await _inner.GetChatsAsync(page, pageSize, includeArchived, ct);

        await EnsureStoreAsync(CurrentUser?.Id, ct);

        var cached = await SafeAsync(() => _store.GetChatsAsync(pageSize, ct), []);
        var fresh = await IsFreshAsync("chats", ct);

        if (cached.Count > 0 && fresh)
        {
            LastReadWasLocal = true;
            return new PagedResult<ChatDto>(cached, cached.Count, page, pageSize);
        }

        // Stale but present: show what we have now and reconcile in the background, so the
        // list appears instantly instead of after a round trip.
        if (cached.Count > 0)
        {
            LastReadWasLocal = true;
            _ = RefreshChatsAsync(pageSize, includeArchived);
            return new PagedResult<ChatDto>(cached, cached.Count, page, pageSize);
        }

        LastReadWasLocal = false;
        var remote = await _inner.GetChatsAsync(page, pageSize, includeArchived, ct);
        await PersistChatsAsync(remote.Items, ct);
        return remote;
    }

    public async Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default)
    {
        if (!Enabled) return await _inner.GetChatAsync(chatId, ct);

        var cached = await SafeAsync(() => _store.GetChatAsync(chatId, ct), null);
        if (cached is not null)
        {
            LastReadWasLocal = true;
            return cached;
        }

        LastReadWasLocal = false;
        var remote = await _inner.GetChatAsync(chatId, ct);
        if (remote is not null) await PersistChatsAsync([remote], ct);
        return remote;
    }

    public async Task<PagedResult<MessageDto>> GetMessagesAsync(Guid chatId, int page = 1, int pageSize = 50, Guid? beforeMessageId = null, CancellationToken ct = default)
    {
        if (!Enabled) return await _inner.GetMessagesAsync(chatId, page, pageSize, beforeMessageId, ct);

        await EnsureStoreAsync(CurrentUser?.Id, ct);

        // Scrollback is the case the cache is really for: a page read once never needs the
        // network again. The cursor is resolved locally so paging matches the server's.
        DateTimeOffset? before = null;
        if (beforeMessageId is not null)
        {
            var anchor = (await SafeAsync(() => _store.GetMessagesAsync(chatId, 1000, null, ct), []))
                .FirstOrDefault(m => m.Id == beforeMessageId);
            before = anchor?.CreatedAt;
        }

        var cached = await SafeAsync(() => _store.GetMessagesAsync(chatId, pageSize, before, ct), []);

        // A full page from disk is trusted. A partial page means the local history has a hole,
        // so the server is asked instead of showing an incomplete thread.
        if (cached.Count >= pageSize || (cached.Count > 0 && beforeMessageId is not null))
        {
            LastReadWasLocal = true;
            if (beforeMessageId is null) _ = RefreshMessagesAsync(chatId, pageSize);
            return new PagedResult<MessageDto>(cached, cached.Count, page, pageSize);
        }

        LastReadWasLocal = false;
        var remote = await _inner.GetMessagesAsync(chatId, page, pageSize, beforeMessageId, ct);
        await SafeAsync(() => _store.SaveMessagesAsync(remote.Items, ct));
        return remote;
    }

    public async Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default)
    {
        if (!Enabled) return await _inner.GetContactsAsync(ct);

        await EnsureStoreAsync(CurrentUser?.Id, ct);

        var cached = await SafeAsync(() => _store.GetContactsAsync(ct), []);
        if (cached.Count > 0 && await IsFreshAsync("contacts", ct))
        {
            LastReadWasLocal = true;
            return cached;
        }

        LastReadWasLocal = false;
        var remote = await _inner.GetContactsAsync(ct);
        await SafeAsync(() => _store.SaveContactsAsync(remote, ct));
        await SafeAsync(() => _store.SetSyncedAtAsync("contacts", DateTimeOffset.UtcNow, ct));
        return remote;
    }

    // -- writes ---------------------------------------------------------------

    public async Task<MessageDto?> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        var sent = await _inner.SendMessageAsync(request, ct);
        if (sent is not null) await SafeAsync(() => _store.SaveMessagesAsync([sent], ct));
        return sent;
    }

    public async Task<MessageDto?> EditMessageAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        var edited = await _inner.EditMessageAsync(messageId, content, ct);
        if (edited is not null) await SafeAsync(() => _store.SaveMessagesAsync([edited], ct));
        return edited;
    }

    public async Task<ApiResult> DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        var result = await _inner.DeleteMessageAsync(messageId, ct);
        if (result.Success) await SafeAsync(() => _store.DeleteMessageAsync(messageId, ct));
        return result;
    }

    public async Task<ChatDto?> CreateChatAsync(CreateChatRequest request, CancellationToken ct = default)
    {
        var chat = await _inner.CreateChatAsync(request, ct);
        if (chat is not null) await PersistChatsAsync([chat], ct);
        return chat;
    }

    public async Task<ChatDto?> OpenDirectChatAsync(Guid otherUserId, CancellationToken ct = default)
    {
        var chat = await _inner.OpenDirectChatAsync(otherUserId, ct);
        if (chat is not null) await PersistChatsAsync([chat], ct);
        return chat;
    }

    // -- straight pass-through ------------------------------------------------

    public Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default) => _inner.GetMembersAsync(chatId, ct);
    public Task<ApiResult> ReactAsync(Guid messageId, string emoji, CancellationToken ct = default) => _inner.ReactAsync(messageId, emoji, ct);
    public Task<ApiResult> MarkReadAsync(Guid chatId, Guid lastReadMessageId, CancellationToken ct = default) => _inner.MarkReadAsync(chatId, lastReadMessageId, ct);
    public Task<ApiResult> SetTypingAsync(Guid chatId, bool isTyping, CancellationToken ct = default) => _inner.SetTypingAsync(chatId, isTyping, ct);
    public Task<ApiResult> SetPresenceAsync(int presence, CancellationToken ct = default) => _inner.SetPresenceAsync(presence, ct);
    public Task<IReadOnlyList<UserDto>> SearchUsersAsync(ContactSearchRequest request, CancellationToken ct = default) => _inner.SearchUsersAsync(request, ct);
    public Task<ApiResult> AddContactAsync(Guid userId, string? alias = null, CancellationToken ct = default) => _inner.AddContactAsync(userId, alias, ct);
    public Task<IReadOnlyList<StatusPostDto>> GetStatusFeedAsync(CancellationToken ct = default) => _inner.GetStatusFeedAsync(ct);
    public Task<ApiResult> ViewStatusAsync(Guid statusId, CancellationToken ct = default) => _inner.ViewStatusAsync(statusId, ct);
    public Task<ApiResult<CallSignalDto>> StartCallAsync(Guid chatId, int type, CancellationToken ct = default) => _inner.StartCallAsync(chatId, type, ct);
    public Task<ApiResult> SendCallSignalAsync(CallSignalDto signal, CancellationToken ct = default) => _inner.SendCallSignalAsync(signal, ct);
    public Task<ApiResult> AnswerCallAsync(Guid callId, bool accepted, CancellationToken ct = default) => _inner.AnswerCallAsync(callId, accepted, ct);
    public Task<ApiResult> EndCallAsync(Guid callId, CancellationToken ct = default) => _inner.EndCallAsync(callId, ct);
    public Task<ThemeDto?> GetActiveThemeAsync(CancellationToken ct = default) => _inner.GetActiveThemeAsync(ct);

    // -- store plumbing -------------------------------------------------------

    public Task<LocalStoreStats> GetLocalStatsAsync(CancellationToken ct = default) => _store.GetStatsAsync(ct);
    public Task ClearLocalAsync(CancellationToken ct = default) => _store.ClearAsync(ct);

    private bool Enabled => _options.Enabled && _store is not NullLocalStore;

    private async Task EnsureStoreAsync(Guid? userId, CancellationToken ct = default)
    {
        if (_storeInitialized || userId is null || !Enabled) return;

        await _store.InitializeAsync(userId.Value, ct);
        _storeInitialized = true;

        // Pruning on open keeps the file bounded without needing a background job.
        _ = SafeAsync(() => _store.PruneAsync(_options.KeepMessagesPerChat, TimeSpan.FromDays(_options.RetentionDays), ct));
    }

    private async Task<bool> IsFreshAsync(string key, CancellationToken ct)
    {
        var syncedAt = await SafeAsync(() => _store.GetSyncedAtAsync(key, ct), null);
        return syncedAt is not null && DateTimeOffset.UtcNow - syncedAt.Value < TimeSpan.FromSeconds(_options.FreshnessSeconds);
    }

    private async Task PersistChatsAsync(IReadOnlyList<ChatDto> chats, CancellationToken ct)
    {
        await SafeAsync(() => _store.SaveChatsAsync(chats, ct));

        // A chat row carries its last message; caching it means the preview survives a restart.
        var previews = chats.Select(c => c.LastMessage).OfType<MessageDto>().ToList();
        if (previews.Count > 0) await SafeAsync(() => _store.SaveMessagesAsync(previews, ct));

        await SafeAsync(() => _store.SetSyncedAtAsync("chats", DateTimeOffset.UtcNow, ct));
    }

    private async Task RefreshChatsAsync(int pageSize, bool includeArchived)
    {
        try
        {
            var remote = await _inner.GetChatsAsync(1, pageSize, includeArchived);
            await PersistChatsAsync(remote.Items, CancellationToken.None);
            foreach (var chat in remote.Items) ChatUpdated?.Invoke(chat);
        }
        catch (Exception e)
        {
            // Serving stale data beats failing; the next read tries again.
            _logger.LogDebug(e, "Background chat refresh failed.");
        }
    }

    private async Task RefreshMessagesAsync(Guid chatId, int pageSize)
    {
        try
        {
            var remote = await _inner.GetMessagesAsync(chatId, 1, pageSize);
            await SafeAsync(() => _store.SaveMessagesAsync(remote.Items, CancellationToken.None));
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Background message refresh failed for chat {ChatId}.", chatId);
        }
    }

    // -- realtime write-through -----------------------------------------------

    private void OnMessageReceived(MessageDto message)
    {
        _ = SafeAsync(() => _store.SaveMessagesAsync([message], CancellationToken.None));
        MessageReceived?.Invoke(message);
    }

    private void OnMessageEdited(MessageDto message)
    {
        _ = SafeAsync(() => _store.SaveMessagesAsync([message], CancellationToken.None));
        MessageEdited?.Invoke(message);
    }

    private void OnMessageDeleted(Guid chatId, Guid messageId)
    {
        _ = SafeAsync(() => _store.DeleteMessageAsync(messageId, CancellationToken.None));
        MessageDeleted?.Invoke(chatId, messageId);
    }

    private void OnChatUpdated(ChatDto chat)
    {
        _ = SafeAsync(() => _store.SaveChatsAsync([chat], CancellationToken.None));
        ChatUpdated?.Invoke(chat);
    }

    /// <summary>
    /// A failing cache must never break the app — a corrupt or locked file degrades to
    /// "no cache", not to an error the user sees.
    /// </summary>
    private async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try { return await action(); }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Local store read failed; falling back to the server.");
            return fallback;
        }
    }

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { _logger.LogWarning(e, "Local store write failed; continuing without it."); }
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await _store.DisposeAsync();
    }
}
