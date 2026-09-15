using Rumble.Net;
using Rumble.Net.Audio;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Audio", "Capture filters", "Process microphone audio before encoding with built-in and custom filters.",
    Description = "IAudioFilter runs in place on each 10 ms frame on the audio thread, before Opus encoding. Keep filters allocation-free; AudioMath provides SIMD helpers.",
    Order = 60)]
public static class AudioFiltersSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var options = ctx.CreateOptions("FilterLab");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.Continuous;
        await using var client = new RumbleClient(options);

        var meter = new LevelMeter();
        client.Audio.AddCaptureFilter(new GainFilter(gainDb: -12));      // quieter
        client.Audio.AddCaptureFilter(new LimiterFilter(ceilingDb: -18)); // hard ceiling
        client.Audio.AddCaptureFilter(meter);                            // custom: measure after processing

        var echoPeak = -96f;
        client.Audio.AudioFrameReceived += (in AudioFrame f) =>
        {
            if (f.Session == MockMumbleServer.EchoBotSession && !f.IsConcealed)
            {
                echoPeak = Math.Max(echoPeak, AudioMath.ToDecibels(AudioMath.Peak(f.Samples)));
            }
        };

        await client.ConnectAsync(ctx.Token);
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId, ctx.Token);

        var input = ToneGenerator.Sine(440, TimeSpan.FromSeconds(1), amplitude: 0.9f);
        ctx.Log($"Input peak: {AudioMath.ToDecibels(AudioMath.Peak(input)):0.0} dBFS");
        await AudioPlayer.PlayAsync(client, input, ctx.Token);
        await Task.Delay(500, ctx.Token);

        ctx.Log($"After gain + limiter (measured by the custom filter): {meter.PeakDb:0.0} dBFS over {meter.Frames} frames");
        ctx.Success($"Peak heard back from EchoBot after Opus round trip: {echoPeak:0.0} dBFS");
    }

    /// <summary>A custom filter: it only observes the signal.</summary>
    private sealed class LevelMeter : IAudioFilter
    {
        public float PeakDb { get; private set; } = -96f;

        public int Frames { get; private set; }

        public void Process(Span<float> samples)
        {
            Frames++;
            PeakDb = Math.Max(PeakDb, AudioMath.ToDecibels(AudioMath.Peak(samples)));
        }
    }
}
