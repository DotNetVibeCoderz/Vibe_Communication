namespace VoipNet.Video;

/// <summary>
/// Turning pictures between the form a screen uses and the form a codec takes.
/// </summary>
/// <remarks>
/// Codecs want NV12: brightness at full resolution, colour at half, because an eye notices a blurred
/// edge far more than a blurred colour. Screens want BGRA. The numbers here are the BT.601 studio
/// range, which is what H.264 means by default and what a browser assumes when the stream says
/// nothing else, so a picture that goes out and comes back looks the same.
/// </remarks>
public static class VideoPictures
{
    /// <summary>Converts a BGRA picture (as a window or a camera gives it) to NV12.</summary>
    /// <param name="bgra">Source pixels, four bytes each, top row first.</param>
    /// <param name="width">Width in pixels; must be even.</param>
    /// <param name="height">Height in pixels; must be even.</param>
    /// <param name="nv12">Destination, <see cref="VideoPicture.Nv12Length"/> bytes.</param>
    public static void FromBgra(ReadOnlySpan<byte> bgra, int width, int height, Span<byte> nv12)
    {
        Check(width, height);
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, width * height * 4, nameof(bgra));
        ArgumentOutOfRangeException.ThrowIfLessThan(nv12.Length, VideoPicture.Nv12Length(width, height), nameof(nv12));

        var chroma = nv12[(width * height)..];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = ((y * width) + x) * 4;
                int b = bgra[at];
                int g = bgra[at + 1];
                int r = bgra[at + 2];
                nv12[(y * width) + x] = (byte)(16 + (((16_829 * r) + (33_039 * g) + (6_416 * b)) >> 16));

                // One pair of colour values for each two-by-two block, averaged over all four pixels:
                // taking one of them would tint a whole block with whatever happened to be in its corner.
                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    var below = at + (width * 4);
                    r += bgra[at + 6] + bgra[below + 2] + bgra[below + 6];
                    g += bgra[at + 5] + bgra[below + 1] + bgra[below + 5];
                    b += bgra[at + 4] + bgra[below] + bgra[below + 4];
                    var at2 = ((y / 2) * width) + x;
                    chroma[at2] = Clamp(128 + ((-(9_713 * r) - (19_071 * g) + (28_784 * b)) >> 18));
                    chroma[at2 + 1] = Clamp(128 + (((28_784 * r) - (24_103 * g) - (4_681 * b)) >> 18));
                }
            }
        }
    }

    /// <summary>Converts an NV12 picture back to BGRA, for showing on screen.</summary>
    /// <param name="nv12">Source planes.</param>
    /// <param name="width">Width in pixels; must be even.</param>
    /// <param name="height">Height in pixels; must be even.</param>
    /// <param name="bgra">Destination, four bytes per pixel.</param>
    public static void ToBgra(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra)
    {
        Check(width, height);
        ArgumentOutOfRangeException.ThrowIfLessThan(nv12.Length, VideoPicture.Nv12Length(width, height), nameof(nv12));
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, width * height * 4, nameof(bgra));

        var chroma = nv12[(width * height)..];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var luma = 298 * (nv12[(y * width) + x] - 16);
                var at2 = ((y / 2) * width) + (x & ~1);
                var u = chroma[at2] - 128;
                var v = chroma[at2 + 1] - 128;
                var at = ((y * width) + x) * 4;
                bgra[at] = Clamp((luma + (516 * u) + 128) >> 8);
                bgra[at + 1] = Clamp((luma - (100 * u) - (208 * v) + 128) >> 8);
                bgra[at + 2] = Clamp((luma + (409 * v) + 128) >> 8);
                bgra[at + 3] = 255;
            }
        }
    }

    private static void Check(int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("A picture with half-resolution colour has an even width and height.");
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
