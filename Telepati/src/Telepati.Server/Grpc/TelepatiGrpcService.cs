using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Server.Realtime;
using Telepati.Shared.Contracts;
using Telepati.Shared.Grpc;

namespace Telepati.Server.Grpc;

/// <summary>
/// The gRPC transport. Every method delegates to the same services the REST endpoints and the
/// SignalR hub use — this class only translates between protobuf and the shared DTOs.
/// </summary>
[Authorize]
public class TelepatiGrpcService(
    IAuthService auth,
    IChatService chats,
    IMessageService messages,
    IUserService users,
    IContactService contacts,
    ICallService calls,
    ChatOrchestrator orchestrator,
    GrpcSubscriberRegistry subscribers) : TelepatiService.TelepatiServiceBase
{
    [AllowAnonymous]
    public override async Task<GrpcLoginResponse> Login(GrpcLoginRequest request, ServerCallContext context)
    {
        var result = await auth.LoginAsync(new LoginRequest(
                request.UsernameOrEmail, request.Password,
                string.IsNullOrWhiteSpace(request.TwoFactorCode) ? null : request.TwoFactorCode,
                request.DeviceName, string.IsNullOrWhiteSpace(request.DeviceType) ? "grpc" : request.DeviceType,
                request.Platform),
            context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString(),
            "grpc",
            context.CancellationToken);

        return ToGrpc(result);
    }

    [AllowAnonymous]
    public override async Task<GrpcLoginResponse> Refresh(GrpcRefreshRequest request, ServerCallContext context) =>
        ToGrpc(await auth.RefreshAsync(request.RefreshToken, context.CancellationToken));

    public override async Task<GetChatsResponse> GetChats(GetChatsRequest request, ServerCallContext context)
    {
        var page = await chats.GetChatsAsync(
            UserId(context),
            request.Page <= 0 ? 1 : request.Page,
            request.PageSize <= 0 ? 30 : request.PageSize,
            request.IncludeArchived,
            context.CancellationToken);

        var response = new GetChatsResponse { Total = page.Total };
        response.Chats.AddRange(page.Items.Select(GrpcMappers.ToGrpc));
        return response;
    }

    public override async Task<GetMessagesResponse> GetMessages(GetMessagesRequest request, ServerCallContext context)
    {
        var page = await messages.GetMessagesAsync(
            UserId(context),
            request.ChatId.ToGuid(),
            request.Page <= 0 ? 1 : request.Page,
            request.PageSize <= 0 ? 50 : request.PageSize,
            request.BeforeMessageId.ToGuidOrNull(),
            context.CancellationToken);

        var response = new GetMessagesResponse { Total = page.Total };
        response.Messages.AddRange(page.Items.Select(GrpcMappers.ToGrpc));
        return response;
    }

    public override async Task<GrpcMessage> SendMessage(GrpcSendMessageRequest request, ServerCallContext context)
    {
        var result = await orchestrator.SendAsync(UserId(context), request.ToDto(), context.CancellationToken);
        if (!result.Success || result.Data is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Error ?? "Gagal mengirim pesan."));
        }

        var message = result.Data.ToGrpc();
        // Echoing the client id lets an optimistic UI reconcile its placeholder with the real row.
        message.ClientMessageId = request.ClientMessageId;
        return message;
    }

    public override async Task<GrpcMessage> EditMessage(GrpcEditMessageRequest request, ServerCallContext context)
    {
        var result = await messages.EditAsync(UserId(context),
            new EditMessageRequest(request.MessageId.ToGuid(), request.Content), context.CancellationToken);

        if (!result.Success || result.Data is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, result.Error ?? "Gagal mengubah pesan."));

        return result.Data.ToGrpc();
    }

    public override async Task<GrpcAck> DeleteMessage(MessageIdRequest request, ServerCallContext context) =>
        Ack(await messages.DeleteAsync(UserId(context), request.MessageId.ToGuid(), context.CancellationToken));

    public override async Task<GrpcAck> PinMessage(PinRequest request, ServerCallContext context) =>
        Ack(await messages.PinAsync(UserId(context), request.MessageId.ToGuid(), request.Pinned, context.CancellationToken));

    public override async Task<GrpcAck> React(GrpcReactionRequest request, ServerCallContext context) =>
        Ack(await messages.ReactAsync(UserId(context),
            new ReactionRequest(request.MessageId.ToGuid(), request.Emoji), context.CancellationToken));

    public override async Task<GrpcAck> MarkRead(MarkReadRequest request, ServerCallContext context) =>
        Ack(await messages.MarkReadAsync(UserId(context),
            new ReadReceiptRequest(request.ChatId.ToGuid(), request.LastReadMessageId.ToGuid()), context.CancellationToken));

    public override async Task<GrpcAck> SetTyping(TypingRequest request, ServerCallContext context) =>
        Ack(await messages.SetTypingAsync(UserId(context), request.ChatId.ToGuid(), request.IsTyping, context.CancellationToken));

    public override async Task<GrpcAck> SetPresence(PresenceRequest request, ServerCallContext context) =>
        Ack(await users.SetPresenceAsync(UserId(context), (UserPresence)request.Presence, context.CancellationToken));

    public override async Task<GetContactsResponse> GetContacts(EmptyRequest request, ServerCallContext context)
    {
        var list = await contacts.GetContactsAsync(UserId(context), context.CancellationToken);
        var response = new GetContactsResponse();
        response.Contacts.AddRange(list.Select(c => c.User.ToGrpc()));
        return response;
    }

    public override async Task<SearchUsersResponse> SearchUsers(SearchUsersRequest request, ServerCallContext context)
    {
        var found = await users.SearchAsync(UserId(context), new ContactSearchRequest
        {
            Query = request.Query,
            Mode = string.IsNullOrWhiteSpace(request.Mode) ? "username" : request.Mode,
            Latitude = request.Latitude == 0 ? null : request.Latitude,
            Longitude = request.Longitude == 0 ? null : request.Longitude,
            RadiusKm = request.RadiusKm <= 0 ? 10 : request.RadiusKm,
            Take = request.Take <= 0 ? 25 : request.Take
        }, context.CancellationToken);

        var response = new SearchUsersResponse();
        response.Users.AddRange(found.Select(GrpcMappers.ToGrpc));
        return response;
    }

    public override async Task<GrpcAck> SendCallSignal(CallSignal request, ServerCallContext context) =>
        Ack(await calls.RelaySignalAsync(UserId(context), request.ToDto(), context.CancellationToken));

    /// <summary>
    /// Long-lived realtime feed. The registry hands over a bounded channel, and this loop drains
    /// it until the client disconnects or the server shuts down.
    /// </summary>
    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<RealtimeEvent> responseStream, ServerCallContext context)
    {
        var userId = UserId(context);
        var (subscriptionId, reader) = subscribers.Subscribe(userId);

        try
        {
            await foreach (var realtimeEvent in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(realtimeEvent, context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal client disconnect.
        }
        finally
        {
            subscribers.Unsubscribe(userId, subscriptionId);
        }
    }

    // -- helpers --------------------------------------------------------------

    private static Guid UserId(ServerCallContext context)
    {
        var user = context.GetHttpContext()?.User;
        if (user is null) throw new RpcException(new Status(StatusCode.Unauthenticated, "Tidak terautentikasi."));
        return user.GetUserId();
    }

    private static GrpcAck Ack(ApiResult result) => new() { Success = result.Success, Error = result.Error ?? string.Empty };

    private static GrpcLoginResponse ToGrpc(LoginResponse result) => new()
    {
        Success = result.Success,
        AccessToken = result.AccessToken.OrEmpty(),
        RefreshToken = result.RefreshToken.OrEmpty(),
        ExpiresAt = result.ExpiresAt.ToTimestamp(),
        User = result.User?.ToGrpc(),
        TwoFactorRequired = result.TwoFactorRequired,
        Error = result.Error.OrEmpty()
    };
}
