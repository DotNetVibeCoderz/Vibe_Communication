using System.Collections.Concurrent;
using System.Threading.Channels;
using Telepati.Shared.Grpc;

namespace Telepati.Server.Realtime;

/// <summary>
/// Tracks the open <c>Subscribe</c> streams, one bounded channel per connection. A slow or
/// stalled consumer drops its oldest events instead of blocking the publisher — realtime
/// delivery must never be held up by one unresponsive client.
/// </summary>
public class GrpcSubscriberRegistry
{
    private const int ChannelCapacity = 256;

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<RealtimeEvent>>> _subscribers = new();

    public (Guid SubscriptionId, ChannelReader<RealtimeEvent> Reader) Subscribe(Guid userId)
    {
        var channel = Channel.CreateBounded<RealtimeEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var subscriptionId = Guid.CreateVersion7();
        _subscribers.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Channel<RealtimeEvent>>())[subscriptionId] = channel;

        return (subscriptionId, channel.Reader);
    }

    public void Unsubscribe(Guid userId, Guid subscriptionId)
    {
        if (!_subscribers.TryGetValue(userId, out var channels)) return;

        if (channels.TryRemove(subscriptionId, out var channel)) channel.Writer.TryComplete();
        if (channels.IsEmpty) _subscribers.TryRemove(userId, out _);
    }

    public bool HasSubscribers(IReadOnlyList<Guid> userIds) => userIds.Any(_subscribers.ContainsKey);

    public Task PublishAsync(IReadOnlyList<Guid> userIds, RealtimeEvent payload, CancellationToken ct = default)
    {
        foreach (var userId in userIds)
        {
            if (!_subscribers.TryGetValue(userId, out var channels)) continue;

            foreach (var channel in channels.Values)
            {
                channel.Writer.TryWrite(payload);
            }
        }

        return Task.CompletedTask;
    }

    public int ConnectionCount => _subscribers.Values.Sum(c => c.Count);
}
