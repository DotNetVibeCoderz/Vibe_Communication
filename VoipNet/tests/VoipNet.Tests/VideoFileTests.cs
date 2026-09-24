using VoipNet.Cli;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Splitting an H.264 byte stream the way the CLI does before it sends it.</summary>
public sealed class VideoFileTests
{
    private static byte[] Nal(byte header, int payload, byte start = 4)
    {
        var unit = new List<byte>(start == 4 ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 }) { header };
        unit.AddRange(Enumerable.Range(0, payload).Select(i => (byte)(i % 251 | 1)));
        return unit.ToArray();
    }

    [Fact]
    public void ParameterSetsStayWithTheFrameTheyDescribe()
    {
        // SPS, PPS, IDR, then a delta frame — what the first second of a stream looks like.
        var stream = Nal(0x67, 8).Concat(Nal(0x68, 4)).Concat(Nal(0x65, 64)).Concat(Nal(0x61, 32)).ToArray();

        var frames = AnnexB.Frames(stream);

        Assert.Equal(2, frames.Count);
        Assert.True(AnnexB.IsKeyframe(frames[0]));
        Assert.False(AnnexB.IsKeyframe(frames[1]));
        // The keyframe carries its parameter sets, so a receiver can start on it.
        Assert.Equal(Nal(0x67, 8).Length + Nal(0x68, 4).Length + Nal(0x65, 64).Length, frames[0].Length);
        Assert.Equal(stream.Length, frames.Sum(f => f.Length));
    }

    [Fact]
    public void ThreeByteStartCodesAreReadToo()
    {
        var stream = Nal(0x65, 16, start: 3).Concat(Nal(0x61, 8, start: 3)).Concat(Nal(0x61, 8)).ToArray();

        var frames = AnnexB.Frames(stream);

        Assert.Equal(3, frames.Count);
        Assert.True(AnnexB.IsKeyframe(frames[0]));
    }

    [Fact]
    public void AStreamWithoutSlicesProducesNoFrames()
    {
        Assert.Empty(AnnexB.Frames(Nal(0x67, 8)));
        Assert.Empty(AnnexB.Frames([]));
    }

    [Fact]
    public void ReceivedVideoIsSummarised()
    {
        var stats = new VideoStreamStats();
        stats.Add(1000, keyframe: true);
        stats.Add(200, keyframe: false);
        stats.Add(220, keyframe: false);

        Assert.Equal(3, stats.Frames);
        Assert.Equal(1, stats.Keyframes);
        Assert.Equal(3, stats.KeyframeInterval);
        Assert.True(stats.Kbps > 0);
        Assert.True(stats.LongestGap >= TimeSpan.Zero);
    }
}
