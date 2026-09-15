using Rumble.Net;
using Rumble.Net.Audio;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Voice", "Send voice and receive audio frames", "Stream a melody in headless mode and meter the EchoBot playback.",
    Description = "Headless mode needs no audio hardware: you push 48 kHz PCM with SendPcm, and every decoded 10 ms frame arrives in AudioFrameReceived (onAudioFrame) on the audio thread.",
    Order = 50)]
public static class VoiceSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var options = ctx.CreateOptions("Singer");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.Continuous;
        options.Audio.Bitrate = 64_000;

        await using var client = new RumbleClient(options);

        var frames = 0;
        var peakDb = -96f;
        client.Audio.AudioFrameReceived += (in AudioFrame frame) =>
        {
            if (frame.Session != MockMumbleServer.EchoBotSession || frame.IsConcealed)
            {
                return;
            }

            Interlocked.Increment(ref frames);
            peakDb = Math.Max(peakDb, frame.LevelDb);
        };
        client.UserTalkingChanged += (_, e) => ctx.Note($"{e.User.Name} {(e.Talking ? "started" : "stopped")} talking");

        await client.ConnectAsync(ctx.Token);
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId, ctx.Token);

        // C major arpeggio, 250 ms per note.
        var melody = ToneGenerator.Melody(
            new[] { 261.63f, 329.63f, 392.00f, 523.25f, 392.00f, 329.63f, 261.63f }
                .Select(f => (f, TimeSpan.FromMilliseconds(250))));
        ctx.Log($"Streaming {melody.Length / 48_000.0:0.0} s of audio…");
        await AudioPlayer.PlayAsync(client, melody, ctx.Token);

        await Task.Delay(600, ctx.Token);
        ctx.Success($"Received {frames} decoded frames ({frames * 10} ms) from EchoBot, peak {peakDb:0.0} dBFS");
        ctx.Log($"Voice transport: {(client.Server.UdpActive ? "encrypted UDP" : "TCP tunnel")}, ping {client.Server.TcpPingMs:0.0} ms");
    }
}
