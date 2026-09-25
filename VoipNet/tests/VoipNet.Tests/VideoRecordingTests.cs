using System.Buffers.Binary;
using System.Text;
using VoipNet.Audio;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Recording a video call to AVI and to MP4, read back box by box.</summary>
public sealed class VideoRecordingTests
{
    [Fact]
    public async Task VideoCallsAreRecordedToAvi()
    {
        await using var pair = await LoopbackPair.ConnectAsync("camera", "recorder", o => o.Video = true);
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-rec-{Guid.NewGuid():N}.avi");
        var recorder = CallRecorder.Start(pair.CalleeLeg, path, RecordingFormat.Avi, RecordingLayout.Mono);
        Assert.Equal(RecordingFormat.Avi, recorder.Format);

        var received = 0;
        pair.CalleeLeg.VideoFrameReceived += (_, _, _, _, _) => Interlocked.Increment(ref received);

        // Both directions talk, so the recorder has audio to pair, and the caller sends video.
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 1500), 16000);
        pair.CalleeLeg.SendAudio(TestHelpers.Tone(16000, 1500, 880), 16000);
        var frames = new List<byte[]>();
        for (var i = 0; i < 3; i++)
        {
            var frame = Frame(i);
            frames.Add(frame);
            pair.CallerLeg.SendVideoFrame((uint)(90000 + (i * 3000)), frame);
            await Task.Delay(120);
        }

        await TestHelpers.ReceivedAudioAsync(pair.CalleeLeg, 900);
        await TestHelpers.WaitAsync(() => Volatile.Read(ref received) >= 3 ? "all" : null, TimeSpan.FromSeconds(10), "video frames");
        recorder.Dispose();

        var (codec, video, audioChunks) = ReadAvi(path);
        if (Environment.GetEnvironmentVariable("VOIPNET_KEEP_RECORDING") is null)
        {
            File.Delete(path);
        }
        else
        {
            Console.WriteLine($"recording kept at {path}");
        }
        Assert.Equal("H264", codec);
        Assert.True(audioChunks > 0, "no audio was recorded");
        Assert.Equal(3, video.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(frames[i], video[i]);
        }
    }

    [Fact]
    public async Task VideoCallsAreRecordedToMp4()
    {
        await using var pair = await LoopbackPair.ConnectAsync("camera", "recorder", o => o.Video = true);
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-rec-{Guid.NewGuid():N}.mp4");
        var recorder = CallRecorder.Start(pair.CalleeLeg, path, RecordingFormat.Mp4, RecordingLayout.Mono);
        Assert.Equal(RecordingFormat.Mp4, recorder.Format);

        var received = 0;
        pair.CalleeLeg.VideoFrameReceived += (_, _, _, _, _) => Interlocked.Increment(ref received);

        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 1500), 16000);
        pair.CalleeLeg.SendAudio(TestHelpers.Tone(16000, 1500, 880), 16000);
        var frames = new List<byte[]>();
        for (var i = 0; i < 3; i++)
        {
            var frame = ParameterisedFrame(i);
            frames.Add(frame);
            // 3600 ticks of the 90 kHz clock apart: 25 frames a second, which the file should say.
            pair.CallerLeg.SendVideoFrame((uint)(90000 + (i * 3600)), frame);
            await Task.Delay(120);
        }

        await TestHelpers.ReceivedAudioAsync(pair.CalleeLeg, 900);
        await TestHelpers.WaitAsync(() => Volatile.Read(ref received) >= 3 ? "all" : null, TimeSpan.FromSeconds(10), "video frames");
        recorder.Dispose();

        var bytes = File.ReadAllBytes(path);
        var boxes = Boxes(bytes, 0, bytes.Length);
        Assert.Equal("ftyp", boxes[0].Type);
        Assert.Contains(boxes, b => b.Type == "mdat");
        var moov = Assert.Single(boxes, b => b.Type == "moov");

        var traks = Boxes(bytes, moov.Start, moov.End).Where(b => b.Type == "trak").ToList();
        Assert.Equal(2, traks.Count);

        // The sample description carries the picture size and the parameter set, because MP4 keeps the
        // decoder's setup in the header rather than in front of every frame.
        var avc1 = Find(bytes, moov, "trak", "mdia", "minf", "stbl", "stsd", "avc1");
        Assert.Equal(320, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(avc1.Start + 24)));
        Assert.Equal(240, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(avc1.Start + 26)));
        var avcC = Assert.Single(Boxes(bytes, avc1.Start + 78, avc1.End), b => b.Type == "avcC");
        var spsLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(avcC.Start + 6));
        Assert.Equal(Sps, bytes.AsSpan(avcC.Start + 8, spsLength).ToArray());
        Assert.Equal(1, bytes[avcC.Start + 8 + spsLength]); // one picture parameter set

        // Every frame lasts the same 3600 ticks, so one entry describes all three.
        var stts = Find(bytes, moov, "trak", "mdia", "minf", "stbl", "stts");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stts.Start + 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stts.Start + 8)));
        Assert.Equal(3600u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stts.Start + 12)));

        // The frames themselves are the call's own, rewritten from start codes to length prefixes.
        var stsz = Find(bytes, moov, "trak", "mdia", "minf", "stbl", "stsz");
        var co64 = Find(bytes, moov, "trak", "mdia", "minf", "stbl", "co64");
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stsz.Start + 8)));
        for (var i = 0; i < frames.Count; i++)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stsz.Start + 12 + (i * 4)));
            var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(co64.Start + 8 + (i * 8)));
            Assert.Equal(LengthPrefixed(frames[i]), bytes.AsSpan((int)offset, size).ToArray());
        }

        var audioTrack = traks[1];
        var audioSizes = Find(bytes, audioTrack, "mdia", "minf", "stbl", "stsz");
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(audioSizes.Start + 4))); // 16-bit mono
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(audioSizes.Start + 8)) > 0, "no audio was recorded");

        // And a real tool agrees, where one is installed.
        var probe = Probe(path);
        if (probe is not null)
        {
            Assert.Contains("h264", probe);
            Assert.Contains("pcm_s16le", probe);
            Assert.Contains("320x240", probe);
        }

        if (Environment.GetEnvironmentVariable("VOIPNET_KEEP_RECORDING") is null)
        {
            File.Delete(path);
        }
        else
        {
            Console.WriteLine($"recording kept at {path}{(probe is null ? " (ffprobe not installed)" : $"\n{probe}")}");
        }
    }

    /// <summary>A sequence parameter set that really says 320x240, baseline, so the file can be checked.</summary>
    private static readonly byte[] Sps = [0x67, 0x42, 0xE0, 0x1E, 0xDA, 0x05, 0x07, 0xE4];

    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];

    /// <summary>An access unit a decoder could actually start on: a parameter set pair and an IDR slice.</summary>
    private static byte[] ParameterisedFrame(int index)
    {
        var frame = new List<byte> { 0, 0, 0, 1 };
        frame.AddRange(Sps);
        frame.AddRange([0, 0, 0, 1]);
        frame.AddRange(Pps);
        frame.AddRange([0, 0, 0, 1, 0x65]);
        frame.AddRange(Enumerable.Range(0, 2000).Select(i => (byte)((i + index) % 251 | 1)));
        return frame.ToArray();
    }

    /// <summary>The same access unit in the form MP4 stores: each unit behind its own length.</summary>
    private static byte[] LengthPrefixed(byte[] frame)
    {
        byte[][] units = [Sps, Pps, [0x65, .. frame[^2000..]]];
        var sample = new List<byte>();
        foreach (var unit in units)
        {
            sample.AddRange([(byte)(unit.Length >> 24), (byte)(unit.Length >> 16), (byte)(unit.Length >> 8), (byte)unit.Length]);
            sample.AddRange(unit);
        }

        return sample.ToArray();
    }

    /// <summary>What ffprobe makes of the file, or null when it is not installed.</summary>
    private static string? Probe(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffprobe")
            {
                ArgumentList = { "-v", "error", "-show_entries", "stream=codec_name,width,height", "-of", "compact", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20000);
            return output.Replace("|width=", "|").Replace("|height=", "x").Replace("codec_name=", string.Empty);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private readonly record struct Mp4Box(string Type, int Start, int End);

    /// <summary>The boxes directly inside a range, with <c>Start</c> at each one's contents.</summary>
    private static List<Mp4Box> Boxes(byte[] bytes, int from, int to)
    {
        var boxes = new List<Mp4Box>();
        var at = from;
        while (at + 8 <= to)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            var type = Encoding.ASCII.GetString(bytes, at + 4, 4);
            var start = at + 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
                start = at + 16;
            }

            if (size < 8 || at + size > to)
            {
                break;
            }

            boxes.Add(new Mp4Box(type, start, (int)(at + size)));
            at += (int)size;
        }

        return boxes;
    }

    /// <summary>Walks a path of box types, taking the first match at each step.</summary>
    private static Mp4Box Find(byte[] bytes, Mp4Box root, params string[] path)
    {
        var box = root;
        foreach (var type in path)
        {
            var next = Boxes(bytes, box.Start, box.End).FirstOrDefault(b => b.Type == type);
            if (next.Type is null)
            {
                // A box with a version and flags in front of its children, such as the sample description.
                next = Boxes(bytes, box.Start + 8, box.End).FirstOrDefault(b => b.Type == type);
            }

            Assert.False(next.Type is null, $"no {type} box");
            box = next;
        }

        return box;
    }

    /// <summary>An access unit with a parameter set and an IDR slice, long enough to be fragmented.</summary>
    private static byte[] Frame(int index)
    {
        var frame = new List<byte> { 0, 0, 0, 1, 0x67, 0x42, 0xE0, 0x1F, 0, 0, 0, 1, 0x65 };
        frame.AddRange(Enumerable.Range(0, 2000).Select(i => (byte)((i + index) % 251 | 1)));
        return frame.ToArray();
    }

    /// <summary>Reads back what the writer produced: the codec, the video frames and the audio chunk count.</summary>
    private static (string Codec, List<byte[]> Video, int AudioChunks) ReadAvi(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("AVI ", Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));

        var codec = string.Empty;
        var video = new List<byte[]>();
        var audio = 0;
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (id == "LIST")
            {
                var listType = Encoding.ASCII.GetString(bytes, at + 8, 4);
                if (listType == "movi")
                {
                    // Walk the chunks inside the list rather than skipping over it.
                    var end = Math.Min(at + 8 + size, bytes.Length);
                    var chunk = at + 12;
                    while (chunk + 8 <= end)
                    {
                        var chunkId = Encoding.ASCII.GetString(bytes, chunk, 4);
                        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(chunk + 4));
                        if (chunkId == "00dc")
                        {
                            video.Add(bytes.AsSpan(chunk + 8, chunkSize).ToArray());
                        }
                        else if (chunkId == "01wb")
                        {
                            audio++;
                        }

                        chunk += 8 + chunkSize + (chunkSize % 2);
                    }

                    at = end;
                    continue;
                }

                if (listType == "strl")
                {
                    // LIST size 'strl' 'strh' size type handler — the video stream names its codec there.
                    if (Encoding.ASCII.GetString(bytes, at + 20, 4) == "vids")
                    {
                        codec = Encoding.ASCII.GetString(bytes, at + 24, 4);
                    }
                }

                at += 12;
                continue;
            }

            at += 8 + size + (size % 2);
        }

        return (codec, video, audio);
    }
}
