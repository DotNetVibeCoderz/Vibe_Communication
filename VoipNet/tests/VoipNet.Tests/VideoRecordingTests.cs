using System.Buffers.Binary;
using System.Text;
using VoipNet.Audio;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Recording a video call to AVI, read back chunk by chunk.</summary>
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
        pair.CalleeLeg.VideoFrameReceived += (_, _, _, _) => Interlocked.Increment(ref received);

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
