using System.Diagnostics;

namespace VoipNet.Video;

/// <summary>
/// A moving picture generated on the spot: colour bars, a sweeping hand and a counter.
/// </summary>
/// <remarks>
/// The video equivalent of a test tone. A machine with no camera — a server, a CI runner, a laptop
/// with the shutter closed — can still put something recognisable on a call, and because every frame
/// differs from the last in a known way, it is worth far more to a test than a still picture would be.
/// </remarks>
internal sealed class PatternSource : IVideoCaptureSource
{
    /// <summary>Bar colours, as BGR: white, yellow, cyan, green, magenta, red, blue.</summary>
    private static readonly (byte B, byte G, byte R)[] Bars =
    [
        (235, 235, 235), (16, 235, 235), (235, 235, 16), (16, 235, 16),
        (235, 16, 235), (16, 16, 235), (235, 16, 16),
    ];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _interval;
    private readonly byte[] _bgra;
    private readonly byte[] _nv12;
    private TimeSpan _next;
    private int _frame;
    private bool _disposed;

    internal PatternSource(int width, int height, int framesPerSecond)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("A picture with half-resolution colour has an even width and height.", nameof(width));
        }

        Width = width;
        Height = height;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(framesPerSecond, 1, 60));
        _bgra = new byte[width * height * 4];
        _nv12 = new byte[VideoPicture.Nv12Length(width, height)];
    }

    /// <inheritdoc />
    public string Name => "Test pattern";

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public VideoPicture? Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Paced like a camera, so a caller that loops on it does not spin.
        var wait = _next - _clock.Elapsed;
        if (wait > TimeSpan.Zero)
        {
            Thread.Sleep(wait);
        }

        _next = _clock.Elapsed + _interval;
        Draw(_frame++);
        VideoPictures.FromBgra(_bgra, Width, Height, _nv12);
        return new VideoPicture(Width, Height, _nv12, _clock.Elapsed);
    }

    /// <inheritdoc />
    public void Dispose() => _disposed = true;

    /// <summary>Bars down the frame, a band that slides across them, and a corner that counts.</summary>
    private void Draw(int frame)
    {
        var band = (frame * 6) % Width;
        var bandWidth = Math.Max(Width / 12, 8);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var (b, g, r) = Bars[x * Bars.Length / Width];
                if (y > Height * 3 / 4)
                {
                    // A grey ramp along the bottom, which shows banding in an encoder at a glance.
                    var value = (byte)(16 + (x * 219 / Math.Max(Width - 1, 1)));
                    (b, g, r) = (value, value, value);
                }

                var inBand = x >= band && x < band + bandWidth;
                if (inBand)
                {
                    (b, g, r) = ((byte)(255 - b), (byte)(255 - g), (byte)(255 - r));
                }

                var at = ((y * Width) + x) * 4;
                _bgra[at] = b;
                _bgra[at + 1] = g;
                _bgra[at + 2] = r;
                _bgra[at + 3] = 255;
            }
        }

        // A block in the corner that changes every frame, so a still picture cannot be mistaken for
        // a moving one: its brightness is the frame count.
        var size = Math.Max(Math.Min(Width, Height) / 8, 8);
        var shade = (byte)(16 + (frame * 7 % 220));
        for (var y = 0; y < size && y < Height; y++)
        {
            for (var x = 0; x < size && x < Width; x++)
            {
                var at = ((y * Width) + x) * 4;
                _bgra[at] = shade;
                _bgra[at + 1] = shade;
                _bgra[at + 2] = shade;
            }
        }
    }
}
