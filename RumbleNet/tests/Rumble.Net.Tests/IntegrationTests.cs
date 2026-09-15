using Microsoft.Extensions.DependencyInjection;
using Rumble.Net.Audio;
using Rumble.Net.Bots;
using Rumble.Net.DependencyInjection;
using Rumble.Net.Events;
using Rumble.Net.Plugins;
using Rumble.Net.Testing;

namespace Rumble.Net.Tests;

/// <summary>End-to-end tests against the embedded mock Mumble server.</summary>
public class IntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static RumbleClientOptions Options(MockMumbleServer server, string name)
    {
        var options = server.CreateClientOptions(name);
        options.PingInterval = TimeSpan.FromMilliseconds(300);
        options.PingTimeout = TimeSpan.FromSeconds(5);
        options.ReconnectMinDelay = TimeSpan.FromMilliseconds(100);
        options.ReconnectMaxDelay = TimeSpan.FromMilliseconds(500);
        return options;
    }

    [Fact]
    public async Task Connects_navigates_chats_and_manages_channels()
    {
        using var server = MockMumbleServer.Start();
        await using var client = new RumbleClient(Options(server, "dotnet"));
        var history = new ChatHistoryPlugin();
        await client.Plugins.AddAsync(history, TestContext.Current.CancellationToken);

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(client.IsConnected);
        Assert.Equal("dotnet", client.Self!.Name);
        var lobby = client.Channels.Single(c => c.Name == "Lobby");
        Assert.Contains(lobby.Users, u => u.Name == "EchoBot");
        Assert.Equal("Root/Lobby", lobby.Path);

        var moved = client.WaitForEventAsync<UserMovedEvent>(m => m.Session == client.Self.Session, Timeout);
        await client.JoinChannelAsync("Lobby", TestContext.Current.CancellationToken);
        await moved;
        Assert.Equal(lobby.Id, client.Self.ChannelId);

        var reply = new TaskCompletionSource<TextMessage>();
        client.ChannelMessageReceived += (_, m) => reply.TrySetResult(m);
        client.PrivateMessageReceived += (_, m) => reply.TrySetResult(m);
        client.SendChannelMessage("halo dari .NET");
        var echoed = await reply.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("echo: halo dari .NET", echoed.Message);
        Assert.Equal("EchoBot", echoed.Sender!.Name);
        Assert.Contains(history.Messages, m => m.Message == "echo: halo dari .NET");

        var room = await client.CreateChannelAsync(client.Server.Root!, "Dotnet Room", temporary: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Dotnet Room", room.Name);
        Assert.Contains(client.Server.Root!.Children, c => c.Id == room.Id);
        await client.RemoveChannelAsync(room, TestContext.Current.CancellationToken);
        Assert.Null(client.Server.GetChannel(room.Id));

        var stats = await client.GetUserStatsAsync(client.Server.GetUser(MockMumbleServer.EchoBotSession)!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(stats.Opus);

        var snapshot = client.GetSnapshot();
        Assert.True(snapshot.Synchronized);

        await client.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Sends_and_receives_voice()
    {
        using var server = MockMumbleServer.Start();
        var options = Options(server, "talker");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.Continuous;
        await using var client = new RumbleClient(options);

        var frames = 0;
        var loudest = -96f;
        client.Audio.AudioFrameReceived += (in AudioFrame frame) =>
        {
            if (frame.Session == MockMumbleServer.EchoBotSession && !frame.IsConcealed)
            {
                Interlocked.Increment(ref frames);
                loudest = Math.Max(loudest, frame.LevelDb);
            }
        };
        var filterCalls = 0;
        client.Audio.AddCaptureFilter(new DelegateAudioFilter(s => Interlocked.Increment(ref filterCalls)));

        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId, TestContext.Current.CancellationToken);

        var talking = client.WaitForEventAsync<UserTalkingEvent>(t => t.Session == MockMumbleServer.EchoBotSession && t.Talking, Timeout);
        await AudioPlayer.PlayAsync(client, ToneGenerator.Sine(440, TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);
        await talking;

        var deadline = DateTime.UtcNow + Timeout;
        while (frames < 30 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(frames >= 30, $"received {frames} echo frames");
        Assert.True(loudest > -20, $"loudest frame {loudest} dBFS");
        Assert.True(filterCalls >= 50, $"capture filter ran {filterCalls} times");
    }

    [Fact]
    public async Task Reconnects_after_connection_loss()
    {
        using var server = MockMumbleServer.Start();
        await using var client = new RumbleClient(Options(server, "phoenix"));
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        var lost = client.WaitForEventAsync<DisconnectedEvent>(d => d.WillReconnect, Timeout);
        var back = client.WaitForEventAsync<ConnectedEvent>(timeout: Timeout);
        server.DropAllConnections();
        await lost;
        await back;
        Assert.True(client.IsConnected);
        Assert.NotNull(client.Self);
    }

    [Fact]
    public async Task Wrong_password_throws_connection_exception()
    {
        using var server = MockMumbleServer.Start(password: "secret");
        var options = Options(server, "intruder");
        options.Password = "wrong";
        await using var client = new RumbleClient(options);
        var ex = await Assert.ThrowsAsync<RumbleConnectionException>(() => client.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal("WrongServerPw", ex.RejectType);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Unreachable_server_fails_fast()
    {
        await using var client = new RumbleClient(new RumbleClientOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Username = "nobody",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        await Assert.ThrowsAsync<RumbleConnectionException>(() => client.ConnectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Queries_server_without_connecting()
    {
        using var server = MockMumbleServer.Start();
        var info = await RumbleClient.QueryServerAsync(server.Host, server.Port, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("1.5.0", info.Version);
        Assert.Equal(100u, info.MaxUsers);
    }

    [Fact]
    public async Task Bot_commands_reply()
    {
        using var server = MockMumbleServer.Start();
        await using var bot = new TestBot(Options(server, "CommandBot"));
        await bot.StartAsync(TestContext.Current.CancellationToken);

        await using var user = new RumbleClient(Options(server, "human"));
        await user.ConnectAsync(TestContext.Current.CancellationToken);
        var reply = new TaskCompletionSource<string>();
        user.PrivateMessageReceived += (_, m) =>
        {
            if (m.Sender?.Name == "CommandBot")
            {
                reply.TrySetResult(m.PlainText);
            }
        };
        user.SendPrivateMessage(user.Server.FindUser("CommandBot")!, "!add 2 40");
        Assert.Equal("42", await reply.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        reply = new TaskCompletionSource<string>();
        user.SendPrivateMessage(user.Server.FindUser("CommandBot")!, "!add two");
        Assert.Equal("Usage: !add <a> <b>", await reply.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        await bot.StopAsync();
    }

    [Fact]
    public async Task Dependency_injection_registers_client()
    {
        var services = new ServiceCollection();
        services.AddRumbleClient(o => o.Username = "di-user");
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<RumbleClient>();
        Assert.Equal("di-user", client.Options.Username);
    }

    private sealed class TestBot(RumbleClientOptions options) : RumbleBot(options)
    {
        [BotCommand("add", Description = "Adds two numbers", Usage = "<a> <b>")]
        public void Add(BotCommandContext context)
            => context.Reply((int.Parse(context.Arguments[0]) + int.Parse(context.Arguments[1])).ToString());
    }
}
