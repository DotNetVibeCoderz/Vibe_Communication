using Rumble.Net.Audio;
using Rumble.Net.Codecs;

namespace Rumble.Net.Tests;

public class NativeTests
{
    [Fact]
    public void Reports_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", RumbleNative.Version);
    }

    [Fact]
    public void Opus_roundtrip_preserves_signal()
    {
        using var encoder = new OpusEncoder(bitrate: 64_000);
        using var decoder = new OpusDecoder();
        var tone = ToneGenerator.Sine(440, TimeSpan.FromMilliseconds(500));
        var packet = new byte[1500];
        var pcm = new float[OpusDecoder.MaxFrameSamples];
        double inEnergy = 0, outEnergy = 0;
        for (var i = 0; i + AudioMath.FrameSize <= tone.Length; i += AudioMath.FrameSize)
        {
            var frame = tone.AsSpan(i, AudioMath.FrameSize);
            var n = encoder.Encode(frame, packet);
            Assert.InRange(n, 1, 400);
            Assert.Equal(AudioMath.FrameSize, decoder.Decode(packet.AsSpan(0, n), pcm));
            if (i > AudioMath.FrameSize * 10)
            {
                inEnergy += AudioMath.Rms(frame);
                outEnergy += AudioMath.Rms(pcm.AsSpan(0, AudioMath.FrameSize));
            }
        }

        Assert.InRange(outEnergy / inEnergy, 0.7, 1.3);
        Assert.Equal(AudioMath.FrameSize, decoder.Conceal(pcm.AsSpan(0, AudioMath.FrameSize)));
    }

    [Fact]
    public void Generates_certificates()
    {
        var cert = RumbleNative.GenerateCertificate("TestBot");
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", cert.CertificatePem);
        Assert.Contains("PRIVATE KEY", cert.PrivateKeyPem);
        Assert.Equal(40, cert.Sha1Fingerprint.Length);
    }

    [Fact]
    public void Runs_benchmarks()
    {
        var results = RumbleNative.RunBenchmarks();
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.True(r.NanosecondsPerOperation > 0));
    }

    [Fact]
    public void Filters_process_audio()
    {
        var samples = ToneGenerator.Sine(1000, TimeSpan.FromMilliseconds(10), 0.5f);
        var before = AudioMath.Rms(samples);
        new GainFilter(-6).Process(samples);
        Assert.InRange(AudioMath.Rms(samples) / before, 0.49, 0.51);

        var loud = Enumerable.Repeat(2f, 480).ToArray();
        var limiter = new LimiterFilter(-1);
        limiter.Process(loud);
        Assert.True(AudioMath.Peak(loud) <= AudioMath.FromDecibels(-1) + 1e-3);
    }

    [Fact]
    public void Wave_roundtrip()
    {
        var tone = ToneGenerator.Sine(300, TimeSpan.FromMilliseconds(100));
        using var ms = new MemoryStream();
        WaveFile.WriteMono48k(ms, tone);
        ms.Position = 0;
        var read = WaveFile.ReadMono48k(ms);
        Assert.Equal(tone.Length, read.Length);
        Assert.InRange(read[100] - tone[100], -1e-3f, 1e-3f);
    }
}
