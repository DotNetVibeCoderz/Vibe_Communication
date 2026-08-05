using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;
using Telepati.Shared.Grpc;

namespace Telepati.Client.Core;

/// <summary>
/// gRPC transport. Realtime arrives on the server-streaming <c>Subscribe</c> call, which is read
/// by a background loop that re-establishes itself if the stream drops. Binary framing makes it
/// the cheapest option on the wire, which matters most on mobile.
/// </summary>
public class GrpcTelepatiClient : ITelepatiClient
{
    private readonly RestTelepatiClient _rest;
    private readonly ClientOptions _options;
    private readonly TokenStore _tokens;
    private readonly ILogger<GrpcTelepatiClient> _logger;

    private GrpcChannel? _channel;
    private TelepatiService.TelepatiServiceClient? _client;
    private CancellationTokenSource? _streamCts;

    public GrpcTelepatiClient(RestTelepatiClient rest, ClientOptions options, TokenStore tokens, ILogger<GrpcTelepatiClient> logger)
    {
        _rest = rest;
        _options = options;
        _tokens = tokens;
        _logger = logger;
    }

    public string TransportName => "gRPC";
    public bool IsConnected { get; private set; }
    public UserDto? CurrentUser => _tokens.User;

    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageEdited;
    public event Action<Guid, Guid>? MessageDeleted;
    public event Action<TypingNotification>? TypingChanged;
    public event Action<PresenceNotification>? PresenceChanged;
    public event Action<CallSignalDto>? CallSignalReceived;
    public event Action<ChatDto>? ChatUpdated;
    public event Action<bool>? ConnectionStateChanged;

    public Task<LoginResponse> LoginAsync(string usernameOrEmail, string password, string? twoFactorCode = null, CancellationToken ct = default) =>
        _rest.LoginAsync(usernameOrEmail, password, twoFactorCode, ct);

