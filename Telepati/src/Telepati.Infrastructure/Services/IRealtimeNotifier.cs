using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

/// <summary>
/// Push side of realtime delivery. The domain services call this without knowing whether the
/// recipient is attached over SignalR or a gRPC stream — the server registers a fan-out
/// implementation that feeds both, so business logic stays transport-agnostic.
/// </summary>
public interface IRealtimeNotifier
{
    Task MessageSentAsync(MessageDto message, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task MessageEditedAsync(MessageDto message, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task MessageDeletedAsync(Guid chatId, Guid messageId, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task ReceiptAsync(Guid chatId, Guid messageId, Guid userId, int state, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task TypingAsync(TypingNotification notification, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task PresenceAsync(PresenceNotification notification, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task CallSignalAsync(CallSignalDto signal, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
    Task ChatUpdatedAsync(ChatDto chat, IReadOnlyList<Guid> recipientIds, CancellationToken ct = default);
}

/// <summary>Used by the admin app and tests, where nothing is listening for pushes.</summary>
public sealed class NullRealtimeNotifier : IRealtimeNotifier
{
    public Task MessageSentAsync(MessageDto m, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageEditedAsync(MessageDto m, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task MessageDeletedAsync(Guid c, Guid m, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReceiptAsync(Guid c, Guid m, Guid u, int s, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task TypingAsync(TypingNotification n, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task PresenceAsync(PresenceNotification n, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task CallSignalAsync(CallSignalDto s, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChatUpdatedAsync(ChatDto c, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
}
