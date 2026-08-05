using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Client.Core;

/// <summary>
/// SignalR transport — the default. Everything the server pushes arrives on the same connection
/// the client already uses for its calls, so there is no polling and no second socket.
///
/// Login still goes over REST: the hub itself requires a token, so one has to exist first.
/// </summary>
public class SignalRTelepatiClient(
    RestTelepatiClient rest,
    ClientOptions options,
    TokenStore tokens,
    ILogger<SignalRTelepatiClient> logger) : ITelepatiClient
{
    private HubConnection? _connection;

    public string TransportName => "SignalR";
    public bool IsConnected => _connection?.State == HubConnectionState.Connected;
    public UserDto? CurrentUser => tokens.User;

    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageEdited;
    public event Action<Guid, Guid>? MessageDeleted;
    public event Action<TypingNotification>? TypingChanged;
    public event Action<PresenceNotification>? PresenceChanged;
    public event Action<CallSignalDto>? CallSignalReceived;
    public event Action<ChatDto>? ChatUpdated;
    public event Action<bool>? ConnectionStateChanged;

    public Task<LoginResponse> LoginAsync(string usernameOrEmail, string password, string? twoFactorCode = null, CancellationToken ct = default) =>
        rest.LoginAsync(usernameOrEmail, password, twoFactorCode, ct);

    public Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        rest.RegisterAsync(request, ct);

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await DisconnectAsync(ct);
        await rest.LogoutAsync(ct);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return;

        var builder = new HubConnectionBuilder()
            .WithUrl($"{options.ServerUrl.TrimEnd('/')}/hubs/telepati", httpOptions =>
            {
                // The hub takes the token from a factory so a reconnect always uses the current one.
                httpOptions.AccessTokenProvider = () => Task.FromResult(tokens.AccessToken);
            })
            // Measured ~20% smaller than the JSON protocol on a 50-message page, and it skips
            // string parsing on both ends. The server keeps JSON registered as the fallback.
            .AddMessagePackProtocol();

        if (options.AutoReconnect)
        {
            builder.WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(options.ReconnectDelaySeconds),
                TimeSpan.FromSeconds(options.ReconnectDelaySeconds * 2),
                TimeSpan.FromSeconds(options.ReconnectDelaySeconds * 4)
            });
        }

        _connection = builder.Build();
        RegisterHandlers(_connection);

        _connection.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            if (error is not null) logger.LogWarning(error, "SignalR connection closed.");
            ConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        await _connection.StartAsync(ct);
        ConnectionStateChanged?.Invoke(true);
    }

    private void RegisterHandlers(HubConnection connection)
    {
        connection.On<MessageDto>("MessageReceived", message => MessageReceived?.Invoke(message));
        connection.On<MessageDto>("MessageEdited", message => MessageEdited?.Invoke(message));
        connection.On<MessageDeletedPayload>("MessageDeleted", payload => MessageDeleted?.Invoke(payload.ChatId, payload.MessageId));
        connection.On<TypingNotification>("TypingChanged", notification => TypingChanged?.Invoke(notification));
        connection.On<PresenceNotification>("PresenceChanged", notification => PresenceChanged?.Invoke(notification));
        connection.On<CallSignalDto>("CallSignal", signal => CallSignalReceived?.Invoke(signal));
        connection.On<ChatDto>("ChatUpdated", chat => ChatUpdated?.Invoke(chat));
        connection.On<ReceiptPayload>("ReceiptUpdated", _ => { /* read state is refreshed with the chat list */ });
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_connection is null) return;

        await _connection.DisposeAsync();
        _connection = null;
        ConnectionStateChanged?.Invoke(false);
    }

    // Hub invocations, with the REST client as the fallback whenever the socket is down.

    public Task<PagedResult<ChatDto>> GetChatsAsync(int page = 1, int pageSize = 30, bool includeArchived = false, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<PagedResult<ChatDto>>("GetChats", page, pageSize, includeArchived, ct)
            : rest.GetChatsAsync(page, pageSize, includeArchived, ct);

    public Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default) => rest.GetChatAsync(chatId, ct);

    public Task<ChatDto?> CreateChatAsync(CreateChatRequest request, CancellationToken ct = default) => rest.CreateChatAsync(request, ct);

    public Task<ChatDto?> OpenDirectChatAsync(Guid otherUserId, CancellationToken ct = default) => rest.OpenDirectChatAsync(otherUserId, ct);

    public Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default) => rest.GetMembersAsync(chatId, ct);

    public Task<PagedResult<MessageDto>> GetMessagesAsync(Guid chatId, int page = 1, int pageSize = 50, Guid? beforeMessageId = null, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<PagedResult<MessageDto>>("GetMessages", chatId, page, pageSize, beforeMessageId, ct)
            : rest.GetMessagesAsync(chatId, page, pageSize, beforeMessageId, ct);

    public async Task<MessageDto?> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        if (!IsConnected) return await rest.SendMessageAsync(request, ct);

        var result = await _connection!.InvokeAsync<ApiResult<MessageDto>>("SendMessage", request, ct);
        return result.Success ? result.Data : null;
    }

    public async Task<MessageDto?> EditMessageAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        if (!IsConnected) return await rest.EditMessageAsync(messageId, content, ct);

        var result = await _connection!.InvokeAsync<ApiResult<MessageDto>>("EditMessage", new EditMessageRequest(messageId, content), ct);
        return result.Success ? result.Data : null;
    }

    public Task<ApiResult> DeleteMessageAsync(Guid messageId, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("DeleteMessage", messageId, ct)
            : rest.DeleteMessageAsync(messageId, ct);

    public Task<ApiResult> ReactAsync(Guid messageId, string emoji, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("React", new ReactionRequest(messageId, emoji), ct)
            : rest.ReactAsync(messageId, emoji, ct);

    public Task<ApiResult> MarkReadAsync(Guid chatId, Guid lastReadMessageId, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("MarkRead", new ReadReceiptRequest(chatId, lastReadMessageId), ct)
            : rest.MarkReadAsync(chatId, lastReadMessageId, ct);

    public Task<ApiResult> SetTypingAsync(Guid chatId, bool isTyping, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("SetTyping", chatId, isTyping, ct)
            : rest.SetTypingAsync(chatId, isTyping, ct);

    public Task<ApiResult> SetPresenceAsync(int presence, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("SetPresence", presence, ct)
            : rest.SetPresenceAsync(presence, ct);

    public Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default) => rest.GetContactsAsync(ct);

    public Task<IReadOnlyList<UserDto>> SearchUsersAsync(ContactSearchRequest request, CancellationToken ct = default) => rest.SearchUsersAsync(request, ct);

    public Task<ApiResult> AddContactAsync(Guid userId, string? alias = null, CancellationToken ct = default) => rest.AddContactAsync(userId, alias, ct);

    public Task<IReadOnlyList<StatusPostDto>> GetStatusFeedAsync(CancellationToken ct = default) => rest.GetStatusFeedAsync(ct);

    public Task<ApiResult> ViewStatusAsync(Guid statusId, CancellationToken ct = default) => rest.ViewStatusAsync(statusId, ct);

    public async Task<ApiResult<CallSignalDto>> StartCallAsync(Guid chatId, int type, CancellationToken ct = default) =>
        IsConnected
            ? await _connection!.InvokeAsync<ApiResult<CallSignalDto>>("StartCall", new StartCallRequest(chatId, type), ct)
            : await rest.StartCallAsync(chatId, type, ct);

    public Task<ApiResult> SendCallSignalAsync(CallSignalDto signal, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("SendCallSignal", signal, ct)
            : rest.SendCallSignalAsync(signal, ct);

    public Task<ApiResult> AnswerCallAsync(Guid callId, bool accepted, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("AnswerCall", callId, accepted, ct)
            : rest.AnswerCallAsync(callId, accepted, ct);

    public Task<ApiResult> EndCallAsync(Guid callId, CancellationToken ct = default) =>
        IsConnected
            ? _connection!.InvokeAsync<ApiResult>("EndCall", callId, ct)
            : rest.EndCallAsync(callId, ct);

    public Task<ThemeDto?> GetActiveThemeAsync(CancellationToken ct = default) => rest.GetActiveThemeAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }

    private record MessageDeletedPayload(Guid ChatId, Guid MessageId);
    private record ReceiptPayload(Guid ChatId, Guid MessageId, Guid UserId, int State);
}
