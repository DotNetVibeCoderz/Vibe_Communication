using System.Diagnostics;
using Rumble.Net.Audio;
using Rumble.Net.Codecs;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Audio", "Opus encoding and packet loss concealment", "Use the native Opus codec directly and measure compression and speed.",
    Description = "OpusEncoder/OpusDecoder expose the same pure-Rust Opus implementation the voice pipeline uses — useful for recording, file streaming or custom transports.",
    Order = 65)]
public static class OpusCodecSample
{
    public static Task RunAsync(SampleContext ctx)
    {
        using var encoder = new OpusEncoder(channels: 1, bitrate: 32_000);
        using var decoder = new OpusDecoder();

        var input = ToneGenerator.Melody([(440f, TimeSpan.FromSeconds(1)), (660f, TimeSpan.FromSeconds(1))]);
        const int frame = 960; // 20 ms
        var packet = new byte[1500];
        var output = new float[OpusDecoder.MaxFrameSamples];
        long bytes = 0;
        var packets = 0;
        var encodeTime = new Stopwatch();
        var decodeTime = new Stopwatch();

        for (var offset = 0; offset + frame <= input.Length; offset += frame)
        {
            encodeTime.Start();
            var length = encoder.Encode(input.AsSpan(offset, frame), packet);
            encodeTime.Stop();

            decodeTime.Start();
            if (packets % 25 == 24)
            {
                decoder.Conceal(output.AsSpan(0, frame)); // simulate one lost packet per 500 ms
            }
            else
            {
                decoder.Decode(packet.AsSpan(0, length), output);
            }

            decodeTime.Stop();
            bytes += length;
            packets++;
        }

        var raw = input.Length * sizeof(short);
        ctx.Log($"Encoded {packets} packets, {bytes / 1024.0:0.0} KiB (raw PCM16 {raw / 1024.0:0.0} KiB)");
        ctx.Log($"Compression ratio {raw / (double)bytes:0.0}:1 · average packet {bytes / packets} bytes");
        ctx.Log($"Encode {encodeTime.Elapsed.TotalMilliseconds / packets:0.000} ms/frame · decode {decodeTime.Elapsed.TotalMilliseconds / packets:0.000} ms/frame");
        ctx.Success($"Real-time factor: {input.Length / 48_000.0 / (encodeTime.Elapsed + decodeTime.Elapsed).TotalSeconds:0}x faster than real time");
        return Task.CompletedTask;
    }
}
