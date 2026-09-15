using Rumble.Net;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Getting started", "Connect and explore the server", "Connect, wait for synchronization and print the channel tree.",
    Description = "ConnectAsync completes once the server has sent its full state. From then on client.Server is a live model you can read at any time.",
    Order = 10)]
public static class ConnectSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var client = new RumbleClient(ctx.CreateOptions("GalleryUser"));
        await client.ConnectAsync(ctx.Token);

        var info = client.Server.Info;
        ctx.Success($"Connected as {client.Self!.Name} (session {client.Self.Session})");
        ctx.Log($"Server {info.Version} · {client.Server.Users.Count} users · max {info.MaxBandwidth / 1000} kbit/s");
        ctx.Log($"Welcome: {Rumble.Net.Events.HtmlText.ToPlainText(info.WelcomeText)}");
        ctx.Log("");

        // Channels enumerate depth-first from the root; Depth gives the tree level.
        foreach (var channel in client.Channels)
        {
            var indent = new string(' ', channel.Depth * 2);
            ctx.Log($"{indent}# {channel.Name}");
            foreach (var user in channel.Users)
            {
                ctx.Log($"{indent}  - {user.Name}{(user.IsSelf ? " (you)" : "")}");
            }
        }

        await client.DisconnectAsync(ctx.Token);
    }
}
