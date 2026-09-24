using System.Diagnostics;

namespace VoipNet.Cli;

/// <summary>
/// Reads an H.264 byte stream (Annex B, as ffmpeg writes with <c>-f h264</c>) and hands it back one
/// access unit at a time — which is what a call sends and receives, one frame per RTP timestamp.
/// </summary>
public static class AnnexB
{
    /// <summary>Splits a byte stream into access units, each starting with its parameter sets.</summary>
    /// <param name="stream">The file contents.</param>
    /// <returns>Frames in order; each one is ready to hand to <c>SendVideoFrame</c>.</returns>
    public static IReadOnlyList<byte[]> Frames(ReadOnlySpan<byte> stream)
    {
        var frames = new List<byte[]>();
        var units = Units(stream);
        var current = new List<byte>();
        var hasSlice = false;
        foreach (var (start, length) in units)
        {
            var type = stream[PayloadStart(stream, start)] & 0x1F;
            // A picture starts at the first slice after the previous one: parameter sets belong with
            // the frame they describe, so they are carried forward into the new access unit.
            var isSlice = type is 1 or 5;
            if (isSlice && hasSlice)
            {
                frames.Add(current.ToArray());
                current = [];
                hasSlice = false;
            }

            current.AddRange(stream.Slice(start, length).ToArray());
            hasSlice |= isSlice;
        }

        if (hasSlice)
        {
            frames.Add(current.ToArray());
        }

        return frames;
    }

    /// <summary>True when the access unit contains an IDR slice, which a decoder can start on.</summary>
    /// <param name="frame">An access unit.</param>
    public static bool IsKeyframe(ReadOnlySpan<byte> frame)
    {
        foreach (var (start, _) in Units(frame))
        {
            if ((frame[PayloadStart(frame, start)] & 0x1F) == 5)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every NAL unit in the stream, as (offset, length) including its start code.</summary>
    private static List<(int Start, int Length)> Units(ReadOnlySpan<byte> stream)
    {
        var units = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i + 2 < stream.Length; i++)
        {
            if (stream[i] != 0 || stream[i + 1] != 0)
            {
                continue;
            }

            var code = stream[i + 2] == 1 ? 3 : i + 3 < stream.Length && stream[i + 2] == 0 && stream[i + 3] == 1 ? 4 : 0;
            if (code == 0)
            {
                continue;
            }

            if (start >= 0)
            {
                units.Add((start, i - start));
            }

            start = i;
            i += code - 1;
        }

        if (start >= 0)
        {
            units.Add((start, stream.Length - start));
        }

        return units;
    }

    /// <summary>Offset of the NAL header inside a unit that begins with a three or four byte start code.</summary>
    private static int PayloadStart(ReadOnlySpan<byte> stream, int unitStart) =>
        stream[unitStart + 2] == 1 ? unitStart + 3 : unitStart + 4;
}

/// <summary>What arrived on a call's video stream.</summary>
public sealed class VideoStreamStats
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;

    /// <summary>Frames received.</summary>
    public int Frames { get; private set; }

    /// <summary>Frames a decoder could start on.</summary>
    public int Keyframes { get; private set; }

    /// <summary>Bytes received.</summary>
    public long Bytes { get; private set; }

    /// <summary>Longest gap between two frames — a freeze, as a viewer would see it.</summary>
    public TimeSpan LongestGap { get; private set; }

    /// <summary>Frames per second over the whole call.</summary>
    public double FrameRate => _clock.Elapsed.TotalSeconds > 0 ? Frames / _clock.Elapsed.TotalSeconds : 0;

    /// <summary>Average bitrate in kilobits per second.</summary>
    public double Kbps => _clock.Elapsed.TotalSeconds > 0 ? Bytes * 8 / 1000.0 / _clock.Elapsed.TotalSeconds : 0;

    /// <summary>Frames between keyframes, which is how often a late joiner could start watching.</summary>
    public double KeyframeInterval => Keyframes == 0 ? 0 : Frames / (double)Keyframes;

    /// <summary>Records one received frame.</summary>
    /// <param name="length">Frame size in bytes.</param>
    /// <param name="keyframe">Whether a decoder could start on it.</param>
    public void Add(int length, bool keyframe)
    {
        var now = _clock.Elapsed;
        if (Frames > 0 && now - _lastFrame > LongestGap)
        {
            LongestGap = now - _lastFrame;
        }

        _lastFrame = now;
        Frames++;
        Bytes += length;
        if (keyframe)
        {
            Keyframes++;
        }
    }
}
