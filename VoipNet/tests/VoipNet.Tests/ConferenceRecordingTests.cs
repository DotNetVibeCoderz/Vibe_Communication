using System.Buffers.Binary;
using System.Text;
using VoipNet.Enterprise.Recording;
using VoipNet.Video;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Recording a whole room: everyone decoded, laid out in one picture, encoded once.</summary>
public sealed class ConferenceRecordingTests
{
    private const int Width = 320;
    private const int Height = 240;

    [Fact]
    public async Task ARoomIsRecordedAsOnePicture()
    {
        Assert.SkipUnless(ConferenceRecorder.IsSupported, "No platform H.264 codec on this OS.");

        // A bridge with two callers on video, as a meeting is: the recorder decodes both, puts them
        // side by side and encodes the result, which is the trade the engine itself never makes.
        await using var host = new VoipClient(TestHelpers.LoopbackOptions("host", o => o.Video = true));
        await using var first = new VoipClient(TestHelpers.LoopbackOptions("first", o => o.Video = true));
        await using var second = new VoipClient(TestHelpers.LoopbackOptions("second", o => o.Video = true));
        await host.StartAsync();
        await first.StartAsync();
        await second.StartAsync();
        host.IncomingCall += (_, e) => _ = e.Call.AnswerAsync();

        var firstLeg = await first.CallAsync($"sip:host@{host.LocalAddress}");
        var secondLeg = await second.CallAsync($"sip:host@{host.LocalAddress}");
        await TestHelpers.WaitUntilAsync(() => host.Calls.Count(c => c.IsActive) == 2, TimeSpan.FromSeconds(10), "both legs up");

        using var conference = host.CreateConference();
        foreach (var call in host.Calls.Where(c => c.IsActive))
        {
            conference.Add(call);
        }

        var path = Path.Combine(Path.GetTempPath(), $"voipnet-room-{Guid.NewGuid():N}.mp4");
        var recorder = ConferenceRecorder.Start(path, 640, 360, framesPerSecond: 10, sampleRate: 16000);
        Assert.NotNull(recorder);
        foreach (var call in host.Calls.Where(c => c.IsActive))
        {
            recorder.Add(call);
        }

        using (var left = Encoder())
        using (var right = Encoder())
        {
            var nv12 = new byte[VideoPicture.Nv12Length(Width, Height)];
            for (var i = 0; i < 25; i++)
            {
                // Both callers send a picture and a voice, which is all the recorder has to work with.
                VideoPictures.FromBgra(Pattern(i, bright: true), Width, Height, nv12);
                foreach (var frame in left.Encode(new VideoPicture(Width, Height, nv12, TimeSpan.FromSeconds(i / 10.0))))
                {
                    firstLeg.SendVideoFrame((uint)(90000 + (i * 9000)), frame.Data.Span);
                }

                VideoPictures.FromBgra(Pattern(i, bright: false), Width, Height, nv12);
                foreach (var frame in right.Encode(new VideoPicture(Width, Height, nv12, TimeSpan.FromSeconds(i / 10.0))))
                {
                    secondLeg.SendVideoFrame((uint)(90000 + (i * 9000)), frame.Data.Span);
                }

                firstLeg.SendAudio(TestHelpers.Tone(16000, 100), 16000);
                secondLeg.SendAudio(TestHelpers.Tone(16000, 100, 880), 16000);
                await Task.Delay(100);
            }
        }

        await Task.Delay(500);
        recorder.Dispose();
        await firstLeg.HangupAsync();
        await secondLeg.HangupAsync();

        var bytes = File.ReadAllBytes(path);
        if (Environment.GetEnvironmentVariable("VOIPNET_KEEP_RECORDING") is null)
        {
            File.Delete(path);
        }
        else
        {
            Console.WriteLine($"recording kept at {path}");
        }

        // An MP4 with both tracks and a picture the size the room was composed at.
        Assert.True(bytes.Length > 4096, $"the recording is {bytes.Length} bytes");
        var boxes = Boxes(bytes);
        Assert.Contains("ftyp", boxes);
        Assert.Contains("mdat", boxes);
        Assert.Contains("moov", boxes);

        var (width, height, samples) = Video(bytes);
        Assert.Equal(640, width);
        Assert.Equal(360, height);
        Assert.True(samples >= 5, $"only {samples} composed frames were written");
    }

    private static IVideoEncoder Encoder() => VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
    {
        Width = Width,
        Height = Height,
        FramesPerSecond = 10,
        BitsPerSecond = 400_000,
    });

    /// <summary>The top-level box types, in order.</summary>
    private static List<string> Boxes(byte[] bytes)
    {
        var types = new List<string>();
        var at = 0;
        while (at + 8 <= bytes.Length)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            types.Add(Encoding.ASCII.GetString(bytes, at + 4, 4));
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
            }

            if (size < 8)
            {
                break;
            }

            at += (int)size;
        }

        return types;
    }

    /// <summary>The picture size and how many frames the file holds, read out of the sample tables.</summary>
    private static (int Width, int Height, int Samples) Video(byte[] bytes)
    {
        // 'avc1' carries the size 24 bytes in; 'stsz' counts the samples 8 bytes in. The search starts
        // past the file type box, which lists avc1 among the brands it is compatible with.
        var moov = Find(bytes, "moov", 4);
        var avc1 = Find(bytes, "avc1", moov);
        var stsz = Find(bytes, "stsz", moov);
        Assert.True(avc1 > 0 && stsz > 0, "the file has no video track");
        return (
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(avc1 + 24)),
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(avc1 + 26)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stsz + 8)));
    }

    /// <summary>Where a box's contents start, wherever it is nested.</summary>
    private static int Find(byte[] bytes, string type, int from)
    {
        var needle = Encoding.ASCII.GetBytes(type);
        for (var i = Math.Max(from, 4); i + 8 < bytes.Length; i++)
        {
            if (bytes.AsSpan(i, 4).SequenceEqual(needle))
            {
                return i + 4;
            }
        }

        return -1;
    }

    /// <summary>Two tellable pictures: one bright with bars, one dark with a band.</summary>
    private static byte[] Pattern(int frame, bool bright)
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var at = ((y * Width) + x) * 4;
                var bar = (x + (frame * 5)) % Width < Width / 2;
                var value = (byte)(bright ? (bar ? 230 : 160) : (bar ? 70 : 30));
                bgra[at] = value;
                bgra[at + 1] = value;
                bgra[at + 2] = value;
                bgra[at + 3] = 255;
            }
        }

        return bgra;
    }
}
