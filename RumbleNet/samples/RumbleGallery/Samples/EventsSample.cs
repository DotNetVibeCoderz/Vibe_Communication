using Rumble.Net;
using Rumble.Net.Events;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Events", "Event hooks and the async event stream", "Use C# events like UserJoined, or consume every event with await foreach.",
    Description = "Events are raised in order on a background loop after the model is updated. ReadEventsAsync gives each consumer its own buffered stream — handy for pipelines and tests.",
    Order = 40)]
public static class EventsSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var watcher = new RumbleClient(ctx.CreateOptions("Watcher"));
        watcher.UserJoined += (_, user) => ctx.Log($"UserJoined: {user.Name}");
        watcher.UserLeft += (_, e) => ctx.Log($"UserLeft: {e.User?.Name}");
        watcher.UserMoved += (_, e) => ctx.Log($"UserMoved: {e.User.Name} → {e.To?.Name}");
        await watcher.ConnectAsync(ctx.Token);

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        var stream = Task.Run(async () =>
        {
            await foreach (var e in watcher.ReadEventsAsync(streamCts.Token))
            {
                if (e is not PingUpdatedEvent)
                {
                    ctx.Note($"stream: {e.GetType().Name}");
                }
            }
        });

        // A second client produces some activity.
        Task<UserLeftEvent> guestLeft;
        await using (var guest = new RumbleClient(ctx.CreateOptions("Guest")))
        {
            await guest.ConnectAsync(ctx.Token);
            await guest.JoinChannelAsync("AFK", ctx.Token);
            guest.SetComment("just visiting");
            await Task.Delay(300, ctx.Token);

            // Start waiting *before* the guest leaves, otherwise the event can arrive before we listen.
            var guestSession = guest.Self!.Session;
            guestLeft = watcher.WaitForEventAsync<UserLeftEvent>(e => e.Session == guestSession, TimeSpan.FromSeconds(10), ctx.Token);
        }

        await guestLeft;
        await streamCts.CancelAsync();
        try
        {
            await stream;
        }
        catch (OperationCanceledException)
        {
        }

        ctx.Success("Observed the guest's full lifecycle");
    }
}
