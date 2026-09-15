using Rumble.Net;
using Rumble.Net.Events;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Resilience", "Automatic reconnect", "Survive a dropped connection: heartbeat detection, backoff and resynchronization.",
    Description = "A supervisor task in the Rust core detects lost connections (socket errors or missed heartbeats), reconnects with exponential backoff and resynchronizes state. Your event handlers keep working.",
    Order = 90)]
public static class ReconnectSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var options = ctx.CreateOptions("Survivor");
        options.AutoReconnect = true;
        options.MaxReconnectAttempts = 10;

        await using var client = new RumbleClient(options);
        client.StateChanged += (_, state) => ctx.Note($"state → {state}");
        client.Disconnected += (_, e) => ctx.Warn($"Disconnected: {e.Reason} (will reconnect: {e.WillReconnect})");
        client.Reconnecting += (_, e) => ctx.Log($"Reconnect attempt {e.Attempt}…");
        client.Connected += (_, e) => ctx.Success($"Connected with session {e.Session}");

        await client.ConnectAsync(ctx.Token);
        await client.JoinChannelAsync("Lobby", ctx.Token);

        for (var round = 1; round <= 2; round++)
        {
            ctx.Log($"— Simulating network failure #{round} —");
            var back = client.WaitForEventAsync<ConnectedEvent>(timeout: TimeSpan.FromSeconds(15), cancellationToken: ctx.Token);
            ctx.Server.DropAllConnections();
            await back;
            ctx.Log($"Channel after reconnect: {client.Self?.Channel?.Name} (the server puts reconnecting users in Root)");
        }
    }
}
