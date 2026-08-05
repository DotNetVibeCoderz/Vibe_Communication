using Telepati.Shared.Contracts;

namespace Telepati.Client.Core;

/// <summary>
/// The only server-facing surface a client app sees. Three implementations exist — SignalR,
/// gRPC and REST — and every UI in this solution is written against this interface alone. That
/// is what lets the transport be a runtime setting rather than a build-time decision.
/// </summary>
public interface ITelepatiClient : IAsyncDisposable
{
    string TransportName { get; }
    bool IsConnected { get; }
    UserDto? CurrentUser { get; }

    event Action<MessageDto>? MessageReceived;
    event Action<MessageDto>? MessageEdited;
    event Action<Guid, Guid>? MessageDeleted;
    event Action<TypingNotification>? TypingChanged;
    event Action<PresenceNotification>? PresenceChanged;
    event Action<CallSignalDto>? CallSignalReceived;
    event Action<ChatDto>? ChatUpdated;
    event Action<bool>? ConnectionStateChanged;

    Task<LoginResponse> LoginAsync(string usernameOrEmail, string password, string? twoFactorCode = null, CancellationToken ct = default);
    Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task LogoutAsync(CancellationToken ct = default);

    /// <summary>Opens the realtime channel. Safe to call more than once.</summary>
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    Task<PagedResult<ChatDto>> GetChatsAsync(int page = 1, int pageSize = 30, bool includeArchived = false, CancellationToken ct = default);
    Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default);
    Task<ChatDto?> CreateChatAsync(CreateChatRequest request, CancellationToken ct = default);
    Task<ChatDto?> OpenDirectChatAsync(Guid otherUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default);

    Task<PagedResult<MessageDto>> GetMessagesAsync(Guid chatId, int page = 1, int pageSize = 50, Guid? beforeMessageId = null, CancellationToken ct = default);
    Task<MessageDto?> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default);
    Task<MessageDto?> EditMessageAsync(Guid messageId, string content, CancellationToken ct = default);
    Task<ApiResult> DeleteMessageAsync(Guid messageId, CancellationToken ct = default);
    Task<ApiResult> ReactAsync(Guid messageId, string emoji, CancellationToken ct = default);
    Task<ApiResult> MarkReadAsync(Guid chatId, Guid lastReadMessageId, CancellationToken ct = default);
    Task<ApiResult> SetTypingAsync(Guid chatId, bool isTyping, CancellationToken ct = default);
    Task<ApiResult> SetPresenceAsync(int presence, CancellationToken ct = default);

    Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> SearchUsersAsync(ContactSearchRequest request, CancellationToken ct = default);
    Task<ApiResult> AddContactAsync(Guid userId, string? alias = null, CancellationToken ct = default);

    Task<IReadOnlyList<StatusPostDto>> GetStatusFeedAsync(CancellationToken ct = default);

    /// <summary>Marks a status as seen, which is what drives the poster's view count.</summary>
    Task<ApiResult> ViewStatusAsync(Guid statusId, CancellationToken ct = default);
    Task<ApiResult<CallSignalDto>> StartCallAsync(Guid chatId, int type, CancellationToken ct = default);
    Task<ApiResult> SendCallSignalAsync(CallSignalDto signal, CancellationToken ct = default);
    Task<ApiResult> AnswerCallAsync(Guid callId, bool accepted, CancellationToken ct = default);
    Task<ApiResult> EndCallAsync(Guid callId, CancellationToken ct = default);

    Task<ThemeDto?> GetActiveThemeAsync(CancellationToken ct = default);
}

/// <summary>Holds the tokens for the lifetime of a session, in memory only.</summary>
public class TokenStore
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public UserDto? User { get; set; }

    public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken) && ExpiresAt > DateTimeOffset.UtcNow;

    /// <summary>True a minute before real expiry, so a refresh happens before a call fails.</summary>
    public bool NeedsRefresh => AccessToken is not null && ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);

    public void Apply(LoginResponse response)
    {
        AccessToken = response.AccessToken;
        RefreshToken = response.RefreshToken;
        ExpiresAt = response.ExpiresAt;
        User = response.User;
    }

    public void Clear()
    {
        AccessToken = null;
        RefreshToken = null;
        ExpiresAt = null;
        User = null;
    }
}
