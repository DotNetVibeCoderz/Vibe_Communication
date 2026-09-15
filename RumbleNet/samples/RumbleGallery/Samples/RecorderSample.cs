using Rumble.Net;
using Rumble.Net.Audio;
using Rumble.Net.Plugins;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Plugins", "Record voices to WAV files", "Use the VoiceRecorderPlugin to save every speaker to a separate file.",
    Description = "Plugins attach to a client with Plugins.AddAsync and are shut down with it. VoiceRecorderPlugin buffers decoded frames per session and writes 48 kHz WAV files on Flush.",
    Order = 70)]
public static class RecorderSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var options = ctx.CreateOptions("Recorder");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.Continuous;
        await using var client = new RumbleClient(options);

        var folder = Path.Combine(Path.GetTempPath(), "RumbleGallery", "recordings");
        var recorder = new VoiceRecorderPlugin(folder);
        await client.Plugins.AddAsync(recorder, ctx.Token);

        await client.ConnectAsync(ctx.Token);
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId, ctx.Token);

        await AudioPlayer.PlayAsync(client, ToneGenerator.Sine(523.25f, TimeSpan.FromSeconds(1.5)), ctx.Token);
        await Task.Delay(500, ctx.Token);

        foreach (var file in recorder.Flush())
        {
            ctx.Success($"Saved {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024} KiB)");
        }

        ctx.Note($"Folder: {folder}");
    }
}
