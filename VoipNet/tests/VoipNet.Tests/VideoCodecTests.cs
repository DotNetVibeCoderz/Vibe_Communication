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
