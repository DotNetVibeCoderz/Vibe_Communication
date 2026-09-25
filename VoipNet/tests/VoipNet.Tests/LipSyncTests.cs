using Xunit;

namespace VoipNet.Tests;

/// <summary>Lining audio up with video from the clocks in RTCP sender reports.</summary>
public class LipSyncTests
{
    [Fact]
    public async Task TimestampsFromBothStreamsLandOnTheSameClock()
    {
        await using var pair = await LoopbackPair.ConnectAsync("speaker", "viewer", o => o.Video = true);

        var frame = new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0xE0, 0x1F, 0, 0, 0, 1, 0x65 }
            .Concat(Enumerable.Range(0, 900).Select(i => (byte)(i % 251 | 1)))
            .ToArray();

        // Reports go out every few seconds, so keep both streams busy at a sane rate until each has
        // sent one. Flooding the sockets instead would only lose the packets the reports count.
        var timestamp = 180_000u;
        DateTimeOffset? audioAt = null;
        DateTimeOffset? videoAt = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && (audioAt is null || videoAt is null))
        {
            pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 200), 16000);
            for (var i = 0; i < 6; i++)
            {
                pair.CallerLeg.SendVideoFrame(timestamp, frame);
                timestamp += 3000;
                await Task.Delay(33);
            }

            audioAt = pair.CalleeLeg.AudioPlayoutTime;
            videoAt = pair.CalleeLeg.PresentationTime(MediaStream.Video, timestamp);
        }

        Assert.NotNull(audioAt);
        Assert.NotNull(videoAt);

        // Both are moments on the sender's clock, so a frame just sent and the audio being heard now
        // are seconds apart at most — which is what makes them comparable at all.
        Assert.True(Math.Abs((videoAt!.Value - audioAt!.Value).TotalSeconds) < 10, $"audio {audioAt}, video {videoAt}");

        // A 90 kHz clock counts 3000 ticks per frame at 30 fps: a third of a second for ten of them.
        var later = pair.CalleeLeg.PresentationTime(MediaStream.Video, timestamp + 30_000);
        var gap = later!.Value - videoAt.Value;
        Assert.InRange(gap.TotalMilliseconds, 330, 337);

        await pair.CallerLeg.HangupAsync();
    }

    [Fact]
    public async Task ACallWithoutVideoHasNoVideoClock()
    {
        await using var pair = await LoopbackPair.ConnectAsync("plain", "peer");

        Assert.Throws<VoipException>(() => pair.CallerLeg.PresentationTime(MediaStream.Video, 0));

        await pair.CallerLeg.HangupAsync();
    }
}
