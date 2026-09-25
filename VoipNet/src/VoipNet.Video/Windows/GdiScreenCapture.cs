using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VoipNet.Video.Windows;

/// <summary>The screen, copied a frame at a time with the drawing API Windows has always had.</summary>
/// <remarks>
/// A screen share is mostly still: slides, a document, a terminal. Copying the desktop with
/// <c>StretchBlt</c> works on every Windows machine, including over remote sessions and on GPUs that
/// refuse to share a surface, and asks nothing of the caller.
///
/// What it costs is whatever the display driver charges for reading the screen back, and that varies
/// enormously: a plain local display answers in a few milliseconds, while some virtual and remote
/// displays take a third of a second per copy no matter how small the rectangle asked for. The rate
/// given is therefore a ceiling rather than a promise — <see cref="Read"/> returns as soon as the copy
/// is done. Desktop Duplication is the fast path everywhere and is not wired up; see PLAN 1.3.
///
/// The picture is scaled to the size asked for on the way through, because a 4K desktop sent whole
/// would cost more to encode than anybody has bandwidth for, and it is paced here: the desktop has no
/// frame rate of its own, so the reader waits between copies rather than spinning.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class GdiScreenCapture : IVideoCaptureSource
{
    private const int SrcCopy = 0x00CC_0020;
    private const int Halftone = 4;
    private const int VirtualLeft = 76;
    private const int VirtualTop = 77;
    private const int VirtualWidth = 78;
    private const int VirtualHeight = 79;

    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _interval;
    private readonly bool _wholeDesktop;
    private readonly byte[] _bgra;
    private readonly byte[] _nv12;
    private nint _screenDc;
    private nint _memoryDc;
    private nint _bitmap;
    private nint _previous;
    private TimeSpan _next;
    private bool _disposed;

    internal GdiScreenCapture(bool wholeDesktop, int width, int height, int framesPerSecond)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException("A picture with half-resolution colour has an even width and height.", nameof(width));
        }

        Width = width;
        Height = height;
        _wholeDesktop = wholeDesktop;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(framesPerSecond, 1, 60));
        _bgra = new byte[width * height * 4];
        _nv12 = new byte[VideoPicture.Nv12Length(width, height)];

        _screenDc = GetDC(0);
        if (_screenDc == 0)
        {
            throw new InvalidOperationException("The screen could not be opened for reading.");
        }

        _memoryDc = CreateCompatibleDC(_screenDc);
        _bitmap = CreateCompatibleBitmap(_screenDc, width, height);
        if (_memoryDc == 0 || _bitmap == 0)
        {
            Dispose();
            throw new InvalidOperationException("The screen could not be copied into memory.");
        }

        _previous = SelectObject(_memoryDc, _bitmap);
        SetStretchBltMode(_memoryDc, Halftone);
    }

    /// <inheritdoc />
    public string Name => _wholeDesktop ? "All screens" : "Screen";

    /// <inheritdoc />
    public int Width { get; }

    /// <inheritdoc />
    public int Height { get; }

    /// <inheritdoc />
    public VideoPicture? Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The desktop does not produce frames; this decides when one is taken.
            var wait = _next - _clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }

            _next = _clock.Elapsed + _interval;

            var (left, top, width, height) = Bounds();
            if (!StretchBlt(_memoryDc, 0, 0, Width, Height, _screenDc, left, top, width, height, SrcCopy))
            {
                // A locked workstation or a secure desktop refuses the copy; the share pauses rather
                // than ending, because the screen comes back when the person does.
                return new VideoPicture(Width, Height, _nv12, _clock.Elapsed);
            }

            var info = new BitmapInfoHeader
            {
                Size = 40,
                Width = Width,
                // Negative: rows top down, the way every other picture in this package is stored.
                Height = -Height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
            };

            if (GetDIBits(_memoryDc, _bitmap, 0, (uint)Height, _bgra, ref info, 0) == 0)
            {
                return null;
            }

            VideoPictures.FromBgra(_bgra, Width, Height, _nv12);
            return new VideoPicture(Width, Height, _nv12, _clock.Elapsed);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_previous != 0)
            {
                SelectObject(_memoryDc, _previous);
            }

            if (_bitmap != 0)
            {
                DeleteObject(_bitmap);
            }

            if (_memoryDc != 0)
            {
                DeleteDC(_memoryDc);
            }

            if (_screenDc != 0)
            {
                ReleaseDC(0, _screenDc);
            }

            _bitmap = 0;
            _memoryDc = 0;
            _screenDc = 0;
            _previous = 0;
        }
    }

    /// <summary>What to copy: every screen side by side, or the main one.</summary>
    private (int Left, int Top, int Width, int Height) Bounds() => _wholeDesktop
        ? (GetSystemMetrics(VirtualLeft), GetSystemMetrics(VirtualTop), GetSystemMetrics(VirtualWidth), GetSystemMetrics(VirtualHeight))
        : (0, 0, GetSystemMetrics(0), GetSystemMetrics(1));

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPixelsPerMeter;
        internal int YPixelsPerMeter;
        internal uint ColoursUsed;
        internal uint ColoursImportant;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint handle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StretchBlt(
        nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int operation);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
