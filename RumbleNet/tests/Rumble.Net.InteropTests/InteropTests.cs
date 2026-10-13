using Rumble.Net.Audio;
using Rumble.Net.Events;

namespace Rumble.Net.InteropTests;

/// <summary>
/// Runs against a real Mumble server (see <see cref="InteropServer"/>). CI starts official
/// mumble-server containers for several versions; locally the suite skips unless you point it at
/// a server.
/// </summary>
public class InteropTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Server_answers_the_udp_query()
    {
        InteropServer.SkipIfUnavailable();
        var info = await RumbleClient.QueryServerAsync(InteropServer.Host!, InteropServer.Port, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage($"{InteropServer.Label}: Mumble {info.Version}, {info.Users}/{info.MaxUsers} users, {info.PingMs:0} ms");
        Assert.StartsWith("1.", info.Version);
        Assert.True(info.MaxUsers > 0);
    }

    [Fact]
    public async Task Connects_authenticates_and_synchronizes()
    {
        InteropServer.SkipIfUnavailable();
        await using var client = new RumbleClient(InteropServer.Options("sync"));
        string? fingerprint = null;
        client.ServerCertificateReceived += (_, c) => fingerprint = c.Sha1Fingerprint;

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.True(client.IsConnected);
        Assert.NotNull(client.Self);
        Assert.NotNull(client.Server.Root);
        Assert.Equal(40, fingerprint?.Length);
        Assert.True(client.Server.Info.Opus, "a modern Mumble server negotiates Opus");

        // Voice runs over encrypted UDP once the first ping round-trips.
        await WaitUntilAsync(() => client.Server.UdpActive, TimeSpan.FromSeconds(10));
        Assert.True(client.Server.UdpActive, "UDP voice channel did not come up");
        TestContext.Current.SendDiagnosticMessage(
            $"{InteropServer.Label}: {client.Server.Info.Version}, tcp {client.Server.TcpPingMs:0.0} ms, udp {client.Server.UdpPingMs:0.0} ms");

        await client.DisconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Two_clients_exchange_text_messages()
    {
        InteropServer.SkipIfUnavailable();
        await using var alice = new RumbleClient(InteropServer.Options("alice"));
        await using var bob = new RumbleClient(InteropServer.Options("bob"));
        var received = new TaskCompletionSource<TextMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.TextMessageReceived += (_, m) => received.TrySetResult(m);

        await alice.ConnectAsync(TestContext.Current.CancellationToken);
        await bob.ConnectAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => alice.Server.GetUser(bob.Self!.Session) is not null, Timeout);

        var target = alice.Server.GetUser(bob.Self!.Session)!;
        alice.SendPrivateMessage(target, "halo from Rumble.Net interop");

        var message = await received.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal("halo from Rumble.Net interop", message.PlainText);
        Assert.Equal(alice.Self!.Session, message.Sender?.Session);
        Assert.True(message.IsPrivate);
    }

    [Fact]
    public async Task Two_clients_exchange_voice_over_udp()
    {
        InteropServer.SkipIfUnavailable();
        var speakerOptions = InteropServer.Options("speaker");
        speakerOptions.Audio.Mode = AudioMode.Headless;
        speakerOptions.Audio.TransmitMode = TransmitMode.Continuous;
        var listenerOptions = InteropServer.Options("listener");
        listenerOptions.Audio.Mode = AudioMode.Headless;

        await using var speaker = new RumbleClient(speakerOptions);
        await using var listener = new RumbleClient(listenerOptions);

        var frames = 0;
        var loudest = -96f;
        listener.Audio.AudioFrameReceived += (in AudioFrame frame) =>
        {
            if (!frame.IsConcealed)
            {
                Interlocked.Increment(ref frames);
                loudest = MathF.Max(loudest, frame.LevelDb);
            }
        };

        await speaker.ConnectAsync(TestContext.Current.CancellationToken);
        await listener.ConnectAsync(TestContext.Current.CancellationToken);

        // Both land in the root channel, so the listener hears the speaker.
        await WaitUntilAsync(() => listener.Server.GetUser(speaker.Self!.Session)?.ChannelId == listener.Self!.ChannelId, Timeout);
        await WaitUntilAsync(() => speaker.Server.UdpActive && listener.Server.UdpActive, TimeSpan.FromSeconds(10));

        var talking = listener.WaitForEventAsync<UserTalkingEvent>(
            t => t.Session == speaker.Self!.Session && t.Talking, Timeout, TestContext.Current.CancellationToken);
        await AudioPlayer.PlayAsync(speaker, ToneGenerator.Sine(440, TimeSpan.FromSeconds(1.5)), TestContext.Current.CancellationToken);
        await talking;
        await WaitUntilAsync(() => frames >= 100, Timeout);

        TestContext.Current.SendDiagnosticMessage(
            $"{InteropServer.Label}: {frames} decoded frames, peak {loudest:0.0} dBFS, transport {(listener.Server.UdpActive ? "UDP" : "TCP")}");
        Assert.True(frames >= 100, $"only {frames} frames of 150 arrived");
        Assert.True(loudest > -30, $"audio was too quiet: {loudest:0.0} dBFS");
    }

    [Fact]
    public async Task Server_loopback_returns_our_own_voice()
    {
        InteropServer.SkipIfUnavailable();
        var options = InteropServer.Options("loopback");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.Continuous;
        await using var client = new RumbleClient(options);

        var frames = 0;
        client.Audio.AudioFrameReceived += (in AudioFrame frame) =>
        {
            if (frame.Session == client.Self?.Session && !frame.IsConcealed)
            {
                Interlocked.Increment(ref frames);
            }
        };

        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => client.Server.UdpActive, TimeSpan.FromSeconds(10));

        client.Audio.VoiceTarget = 31;   // Mumble's server loopback: only we hear it
        await AudioPlayer.PlayAsync(client, ToneGenerator.Sine(660, TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => frames >= 60, Timeout);

        Assert.True(frames >= 60, $"loopback returned only {frames} frames");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
