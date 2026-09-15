using Rumble.Net;
using Rumble.Net.Models;
using Rumble.Net.Plugins;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Plugins", "Write your own plugin", "Package reusable behavior: a greeter that welcomes users joining your channel.",
    Description = "Derive from RumblePlugin, subscribe in OnInitializeAsync and clean up in OnShutdownAsync. Plugins can combine events, commands and audio filters.",
    Order = 75)]
public static class CustomPluginSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var host = new RumbleClient(ctx.CreateOptions("Host"));
        await host.Plugins.AddAsync(new GreeterPlugin("Welcome to {channel}, {user}!", ctx), ctx.Token);
        await host.ConnectAsync(ctx.Token);
        await host.JoinChannelAsync("Lobby", ctx.Token);

        await using var visitor = new RumbleClient(ctx.CreateOptions("Visitor"));
        var greeted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        visitor.ChannelMessageReceived += (_, m) =>
        {
            if (m.Sender?.Name == "Host")
            {
                greeted.TrySetResult(m.PlainText);
            }
        };
        await visitor.ConnectAsync(ctx.Token);
        await visitor.JoinChannelAsync("Lobby", ctx.Token);

        ctx.Success($"Visitor saw: \"{await greeted.Task.WaitAsync(TimeSpan.FromSeconds(5), ctx.Token)}\"");
    }

    private sealed class GreeterPlugin(string template, SampleContext ctx) : RumblePlugin
    {
        public override string Name => "Greeter";

        protected override ValueTask OnInitializeAsync(CancellationToken cancellationToken)
        {
            Client.UserMoved += OnUserMoved;
            ctx.Note("Greeter plugin initialized");
            return ValueTask.CompletedTask;
        }

        protected override ValueTask OnShutdownAsync()
        {
            Client.UserMoved -= OnUserMoved;
            return ValueTask.CompletedTask;
        }

        private void OnUserMoved(object? sender, Rumble.Net.Events.UserMovedEventArgs e)
        {
            if (e.User.IsSelf || e.To is not Channel to || to.Id != Client.Self?.ChannelId)
            {
                return;
            }

            Client.SendChannelMessage(to, template.Replace("{channel}", to.Name).Replace("{user}", e.User.Name));
        }
    }
}
