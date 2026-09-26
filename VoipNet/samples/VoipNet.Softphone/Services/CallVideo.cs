using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VoipNet.Video;

namespace VoipNet.Softphone.Services;

/// <summary>
/// The picture either way on a call: the camera encoded and sent, and what arrives decoded and put on
/// screen.
/// </summary>
/// <remarks>
/// Two threads do the work so neither waits for the other. Capture blocks on the camera, which paces
/// itself, and hands each picture to the encoder. Decoding runs on its own thread with a short queue:
/// when the screen falls behind, the oldest picture is dropped rather than the newest, because on a
/// call an old picture is worth nothing.
/// </remarks>
public sealed class CallVideo : IDisposable
{
    /// <summary>How many arrived frames may wait to be decoded before the oldest is given up.</summary>
    private const int Backlog = 4;

    private readonly VoipCall _call;
    private readonly BlockingCollection<byte[]> _arrived = new(Backlog);
    private readonly PictureSurface _remote = new();
    private readonly PictureSurface _local = new();
    private IVideoCaptureSource? _camera;
    private IVideoEncoder? _encoder;
    private Thread? _captureThread;
    private Thread? _decodeThread;
    private Thread? _screenThread;
    private volatile bool _running;
    private volatile bool _sharing;

    public CallVideo(VoipCall call)
    {
        _call = call;
    }

    /// <summary>Whether this machine has the codec and the camera to do any of this.</summary>
    public static bool IsSupported => VideoCodecs.IsH264Available;

    /// <summary>Send the test pattern rather than a camera, for a run with nobody in front of one.</summary>
    public static bool TestPattern { get; set; }

    private static IVideoCaptureSource OpenCameraOrPattern()
    {
        try
        {
            return VideoCapture.OpenCamera(width: 640, height: 360, framesPerSecond: 30);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // No camera, or somebody else has it: the call still gets a picture.
            return VideoCapture.OpenPattern(640, 360, framesPerSecond: 15);
        }
    }

    /// <summary>Raised on the UI thread when either picture has changed.</summary>
    public event Action? Changed;

    /// <summary>The far end's picture, or null until one arrives.</summary>
    public Bitmap? Remote => _remote.Current;

    /// <summary>This end's own picture, as it is being sent.</summary>
    public Bitmap? Local => _local.Current;

    /// <summary>What the camera is called, for the label under the self-view.</summary>
    public string CameraName { get; private set; } = string.Empty;

    /// <summary>Whether the screen is going out as a second stream.</summary>
    public bool IsSharingScreen => _sharing;

