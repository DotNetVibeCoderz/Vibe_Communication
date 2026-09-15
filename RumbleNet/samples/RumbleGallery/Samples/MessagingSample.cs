using Rumble.Net;
using Rumble.Net.Events;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Messaging", "Channel and private messages", "Send text to a channel and to a user, and react to replies.",
    Description = "ChannelMessageReceived fires for channel messages (onChannelMessage) and PrivateMessageReceived for direct messages. Messages may contain HTML; PlainText strips it.",
    Order = 30)]
public static class MessagingSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var client = new RumbleClient(ctx.CreateOptions("Chatter"));
        var replies = new TaskCompletionSource<TextMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        client.TextMessageReceived += (_, message) =>
        {
            var where = message.IsPrivate ? "private" : string.Join(", ", message.Channels.Select(c => c.Name));
            ctx.Log($"[{where}] {message.Sender?.Name}: {message.PlainText}");
            replies.TrySetResult(message);
        };

        await client.ConnectAsync(ctx.Token);
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId, ctx.Token);

        client.SendChannelMessage("Hello <b>Lobby</b>!");
        ctx.Note("sent channel message");
        await replies.Task.WaitAsync(TimeSpan.FromSeconds(5), ctx.Token);

        replies = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var echoBot = client.Server.FindUser("EchoBot")!;
        client.SendPrivateMessage(echoBot, "Are you a bot?");
        ctx.Note($"sent private message to {echoBot.Name}");
        var reply = await replies.Task.WaitAsync(TimeSpan.FromSeconds(5), ctx.Token);

        ctx.Success($"Got a {(reply.IsPrivate ? "private" : "channel")} reply at {reply.Timestamp:HH:mm:ss}");
    }
}
