using Rumble.Net;
using Rumble.Net.Bots;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Bots", "Build a command bot", "Create a bot with [BotCommand] methods, then talk to it from another client.",
    Description = "RumbleBot handles the lifecycle, routes !commands (quotes supported) and generates !help. Host it anywhere, or as an IHostedService with RumbleBotHostedService<T>.",
    Order = 80)]
public static class CommandBotSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var bot = new DiceBot(ctx.CreateOptions("DiceBot"));
        await bot.StartAsync(ctx.Token);
        ctx.Success("DiceBot is online");

        await using var player = new RumbleClient(ctx.CreateOptions("Player"));
        player.PrivateMessageReceived += (_, m) => ctx.Log($"{m.Sender?.Name}: {m.PlainText}");
        await player.ConnectAsync(ctx.Token);

        var diceBot = player.Server.FindUser("DiceBot")!;
        foreach (var command in new[] { "!help", "!roll 20", "!roll 6 3", "!choose \"pizza\" \"ramen\" \"nasi goreng\"", "!roll many" })
        {
            ctx.Note($"Player → {command}");
            player.SendPrivateMessage(diceBot, command);
            await Task.Delay(300, ctx.Token);
        }

        await bot.StopAsync();
    }

    private sealed class DiceBot(RumbleClientOptions options) : RumbleBot(options)
    {
        [BotCommand("roll", Description = "Rolls dice", Usage = "<sides> [count]")]
        public void Roll(BotCommandContext context)
        {
            var sides = int.Parse(context.Arguments[0]);
            var count = context.Arguments.Count > 1 ? int.Parse(context.Arguments[1]) : 1;
            var rolls = Enumerable.Range(0, Math.Clamp(count, 1, 10)).Select(_ => Random.Shared.Next(1, sides + 1)).ToList();
            context.Reply($"🎲 {string.Join(" + ", rolls)} = {rolls.Sum()}");
        }

        [BotCommand("choose", Description = "Picks one option", Usage = "\"a\" \"b\" …")]
        public void Choose(BotCommandContext context)
            => context.Reply($"I pick: {context.Arguments[Random.Shared.Next(context.Arguments.Count)]}");
    }
}
