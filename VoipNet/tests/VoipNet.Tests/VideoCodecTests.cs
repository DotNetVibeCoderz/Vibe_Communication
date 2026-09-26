using VoipNet.Video;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Encoding and decoding a picture with the platform's own codec, and the colour conversion around it.</summary>
public sealed class VideoCodecTests
{
    private const int Width = 320;
    private const int Height = 240;

    [Fact]
    public void ColoursSurviveTheTripToNv12AndBack()
    {
        // A picture with no hard edges: colour is stored for each two-by-two block, so a boundary that
        // falls inside one can only be approximated, and that says nothing about the conversion.
        var source = Gradient();
        var nv12 = new byte[VideoPicture.Nv12Length(Width, Height)];
        var back = new byte[Width * Height * 4];

        VideoPictures.FromBgra(source, Width, Height, nv12);
        VideoPictures.ToBgra(nv12, Width, Height, back);

        // Colour is stored at half resolution, so a pixel is close rather than identical. What matters
        // is that nothing drifts: red stays red and the gradient keeps its shape.
        var worst = 0;
        double total = 0;
        for (var i = 0; i < source.Length; i++)
        {
            if (i % 4 == 3)
            {
                Assert.Equal(255, back[i]); // opaque
                continue;
            }

            var difference = Math.Abs(source[i] - back[i]);
            worst = Math.Max(worst, difference);
            total += difference;
        }

        Assert.True(total / source.Length < 2, $"the picture drifted on average by {total / source.Length:F1}");
        Assert.True(worst < 12, $"one channel was off by {worst}");
    }

    [Fact]
    public void AnEvenSizeIsRequired()
    {
        var nv12 = new byte[VideoPicture.Nv12Length(4, 4)];
        Assert.Throws<ArgumentException>(() => VideoPictures.FromBgra(new byte[3 * 4 * 4], 3, 4, nv12));
    }

    [Fact]
    public void PicturesGoThroughThePlatformEncoderAndComeBack()
    {
        Assert.SkipUnless(VideoCodecs.IsH264Available, "No platform H.264 codec on this OS.");

        var nv12 = new byte[VideoPicture.Nv12Length(Width, Height)];
        var encoded = new List<EncodedVideoFrame>();
        using (var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
        {
            Width = Width,
            Height = Height,
            FramesPerSecond = 25,
            BitsPerSecond = 800_000,
        }))
        {
            Assert.Equal("H264", encoder.Codec);
            for (var i = 0; i < 30; i++)
            {
                VideoPictures.FromBgra(Pattern(i), Width, Height, nv12);
                if (i == 15)
                {
                    // What answering a PLI does: the next picture must be one a decoder can start on.
                    encoder.RequestKeyframe();
                }

                encoded.AddRange(encoder.Encode(new VideoPicture(Width, Height, nv12, TimeSpan.FromSeconds(i / 25.0))));
            }

            encoded.AddRange(encoder.Drain());
        }

