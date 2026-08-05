using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Server.Realtime;

/// <summary>
/// The SignalR face of the server and the default transport for every client. Its method names
/// mirror the REST routes and the gRPC service so a client can switch transport without any
/// behavioural difference.
/// </summary>
[Authorize]
public class TelepatiHub(
    IChatService chats,
    IMessageService messages,
    IUserService users,
    ICallService calls,
    ChatOrchestrator orchestrator) : Hub
{
    /// <summary>Group name every push to a specific user is addressed to.</summary>
    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();

        // One group per user rather than per chat: a user may hold several devices, and
        // membership changes then need no group bookkeeping at all.
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        await users.SetPresenceAsync(userId, UserPresence.Online);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User!.GetUserId();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, UserGroup(userId));
        await users.SetPresenceAsync(userId, UserPresence.Offline);

        await base.OnDisconnectedAsync(exception);
    }

    public Task<PagedResult<ChatDto>> GetChats(int page, int pageSize, bool includeArchived) =>
        chats.GetChatsAsync(Context.User!.GetUserId(), page, pageSize, includeArchived);

    public Task<PagedResult<MessageDto>> GetMessages(Guid chatId, int page, int pageSize, Guid? beforeMessageId) =>
        messages.GetMessagesAsync(Context.User!.GetUserId(), chatId, page, pageSize, beforeMessageId);

    public Task<ApiResult<MessageDto>> SendMessage(SendMessageRequest request) =>
        orchestrator.SendAsync(Context.User!.GetUserId(), request);

    public Task<ApiResult<MessageDto>> EditMessage(EditMessageRequest request) =>
        messages.EditAsync(Context.User!.GetUserId(), request);

    public Task<ApiResult> DeleteMessage(Guid messageId) =>
        messages.DeleteAsync(Context.User!.GetUserId(), messageId);

    public Task<ApiResult> React(ReactionRequest request) =>
        messages.ReactAsync(Context.User!.GetUserId(), request);

    public Task<ApiResult> MarkRead(ReadReceiptRequest request) =>
        messages.MarkReadAsync(Context.User!.GetUserId(), request);

    public Task<ApiResult> SetTyping(Guid chatId, bool isTyping) =>
        messages.SetTypingAsync(Context.User!.GetUserId(), chatId, isTyping);

    public Task<ApiResult> SetPresence(int presence) =>
        users.SetPresenceAsync(Context.User!.GetUserId(), (UserPresence)presence);

    public Task<ApiResult> SendCallSignal(CallSignalDto signal) =>
        calls.RelaySignalAsync(Context.User!.GetUserId(), signal);

    public Task<ApiResult<CallSignalDto>> StartCall(StartCallRequest request) =>
        calls.StartAsync(Context.User!.GetUserId(), request);

    public Task<ApiResult> AnswerCall(Guid callId, bool accepted) =>
        calls.AnswerAsync(Context.User!.GetUserId(), callId, accepted);

    public Task<ApiResult> EndCall(Guid callId) =>
        calls.EndAsync(Context.User!.GetUserId(), callId);
}