    public Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        _rest.RegisterAsync(request, ct);

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await DisconnectAsync(ct);
        await _rest.LogoutAsync(ct);
    }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return Task.CompletedTask;

        _channel = GrpcChannel.ForAddress(_options.ServerUrl);
        _client = new TelepatiService.TelepatiServiceClient(_channel);

        _streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = SubscribeLoopAsync(_streamCts.Token);

        IsConnected = true;
        ConnectionStateChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Holds the realtime stream open, reconnecting with a fixed backoff when it breaks. Without
    /// this loop a single network blip would silently end all push delivery.
    /// </summary>
    private async Task SubscribeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var call = _client!.Subscribe(new SubscribeRequest(), Headers(), cancellationToken: ct);

                await foreach (var realtimeEvent in call.ResponseStream.ReadAllAsync(ct))
                {
                    Dispatch(realtimeEvent);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "gRPC realtime stream dropped; reconnecting.");
                ConnectionStateChanged?.Invoke(false);

                if (!_options.AutoReconnect) return;
                await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), ct);
                ConnectionStateChanged?.Invoke(true);
            }
        }
    }

    private void Dispatch(RealtimeEvent e)
    {
        switch (e.Kind)
        {
            case "message" when e.Message is not null:
                MessageReceived?.Invoke(ToDto(e.Message));
                break;
            case "message_edited" when e.Message is not null:
                MessageEdited?.Invoke(ToDto(e.Message));
                break;
            case "message_deleted":
                if (Guid.TryParse(e.DeletedMessageId, out var deletedId))
                    MessageDeleted?.Invoke(Guid.Empty, deletedId);
                break;
            case "typing" when e.Typing is not null:
                TypingChanged?.Invoke(new TypingNotification(
                    Guid.Parse(e.Typing.ChatId), Guid.Parse(e.Typing.UserId), e.Typing.UserName, e.Typing.IsTyping));
                break;
            case "presence" when e.Presence is not null:
                PresenceChanged?.Invoke(new PresenceNotification(
                    Guid.Parse(e.Presence.UserId), e.Presence.Presence, e.Presence.LastSeenAt?.ToDateTimeOffset()));
                break;
            case "call" when e.Call is not null:
                CallSignalReceived?.Invoke(new CallSignalDto(
                    Guid.Parse(e.Call.CallId), Guid.Parse(e.Call.ChatId), Guid.Parse(e.Call.FromUserId),
                    Guid.TryParse(e.Call.ToUserId, out var to) ? to : null,
                    e.Call.Type, e.Call.SignalKind, e.Call.Payload));
                break;
            case "chat_updated" when e.Chat is not null:
                ChatUpdated?.Invoke(ToDto(e.Chat));
                break;
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;

        _channel?.Dispose();
        _channel = null;
        _client = null;

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(false);
        }
        return Task.CompletedTask;
    }

    // -- data -----------------------------------------------------------------

    public async Task<PagedResult<ChatDto>> GetChatsAsync(int page = 1, int pageSize = 30, bool includeArchived = false, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.GetChatsAsync(page, pageSize, includeArchived, ct);

        var response = await _client.GetChatsAsync(
            new GetChatsRequest { Page = page, PageSize = pageSize, IncludeArchived = includeArchived },
            Headers(), cancellationToken: ct);

        return new PagedResult<ChatDto>(response.Chats.Select(ToDto).ToList(), response.Total, page, pageSize);
    }

    public Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default) => _rest.GetChatAsync(chatId, ct);

    public Task<ChatDto?> CreateChatAsync(CreateChatRequest request, CancellationToken ct = default) => _rest.CreateChatAsync(request, ct);

    public Task<ChatDto?> OpenDirectChatAsync(Guid otherUserId, CancellationToken ct = default) => _rest.OpenDirectChatAsync(otherUserId, ct);

    public Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default) => _rest.GetMembersAsync(chatId, ct);

    public async Task<PagedResult<MessageDto>> GetMessagesAsync(Guid chatId, int page = 1, int pageSize = 50, Guid? beforeMessageId = null, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.GetMessagesAsync(chatId, page, pageSize, beforeMessageId, ct);

        var response = await _client.GetMessagesAsync(new GetMessagesRequest
        {
            ChatId = chatId.ToString(),
            Page = page,
            PageSize = pageSize,
            BeforeMessageId = beforeMessageId?.ToString() ?? string.Empty
        }, Headers(), cancellationToken: ct);

        return new PagedResult<MessageDto>(response.Messages.Select(ToDto).ToList(), response.Total, page, pageSize);
    }

    public async Task<MessageDto?> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.SendMessageAsync(request, ct);

        var grpcRequest = new GrpcSendMessageRequest
        {
            ChatId = request.ChatId.ToString(),
            Type = request.Type,
            Content = request.Content ?? string.Empty,
            ReplyToMessageId = request.ReplyToMessageId?.ToString() ?? string.Empty,
            ForwardedFromMessageId = request.ForwardedFromMessageId?.ToString() ?? string.Empty,
            Latitude = request.Latitude ?? 0,
            Longitude = request.Longitude ?? 0,
            ClientMessageId = request.ClientMessageId ?? string.Empty
        };
        grpcRequest.AttachmentIds.AddRange(request.AttachmentIds.Select(id => id.ToString()));
        grpcRequest.MentionedUserIds.AddRange(request.MentionedUserIds.Select(id => id.ToString()));

        try
        {
            return ToDto(await _client.SendMessageAsync(grpcRequest, Headers(), cancellationToken: ct));
        }
        catch (RpcException e)
        {
            _logger.LogWarning("Send failed over gRPC: {Detail}", e.Status.Detail);
            return null;
        }
    }

    public async Task<MessageDto?> EditMessageAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.EditMessageAsync(messageId, content, ct);

        try
        {
            var response = await _client.EditMessageAsync(
                new GrpcEditMessageRequest { MessageId = messageId.ToString(), Content = content },
                Headers(), cancellationToken: ct);
            return ToDto(response);
        }
        catch (RpcException e)
        {
            _logger.LogWarning("Edit failed over gRPC: {Detail}", e.Status.Detail);
            return null;
        }
    }

    public async Task<ApiResult> DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.DeleteMessageAsync(messageId, ct);

        var ack = await _client.DeleteMessageAsync(new MessageIdRequest { MessageId = messageId.ToString() }, Headers(), cancellationToken: ct);
        return ToResult(ack);
    }

    public async Task<ApiResult> ReactAsync(Guid messageId, string emoji, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.ReactAsync(messageId, emoji, ct);

        var ack = await _client.ReactAsync(
            new GrpcReactionRequest { MessageId = messageId.ToString(), Emoji = emoji }, Headers(), cancellationToken: ct);
        return ToResult(ack);
    }

    public async Task<ApiResult> MarkReadAsync(Guid chatId, Guid lastReadMessageId, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.MarkReadAsync(chatId, lastReadMessageId, ct);

        var ack = await _client.MarkReadAsync(new MarkReadRequest
        {
            ChatId = chatId.ToString(),
            LastReadMessageId = lastReadMessageId.ToString()
        }, Headers(), cancellationToken: ct);

        return ToResult(ack);
    }

    public async Task<ApiResult> SetTypingAsync(Guid chatId, bool isTyping, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.SetTypingAsync(chatId, isTyping, ct);

        var ack = await _client.SetTypingAsync(
            new TypingRequest { ChatId = chatId.ToString(), IsTyping = isTyping }, Headers(), cancellationToken: ct);
        return ToResult(ack);
    }

    public async Task<ApiResult> SetPresenceAsync(int presence, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.SetPresenceAsync(presence, ct);

        var ack = await _client.SetPresenceAsync(new PresenceRequest { Presence = presence }, Headers(), cancellationToken: ct);
        return ToResult(ack);
    }

    public Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default) => _rest.GetContactsAsync(ct);

    public Task<IReadOnlyList<UserDto>> SearchUsersAsync(ContactSearchRequest request, CancellationToken ct = default) => _rest.SearchUsersAsync(request, ct);

    public Task<ApiResult> AddContactAsync(Guid userId, string? alias = null, CancellationToken ct = default) => _rest.AddContactAsync(userId, alias, ct);

    public Task<IReadOnlyList<StatusPostDto>> GetStatusFeedAsync(CancellationToken ct = default) => _rest.GetStatusFeedAsync(ct);

    public Task<ApiResult> ViewStatusAsync(Guid statusId, CancellationToken ct = default) => _rest.ViewStatusAsync(statusId, ct);

    public Task<ApiResult<CallSignalDto>> StartCallAsync(Guid chatId, int type, CancellationToken ct = default) => _rest.StartCallAsync(chatId, type, ct);

    public async Task<ApiResult> SendCallSignalAsync(CallSignalDto signal, CancellationToken ct = default)
    {
        if (_client is null) return await _rest.SendCallSignalAsync(signal, ct);

        var ack = await _client.SendCallSignalAsync(new CallSignal
        {
            CallId = signal.CallId.ToString(),
            ChatId = signal.ChatId.ToString(),
            FromUserId = signal.FromUserId.ToString(),
            ToUserId = signal.ToUserId?.ToString() ?? string.Empty,
            Type = signal.Type,
            SignalKind = signal.SignalKind,
            Payload = signal.Payload ?? string.Empty
        }, Headers(), cancellationToken: ct);

        return ToResult(ack);
    }

    public Task<ApiResult> AnswerCallAsync(Guid callId, bool accepted, CancellationToken ct = default) => _rest.AnswerCallAsync(callId, accepted, ct);

    public Task<ApiResult> EndCallAsync(Guid callId, CancellationToken ct = default) => _rest.EndCallAsync(callId, ct);

    public Task<ThemeDto?> GetActiveThemeAsync(CancellationToken ct = default) => _rest.GetActiveThemeAsync(ct);

    // -- helpers --------------------------------------------------------------

    private Metadata Headers() =>
        _tokens.AccessToken is null ? [] : new Metadata { { "Authorization", $"Bearer {_tokens.AccessToken}" } };

    private static ApiResult ToResult(GrpcAck ack) =>
        ack.Success ? ApiResult.Ok() : ApiResult.Fail(ack.Error);

    private static MessageDto ToDto(GrpcMessage message) => new()
    {
        Id = Guid.Parse(message.Id),
        ChatId = Guid.Parse(message.ChatId),
        SenderId = Guid.Parse(message.SenderId),
        SenderName = message.SenderName,
        SenderAvatarUrl = Nullable(message.SenderAvatarUrl),
        Type = message.Type,
        Content = message.Content,
        IsEncrypted = message.IsEncrypted,
        ReplyToMessageId = Guid.TryParse(message.ReplyToMessageId, out var reply) ? reply : null,
        ReplyToPreview = Nullable(message.ReplyToPreview),
        ForwardedFromUserId = Guid.TryParse(message.ForwardedFromUserId, out var forwarded) ? forwarded : null,
        ForwardedFromName = Nullable(message.ForwardedFromName),
        IsPinned = message.IsPinned,
        IsEdited = message.IsEdited,
        IsBotMessage = message.IsBotMessage,
        DeliveryState = message.DeliveryState,
        CreatedAt = message.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
        Latitude = message.Latitude == 0 ? null : message.Latitude,
        Longitude = message.Longitude == 0 ? null : message.Longitude,
        Attachments = message.Attachments.Select(a => new AttachmentDto(
            Guid.Parse(a.Id), a.FileName, a.ContentType, a.SizeBytes, a.Url,
            Nullable(a.ThumbnailUrl), a.Width == 0 ? null : a.Width,
            a.Height == 0 ? null : a.Height, a.DurationSeconds == 0 ? null : a.DurationSeconds)).ToList(),
        Reactions = message.Reactions.Select(r => new ReactionDto(
            r.Emoji, r.Count, r.UserIds.Select(Guid.Parse).ToList())).ToList(),
        MentionedUserIds = message.MentionedUserIds.Select(Guid.Parse).ToList()
    };

    private static ChatDto ToDto(GrpcChat chat) => new()
    {
        Id = Guid.Parse(chat.Id),
        Type = chat.Type,
        Title = chat.Title,
        Description = Nullable(chat.Description),
        AvatarUrl = Nullable(chat.AvatarUrl),
        Handle = Nullable(chat.Handle),
        IsEncrypted = chat.IsEncrypted,
        IsPublic = chat.IsPublic,
        OnlyAdminsCanPost = chat.OnlyAdminsCanPost,
        MemberCount = chat.MemberCount,
        UnreadCount = chat.UnreadCount,
        IsMuted = chat.IsMuted,
        IsPinned = chat.IsPinned,
        IsArchived = chat.IsArchived,
        MyRole = chat.MyRole,
        LastMessage = chat.LastMessage is null ? null : ToDto(chat.LastMessage),
        LastMessageAt = chat.LastMessageAt?.ToDateTimeOffset()
    };

    private static string? Nullable(string value) => string.IsNullOrEmpty(value) ? null : value;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
