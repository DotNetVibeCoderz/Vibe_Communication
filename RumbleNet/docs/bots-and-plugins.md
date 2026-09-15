# Bots and plugins

## Command bots (`Rumble.Net.Bots`)

```csharp
public sealed class DiceBot(RumbleClientOptions options) : RumbleBot(options)
{
    [BotCommand("roll", Description = "Rolls dice", Usage = "<sides> [count]", Aliases = ["r"])]
    public void Roll(BotCommandContext ctx)
    {
        var sides = int.Parse(ctx.Arguments[0]);
        ctx.Reply($"🎲 {Random.Shared.Next(1, sides + 1)}");
    }

    protected override Task OnUserJoinedAsync(User user, CancellationToken ct)
    {
        Client.SendPrivateMessage(user, "Welcome! Type !help");
        return Task.CompletedTask;
    }
}

await using var bot = new DiceBot(new RumbleClientOptions { Host = "voice.example.org", Username = "DiceBot" });
await bot.RunAsync(cancellationToken);
```

- `RumbleBot` sets `IsBot`, defaults to headless audio, and registers every `[BotCommand]` method. A method can take `(BotCommandContext)` or `(BotCommandContext, CancellationToken)` and return `void`, `Task` or `ValueTask`.
- Commands start with `!`, which you can change with `Commands.Prefix`. Double quotes group arguments. `!help` is built in.
- Handlers that throw `FormatException`, `ArgumentException` or an out-of-range error automatically reply with the command's usage text.
- `ctx.Reply` answers privately to private messages and in the channel otherwise.
- Hooks: `OnStartedAsync`, `OnStoppingAsync`, `OnMessageAsync` (for messages that aren't commands), `OnUserJoinedAsync`, `OnUserLeftAsync`, `OnUserMovedAsync`.

To host a bot inside ASP.NET Core or a worker service:

```csharp
builder.Services.AddSingleton(sp => new DiceBot(options));
builder.Services.AddHostedService<RumbleBotHostedService<DiceBot>>();
```

## AI chat

`AiChatBot` and `AiChatResponder` work with any `Microsoft.Extensions.AI` `IChatClient`:

```csharp
IChatClient chat = /* e.g. new OpenAI.Chat.ChatClient("model", key).AsIChatClient() or an Ollama client */;
await using var bot = new AiChatBot(options, chat, new AiChatOptions
{
    SystemPrompt = "You are the helpful host of our gaming server.",
    RequireMentionInChannels = true,   // in channels, reply only when the message mentions the bot's name
    MaxHistoryMessages = 20,
});
```

The bot keeps a separate history for each channel and each private conversation. `!forget` clears the history.

## Plugins

```csharp
public sealed class Greeter : RumblePlugin
{
    protected override ValueTask OnInitializeAsync(CancellationToken ct)
    {
        Client.UserMoved += OnMoved;
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnShutdownAsync()
    {
        Client.UserMoved -= OnMoved;
        return ValueTask.CompletedTask;
    }

    private void OnMoved(object? s, UserMovedEventArgs e)
    {
        if (e.To?.Id == Client.Self?.ChannelId && !e.User.IsSelf)
            Client.SendChannelMessage(e.To, $"Welcome, {e.User.Name}!");
    }
}

await client.Plugins.AddAsync(new Greeter());
```

The SDK includes two plugins:

- `ChatHistoryPlugin` keeps a bounded message history.
- `VoiceRecorderPlugin` writes each speaker to a separate WAV file.

Plugins are shut down when the client is disposed. A plugin can combine events, commands, `Audio.AddCaptureFilter` and `Audio.AudioFrameReceived`, so audio effects, bots and integrations all use the same model.