    /// <summary>Starts sending the camera and showing what arrives. Safe to call twice.</summary>
    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _call.VideoFrameReceived += OnVideoFrame;
        _call.KeyframeRequested += OnKeyframeRequested;
        _decodeThread = new Thread(Decode) { IsBackground = true, Name = "softphone video decode" };
        _decodeThread.Start();
        _captureThread = new Thread(Capture) { IsBackground = true, Name = "softphone camera" };
        _captureThread.Start();
    }

    /// <summary>
    /// Offers the screen as a second video stream (<c>a=content:slides</c>), which is a re-INVITE:
    /// the camera carries on untouched on the first stream while this one is added beside it.
    /// </summary>
    public void ShareScreen()
    {
        if (_sharing || !_running)
        {
            return;
        }

        _sharing = true;
        _call.ShareScreen();
        _screenThread = new Thread(CaptureScreen) { IsBackground = true, Name = "softphone screen" };
        _screenThread.Start();
    }

    /// <summary>Withdraws the screen stream, leaving the call and its camera as they were.</summary>
    public void StopSharingScreen()
    {
        if (!_sharing)
        {
            return;
        }

        _sharing = false;
        _screenThread?.Join(TimeSpan.FromSeconds(2));
        _screenThread = null;
        if (_call.IsActive)
        {
            _call.StopScreenShare();
        }
    }

    /// <summary>Stops both directions and puts the camera light out.</summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        StopSharingScreen();
        _running = false;
        _call.VideoFrameReceived -= OnVideoFrame;
        _call.KeyframeRequested -= OnKeyframeRequested;
        _arrived.CompleteAdding();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _decodeThread?.Join(TimeSpan.FromSeconds(2));
        _camera?.Dispose();
        _encoder?.Dispose();
        _camera = null;
        _encoder = null;
        CameraName = string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _arrived.Dispose();
    }

    private void OnKeyframeRequested(object? sender, bool full) => _encoder?.RequestKeyframe();

    private void OnVideoFrame(VoipCall call, uint timestamp, bool keyframe, ReadOnlySpan<byte> frame, string content)
    {
        if (!_running || content != "main")
        {
            return;
        }

        // A frame nobody has decoded yet is worth less than the one that just arrived.
        while (_arrived.Count >= Backlog && _arrived.TryTake(out _))
        {
        }

        _arrived.TryAdd(frame.ToArray());
    }

    private void Capture()
    {
        try
        {
            // A machine with no camera — a server, a laptop with the shutter closed, a headless run
            // taking the documentation screenshots — still has something to send.
            _camera = TestPattern || !VideoCapture.IsSupported
                ? VideoCapture.OpenPattern(640, 360, framesPerSecond: 15)
                : OpenCameraOrPattern();
            CameraName = _camera.Name;
            _encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
            {
                Width = _camera.Width,
                Height = _camera.Height,
                FramesPerSecond = 30,
                BitsPerSecond = 900_000,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // No camera, or somebody else has it: the call carries on with sound only.
            _running = false;
            return;
        }

        var started = DateTime.UtcNow;
        while (_running && _call.IsActive)
        {
            var picture = _camera.Read();
            if (picture is not { } frame)
            {
                break;
            }

            _local.Write(frame);
            Dispatcher.UIThread.Post(() => Changed?.Invoke());

            var elapsed = DateTime.UtcNow - started;
            foreach (var encoded in _encoder.Encode(frame))
            {
                try
                {
                    _call.SendVideoFrame((uint)(elapsed.TotalSeconds * 90_000), encoded.Data.Span);
                }
                catch (VoipException)
                {
                    // The call ended between one frame and the next, which is how calls end.
                    return;
                }
            }
        }
    }

    /// <summary>Copies the screen and sends it on the second stream until the share is stopped.</summary>
    private void CaptureScreen()
    {
        try
        {
            using var screen = VideoCapture.OpenScreen(wholeDesktop: false, width: 1280, height: 720, framesPerSecond: 8);
            using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
            {
                Width = screen.Width,
                Height = screen.Height,
                FramesPerSecond = 8,
                BitsPerSecond = 1_200_000,
                Content = VideoContent.Detail,
            });

            while (_sharing && _running && _call.IsActive)
            {
                if (screen.Read() is not { } picture)
                {
                    return;
                }

                foreach (var frame in encoder.Encode(picture))
                {
                    if (!_sharing)
                    {
                        return;
                    }

                    try
                    {
                        _call.SendVideoFrame((uint)(picture.Timestamp.TotalSeconds * 90_000), frame.Data.Span, "slides");
                    }
                    catch (VoipException)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            // No screen to read here: the call carries on with the camera alone.
            _sharing = false;
        }
    }

    private void Decode()
    {
        using var decoder = VideoCodecs.CreateH264Decoder();
        foreach (var frame in _arrived.GetConsumingEnumerable())
        {
            if (!_running)
            {
                return;
            }

            foreach (var picture in decoder.Decode(frame, TimeSpan.Zero))
            {
                _remote.Write(picture);
                Dispatcher.UIThread.Post(() => Changed?.Invoke());
            }
        }
    }
}

/// <summary>
/// A picture on its way to the screen. Two bitmaps take turns: one is drawn while the other is
/// written, so a half-written picture is never on screen and the control sees a new value each time.
/// </summary>
internal sealed class PictureSurface
{
    private readonly Lock _gate = new();
    private WriteableBitmap?[] _bitmaps = new WriteableBitmap?[2];
    private byte[] _bgra = [];
    private int _width;
    private int _height;
    private int _next;

    internal Bitmap? Current { get; private set; }

    internal unsafe void Write(VideoPicture picture)
    {
        lock (_gate)
        {
            if (picture.Width != _width || picture.Height != _height)
            {
                _width = picture.Width;
                _height = picture.Height;
                _bgra = new byte[_width * _height * 4];
                _bitmaps = [Create(_width, _height), Create(_width, _height)];
            }

            var bitmap = _bitmaps[_next];
            _next = 1 - _next;
            if (bitmap is null)
            {
                return;
            }

            VideoPictures.ToBgra(picture.Data.Span, _width, _height, _bgra);
            using (var buffer = bitmap.Lock())
            {
                var row = _width * 4;
                for (var y = 0; y < _height; y++)
                {
                    _bgra.AsSpan(y * row, row).CopyTo(new Span<byte>((byte*)buffer.Address + (y * buffer.RowBytes), row));
                }
            }

            Current = bitmap;
        }
    }

    private static WriteableBitmap Create(int width, int height) =>
        new(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
}