        Assert.True(encoded.Count >= 25, $"only {encoded.Count} frames came out of the encoder");
        Assert.True(encoded[0].Keyframe, "a stream starts on a keyframe");
        Assert.True(encoded.Count(f => f.Keyframe) >= 2, "the keyframe that was asked for never arrived");
        Assert.All(encoded, f => Assert.True(f.Data.Length > 0));
        // Annex B: every access unit starts on a start code.
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, encoded[0].Data.Span[..4].ToArray());

        var pictures = new List<VideoPicture>();
        using (var decoder = VideoCodecs.CreateH264Decoder())
        {
            foreach (var frame in encoded)
            {
                pictures.AddRange(decoder.Decode(frame.Data.Span, frame.Timestamp));
            }
        }

        Assert.True(pictures.Count >= encoded.Count - 2, $"{encoded.Count} frames in, {pictures.Count} pictures out");
        Assert.Equal(Width, pictures[0].Width);
        Assert.Equal(Height, pictures[0].Height);
        Assert.Equal(VideoPicture.Nv12Length(Width, Height), pictures[0].Data.Length);

        // And it is the picture that went in, not merely a picture: the last one is compared with the
        // frame it was made from, in brightness, where the eye and the codec both put their effort.
        var last = pictures[^1];
        VideoPictures.FromBgra(Pattern(pictures.Count - 1), Width, Height, nv12);
        double error = 0;
        for (var i = 0; i < Width * Height; i++)
        {
            var difference = last.Data.Span[i] - nv12[i];
            error += difference * difference;
        }

        var psnr = 10 * Math.Log10(255.0 * 255.0 / (error / (Width * Height)));
        Assert.True(psnr > 30, $"the picture came back at {psnr:F1} dB, which is not the one that went in");
    }

    [Fact]
    public async Task EncodedPicturesSurviveARealCall()
    {
        Assert.SkipUnless(VideoCodecs.IsH264Available, "No platform H.264 codec on this OS.");

        await using var pair = await LoopbackPair.ConnectAsync("camera", "screen", o => o.Video = true);
        Assert.Equal("H264", pair.CallerLeg.VideoCodec);

        var decoded = new List<VideoPicture>();
        using var decoder = VideoCodecs.CreateH264Decoder();
        var arrived = new List<byte[]>();
        pair.CalleeLeg.VideoFrameReceived += (_, _, _, frame, _) =>
        {
            lock (arrived)
            {
                arrived.Add(frame.ToArray());
            }
        };

        using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
        {
            Width = Width,
            Height = Height,
            FramesPerSecond = 25,
            BitsPerSecond = 600_000,
        });

        var nv12 = new byte[VideoPicture.Nv12Length(Width, Height)];
        var sent = 0;
        for (var i = 0; i < 25; i++)
        {
            VideoPictures.FromBgra(Pattern(i), Width, Height, nv12);
            foreach (var frame in encoder.Encode(new VideoPicture(Width, Height, nv12, TimeSpan.FromSeconds(i / 25.0))))
            {
                pair.CallerLeg.SendVideoFrame((uint)(90000 + (sent * 3600)), frame.Data.Span);
                sent++;
            }

            await Task.Delay(40);
        }

        Assert.True(sent > 0, "the encoder produced nothing to send");
        await TestHelpers.WaitAsync(
            () =>
            {
                lock (arrived)
                {
                    return arrived.Count >= sent - 3 ? "enough" : null;
                }
            },
            TimeSpan.FromSeconds(20),
            "video frames to arrive");

        byte[][] frames;
        lock (arrived)
        {
            frames = [.. arrived];
        }

        foreach (var frame in frames)
        {
            decoded.AddRange(decoder.Decode(frame, TimeSpan.Zero));
        }

        // The pictures came back through packetisation, RTP and reassembly, and are still the pictures.
        Assert.True(decoded.Count >= frames.Length - 2, $"{frames.Length} frames arrived, {decoded.Count} decoded");
        Assert.Equal(Width, decoded[0].Width);
        VideoPictures.FromBgra(Pattern(decoded.Count - 1), Width, Height, nv12);
        double error = 0;
        for (var i = 0; i < Width * Height; i++)
        {
            var difference = decoded[^1].Data.Span[i] - nv12[i];
            error += difference * difference;
        }

        var psnr = 10 * Math.Log10(255.0 * 255.0 / (error / (Width * Height)));
        Assert.True(psnr > 25, $"the picture arrived at {psnr:F1} dB, which is not the one that was sent");
    }

    [Fact]
    public void ScreenContentIsEncodedForQualityRatherThanRate()
    {
        Assert.SkipUnless(VideoCodecs.IsH264Available, "No platform H.264 codec on this OS.");

        // The same pictures through both settings: a screen encoded for quality spends less on a
        // picture that barely moves, which is what a shared document mostly is.
        var motion = Encode(VideoContent.Motion);
        var detail = Encode(VideoContent.Detail);

        Assert.True(motion.Frames > 0 && detail.Frames > 0, "neither setting produced anything");
        Assert.True(detail.Bytes < motion.Bytes, $"detail {detail.Bytes} bytes, motion {motion.Bytes}");

        // And what comes out is still decodable H.264 of the right size.
        using var decoder = VideoCodecs.CreateH264Decoder();
        var pictures = new List<VideoPicture>();
        foreach (var frame in detail.Encoded)
        {
            pictures.AddRange(decoder.Decode(frame.Data.Span, frame.Timestamp));
        }

        Assert.NotEmpty(pictures);
        Assert.Equal(Width, pictures[0].Width);
    }

    private static (int Frames, int Bytes, List<EncodedVideoFrame> Encoded) Encode(VideoContent content)
    {
        using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
        {
            Width = Width,
            Height = Height,
            FramesPerSecond = 10,
            BitsPerSecond = 1_000_000,
            Content = content,
        });

        var nv12 = new byte[VideoPicture.Nv12Length(Width, Height)];
        var encoded = new List<EncodedVideoFrame>();
        var bytes = 0;
        for (var i = 0; i < 20; i++)
        {
            VideoPictures.FromBgra(Text(i), Width, Height, nv12);
            foreach (var frame in encoder.Encode(new VideoPicture(Width, Height, nv12, TimeSpan.FromSeconds(i / 10.0))))
            {
                encoded.Add(frame);
                bytes += frame.Data.Length;
            }
        }

        foreach (var frame in encoder.Drain())
        {
            encoded.Add(frame);
            bytes += frame.Data.Length;
        }

        return (encoded.Count, bytes, encoded);
    }

    /// <summary>Lines of text on white with one blinking cursor: what a shared document looks like.</summary>
    private static byte[] Text(int frame)
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var at = ((y * Width) + x) * 4;
                var glyph = y % 14 < 9 && (x + (y / 14 * 3)) % 7 < 4 && x < Width - 60;
                var cursor = x > Width - 60 && x < Width - 40 && (y / 20) % 2 == frame % 2;
                var value = (byte)(glyph || cursor ? 30 : 245);
                bgra[at] = value;
                bgra[at + 1] = value;
                bgra[at + 2] = value;
                bgra[at + 3] = 255;
            }
        }

        return bgra;
    }

    [Fact]
    public async Task SeveralEncodingsOfOnePictureTravelSeparately()
    {
        Assert.SkipUnless(VideoCodecs.IsH264Available, "No platform H.264 codec on this OS.");

        // Two sizes of the same picture, sent on one stream and told apart at the far end by the
        // label on each packet. The receiver keeps one of them, which is what simulcast is for.
        await using var pair = await LoopbackPair.ConnectAsync(
            "camera",
            "viewer",
            o => o.Video = true,
            caller => caller.VideoEncodings = ["h", "l"]);

        using var big = Encoder(Width, Height, 700_000);
        using var small = Encoder(Width / 2, Height / 2, 150_000);
        var arrived = 0;
        pair.CalleeLeg.VideoFrameReceived += (_, _, _, _, _) => Interlocked.Increment(ref arrived);

        var large = new byte[VideoPicture.Nv12Length(Width, Height)];
        var tiny = new byte[VideoPicture.Nv12Length(Width / 2, Height / 2)];
        for (var i = 0; i < 20; i++)
        {
            VideoPictures.FromBgra(Pattern(i), Width, Height, large);
            VideoPictures.FromBgra(Half(Pattern(i)), Width / 2, Height / 2, tiny);
            var at = TimeSpan.FromSeconds(i / 25.0);
            foreach (var frame in big.Encode(new VideoPicture(Width, Height, large, at)))
            {
                pair.CallerLeg.SendVideoFrameAs((uint)(90000 + (i * 3600)), frame.Data.Span, "h");
            }

            foreach (var frame in small.Encode(new VideoPicture(Width / 2, Height / 2, tiny, at)))
            {
                pair.CallerLeg.SendVideoFrameAs((uint)(90000 + (i * 3600)), frame.Data.Span, "l");
            }

            await Task.Delay(40);
        }

        await TestHelpers.WaitAsync(
            () => pair.CalleeLeg.VideoLayers.Count >= 2 ? "both" : null,
            TimeSpan.FromSeconds(15),
            "both encodings to be seen");

        var layers = pair.CalleeLeg.VideoLayers;
        Assert.Contains(layers, l => l.Name == "h");
        Assert.Contains(layers, l => l.Name == "l");
        Assert.Single(layers, l => l.Selected);
        Assert.True(Volatile.Read(ref arrived) > 0, "no pictures were passed on");
    }

    private static IVideoEncoder Encoder(int width, int height, int bitrate) =>
        VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
        {
            Width = width,
            Height = height,
            FramesPerSecond = 25,
            BitsPerSecond = bitrate,
        });

    /// <summary>The same picture at half the size, by taking every other pixel of every other row.</summary>
    private static byte[] Half(byte[] bgra)
    {
        var half = new byte[Width / 2 * (Height / 2) * 4];
        for (var y = 0; y < Height / 2; y++)
        {
            for (var x = 0; x < Width / 2; x++)
            {
                var from = ((y * 2 * Width) + (x * 2)) * 4;
                var to = ((y * (Width / 2)) + x) * 4;
                bgra.AsSpan(from, 4).CopyTo(half.AsSpan(to));
            }
        }

        return half;
    }

    [Fact]
    public void CamerasCanBeListedWithoutOpeningOne()
    {
        // Listing is safe to run anywhere: it never opens a device, so no light goes on and a machine
        // with no camera simply has none. Opening one is not tested — a CI runner has nothing to see.
        var cameras = VideoCapture.Cameras();
        Assert.NotNull(cameras);
        if (!VideoCapture.IsSupported)
        {
            Assert.Empty(cameras);
            return;
        }

        Assert.All(cameras, camera =>
        {
            Assert.False(string.IsNullOrWhiteSpace(camera.Id), "a camera has a system name to reopen it by");
            Assert.False(string.IsNullOrWhiteSpace(camera.Name), "a camera has a name to show");
        });
    }

    [Fact]
    public void ThePatternMovesAndIsThereOnEveryPlatform()
    {
        // No codec and no camera needed: this is the video equivalent of a test tone, and a machine
        // with neither should still be able to put something recognisable on a call.
        using var pattern = VideoCapture.OpenPattern(320, 240, framesPerSecond: 30);
        Assert.Equal("Test pattern", pattern.Name);

        var read = pattern.Read();
        Assert.NotNull(read);
        Assert.Equal(VideoPicture.Nv12Length(320, 240), read.Value.Data.Length);
        // A picture borrows the source's buffer until the next one, so keep a copy to compare with.
        var first = new VideoPicture(read.Value.Width, read.Value.Height, read.Value.Data.ToArray(), read.Value.Timestamp);
        var second = pattern.Read();
        Assert.NotNull(second);
        Assert.True(second.Value.Timestamp > first.Timestamp, "the pictures are paced");

        // Every frame differs from the last, which is what makes it worth more than a still picture.
        var changed = 0;
        for (var i = 0; i < 320 * 240; i++)
        {
            if (first.Data.Span[i] != second.Value.Data.Span[i])
            {
                changed++;
            }
        }

        Assert.True(changed > 500, $"only {changed} pixels moved between frames");

        // And it is a picture, not a flat colour: the bars cover the range.
        int low = 255, high = 0;
        for (var i = 0; i < 320 * 240; i++)
        {
            low = Math.Min(low, first.Data.Span[i]);
            high = Math.Max(high, first.Data.Span[i]);
        }

        Assert.True(high - low > 100, $"the picture only spans {high - low} of the brightness range");
    }

    [Fact]
    public void TheScreenCanBeRead()
    {
        Assert.SkipUnless(VideoCapture.IsSupported, "Screen capture is not wired up on this OS.");

        IVideoCaptureSource? opened = null;
        try
        {
            opened = VideoCapture.OpenScreen(wholeDesktop: false, width: 640, height: 360, framesPerSecond: 10);
        }
        catch (InvalidOperationException)
        {
            Assert.Skip("This machine has no screen to read — a headless agent, or a session with no desktop.");
        }

        using var screen = opened!;
        Assert.Equal(640, screen.Width);
        Assert.Equal(360, screen.Height);

        var picture = screen.Read();
        Assert.NotNull(picture);
        Assert.Equal(VideoPicture.Nv12Length(640, 360), picture.Value.Data.Length);

        // What is on the screen is the machine's business, not the test's: only that a whole picture
        // of the right size arrived, and that the second one came after the first.
        var second = screen.Read();
        Assert.NotNull(second);
        Assert.True(second.Value.Timestamp >= picture.Value.Timestamp);
    }

    /// <summary>A smooth sweep through the colours, which is what half-resolution colour is made for.</summary>
    private static byte[] Gradient()
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var at = ((y * Width) + x) * 4;
                bgra[at] = (byte)(x * 255 / Width);
                bgra[at + 1] = (byte)(y * 255 / Height);
                bgra[at + 2] = (byte)(((x + y) * 255) / (Width + Height));
                bgra[at + 3] = 255;
            }
        }

        return bgra;
    }

    /// <summary>Colour bars that move sideways, with a gradient down, so every frame differs from the last.</summary>
    private static byte[] Pattern(int frame)
    {
        var bgra = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var at = ((y * Width) + x) * 4;
                var bar = (x + (frame * 6)) % Width;
                bgra[at] = (byte)(bar < 100 ? 240 : 20);
                bgra[at + 1] = (byte)(y * 255 / Height);
                bgra[at + 2] = (byte)(bar > 200 ? 240 : 40);
                bgra[at + 3] = 255;
            }
        }

        return bgra;
    }
}
