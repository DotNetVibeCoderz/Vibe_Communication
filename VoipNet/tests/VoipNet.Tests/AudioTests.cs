using VoipNet.Audio;
using Xunit;

namespace VoipNet.Tests;

public sealed class AudioTests
{
    [Fact]
    public void WavFilesRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-{Guid.NewGuid():N}.wav");
        var tone = TestHelpers.Tone(16000, 250);
        using (var writer = new WavWriter(path, 16000))
        {
            writer.Write(tone);
            Assert.Equal(250, (int)writer.Duration.TotalMilliseconds);
        }

        var (samples, rate, channels) = WavReader.Read(path);
        File.Delete(path);
        Assert.Equal(16000, rate);
        Assert.Equal(1, channels);
        Assert.Equal(tone, samples);
    }

    [Theory]
    [InlineData(8000, 16000)]
    [InlineData(16000, 8000)]
    [InlineData(24000, 16000)]
    [InlineData(16000, 48000)]
    public void ResamplerKeepsDurationAcrossBlocks(int from, int to)
    {
        var resampler = new AudioResampler(from, to);
        var output = new List<short>();
        var block = TestHelpers.Tone(from, 20);
        for (var i = 0; i < 50; i++)
        {
            resampler.Process(block, output);
        }

        Assert.InRange(output.Count, to - 2, to + 2);
    }

    [Fact]
    public void VoiceActivityDetectorFindsSpeechBetweenSilence()
    {
        var vad = new VoiceActivityDetector(16000) { SilenceHangoverMs = 300 };
        var started = 0;
        var ended = 0;
        vad.SpeechStarted += (_, _) => started++;
        vad.SpeechEnded += (_, _) => ended++;

        var silence = new short[320];
        var speech = TestHelpers.Tone(16000, 20, 300, 6000);
        for (var i = 0; i < 25; i++) vad.Process(silence);
        for (var i = 0; i < 40; i++) vad.Process(speech);
        Assert.True(vad.IsSpeaking);
        for (var i = 0; i < 30; i++) vad.Process(silence);

        Assert.Equal(1, started);
        Assert.Equal(1, ended);
        Assert.False(vad.IsSpeaking);
    }

    [Fact]
    public void TonesHaveTheRequestedLength()
    {
        Assert.Equal(8000, ToneGenerator.Ringback(8000, 1000).Length);
        Assert.Equal(1920, ToneGenerator.Dtmf('5', 16000).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ToneGenerator.Dtmf('x', 8000));
    }

    [Fact]
    public async Task CallRecorderWritesBothDirections()
    {
        await using var pair = await LoopbackPair.ConnectAsync();
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-rec-{Guid.NewGuid():N}.wav");
        var recorder = CallRecorder.Start(pair.CalleeLeg, path, RecordingFormat.Wav, RecordingLayout.Stereo);

        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 800), 16000);
        pair.CalleeLeg.SendAudio(TestHelpers.Tone(16000, 800, 880), 16000);
        await Task.Delay(1400);
        recorder.Dispose();

        var (samples, rate, channels) = WavReader.Read(recorder.Path);
        File.Delete(recorder.Path);
        Assert.Equal(2, channels);
        // The recording keeps the call's own rate (48 kHz with Opus).
        Assert.Equal(pair.CalleeLeg.SampleRate, rate);
        Assert.True(samples.Length / 2 > rate * 0.6, $"recorded {samples.Length / 2} frames");

        // Both channels carry signal.
        Assert.Contains(samples.Where((_, i) => i % 2 == 0), s => Math.Abs((int)s) > 2000);
        Assert.Contains(samples.Where((_, i) => i % 2 == 1), s => Math.Abs((int)s) > 2000);
    }

    [Fact]
    public async Task Mp3RecordingWorksOrFallsBackToWav()
    {
        await using var pair = await LoopbackPair.ConnectAsync();
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-rec-{Guid.NewGuid():N}.mp3");
        var recorder = CallRecorder.Start(pair.CalleeLeg, path, RecordingFormat.Mp3, RecordingLayout.Mono);
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 600), 16000);
        await Task.Delay(1000);
        recorder.Dispose();

        Assert.True(File.Exists(recorder.Path));
        Assert.True(new FileInfo(recorder.Path).Length > 1000);
        File.Delete(recorder.Path);
    }
}
