using Microsoft.AspNetCore.SignalR;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Server.Realtime;

/// <summary>
/// Fans every push out to both live transports. A user may hold a SignalR connection on the web
/// and a gRPC stream on the desktop at the same time, and neither should miss an event — so the
/// domain services publish once and this decides who actually gets it.
/// </summary>
public class CompositeRealtimeNotifier(
    IHubContext<TelepatiHub> hub,
    GrpcSubscriberRegistry grpcSubscribers) : IRealtimeNotifier
{
    public Task MessageSentAsync(MessageDto message, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("MessageReceived", message, recipientIds,
            () => GrpcEventFactory.Message("message", message), ct);

    public Task MessageEditedAsync(MessageDto message, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("MessageEdited", message, recipientIds,
            () => GrpcEventFactory.Message("message_edited", message), ct);

    public Task MessageDeletedAsync(Guid chatId, Guid messageId, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("MessageDeleted", new { chatId, messageId }, recipientIds,
            () => GrpcEventFactory.Deleted(messageId), ct);

    public Task ReceiptAsync(Guid chatId, Guid messageId, Guid userId, int state, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("ReceiptUpdated", new { chatId, messageId, userId, state }, recipientIds,
            () => GrpcEventFactory.Receipt(chatId, messageId, userId, state), ct);

    public Task TypingAsync(TypingNotification notification, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("TypingChanged", notification, recipientIds,
            () => GrpcEventFactory.Typing(notification), ct);

    public Task PresenceAsync(PresenceNotification notification, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("PresenceChanged", notification, recipientIds,
            () => GrpcEventFactory.Presence(notification), ct);

    public Task CallSignalAsync(CallSignalDto signal, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("CallSignal", signal, recipientIds,
            () => GrpcEventFactory.Call(signal), ct);

    public Task ChatUpdatedAsync(ChatDto chat, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default) =>
        DispatchAsync("ChatUpdated", chat, recipientIds,
            () => GrpcEventFactory.Chat(chat), ct);

    /// <summary>
    /// The gRPC envelope is built lazily: when no gRPC client is subscribed — the common case
    /// for a pure web deployment — the protobuf conversion never runs.
    /// </summary>
    private async Task DispatchAsync(
        string signalRMethod, object payload, IReadOnlyList<Guid> recipientIds,
        Func<Shared.Grpc.RealtimeEvent> grpcEventFactory, CancellationToken ct)
    {
        var groups = recipientIds.Select(TelepatiHub.UserGroup).ToList();
        if (groups.Count > 0)
        {
            await hub.Clients.Groups(groups).SendAsync(signalRMethod, payload, ct);
        }

        if (grpcSubscribers.HasSubscribers(recipientIds))
        {
            await grpcSubscribers.PublishAsync(recipientIds, grpcEventFactory(), ct);
        }
    }
}
