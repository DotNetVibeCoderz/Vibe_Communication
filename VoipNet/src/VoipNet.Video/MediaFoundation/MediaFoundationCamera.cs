using System.Runtime.Versioning;

namespace VoipNet.Video.MediaFoundation;

/// <summary>A camera read through Media Foundation's source reader.</summary>
/// <remarks>
/// Reading is a pull: ask for the next picture and the camera's own pacing decides when it comes.
/// That keeps the thread that encodes and the thread that captures the same one, which is the
/// simplest thing that can work in a call, and it means no frame is ever queued behind a slow encoder.
/// The reader is asked to hand over NV12 at the size wanted; it inserts a converter and a scaler when
/// the device cannot do that itself, so a camera that only offers MJPEG still works.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MediaFoundationCamera : IVideoCaptureSource
{
    private readonly Lock _gate = new();
    private nint _source;
    private nint _reader;
    private int _stride;
    private byte[] _picture = [];
    private bool _disposed;

    internal MediaFoundationCamera(string symbolicLink, string name, int width, int height, int framesPerSecond)
    {
        Mf.EnsureStarted();
        Name = name;
        Width = width;
        Height = height;

        try
        {
            _source = Activate(symbolicLink);
            _reader = CreateReader(_source);
            Configure(width, height, framesPerSecond);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public int Width { get; private set; }

    /// <inheritdoc />
    public int Height { get; private set; }

    /// <inheritdoc />
    public VideoPicture? Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (true)
            {
                Mf.Check(Mf.ReadSample(_reader, Mf.FirstVideoStream, out var flags, out var timestamp, out var sample), "ReadSample");
                if ((flags & Mf.StreamEndOfStream) != 0)
                {
                    return null;
                }

                if (sample == 0)
                {
                    // The device is warming up, or the format changed: ask again rather than guess.
                    continue;
                }

                try
                {
                    var picture = Read(sample);
                    if (picture.IsEmpty)
                    {
                        continue;
                    }

                    return new VideoPicture(Width, Height, picture, TimeSpan.FromTicks(timestamp));
                }
                finally
                {
                    Mf.Release(ref sample);
                }
            }
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
            Mf.Release(ref _reader);
            if (_source != 0)
            {
                // Shut the device down rather than only letting go of it, so the light goes out now.
                Mf.ShutdownSource(_source);
                Mf.Release(ref _source);
            }
        }
    }

    /// <summary>The cameras this machine has, as the operating system describes them.</summary>
    internal static List<VideoCaptureDevice> Devices()
    {
        Mf.EnsureStarted();
        Mf.Check(Mf.MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
        try
        {
            Mf.SetGuid(attributes, Mf.DeviceSourceType, Mf.DeviceSourceVideo);
            Mf.Check(Mf.MFEnumDeviceSources(attributes, out var devices, out var count), "MFEnumDeviceSources");
            var found = new List<VideoCaptureDevice>((int)count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var device = devices[i];
                    found.Add(new VideoCaptureDevice(
                        Mf.GetItemString(device, Mf.DeviceSymbolicLink),
                        Mf.GetItemString(device, Mf.DeviceFriendlyName)));
                    Mf.Release(ref device);
                }
            }
            finally
            {
                Mf.CoTaskMemFree((nint)devices);
            }

            return found;
        }
        finally
        {
            Mf.Release(ref attributes);
        }
    }

    private static nint Activate(string symbolicLink)
    {
        Mf.Check(Mf.MFCreateAttributes(out var attributes, 2), "MFCreateAttributes");
        try
        {
            Mf.SetGuid(attributes, Mf.DeviceSourceType, Mf.DeviceSourceVideo);
            if (symbolicLink.Length > 0)
            {
                Mf.SetString(attributes, Mf.DeviceSymbolicLink, symbolicLink);
            }

            Mf.Check(Mf.MFEnumDeviceSources(attributes, out var devices, out var count), "MFEnumDeviceSources");
            try
            {
                if (count == 0)
                {
                    throw new InvalidOperationException("This machine has no camera.");
                }

                var device = devices[0];
                try
                {
                    Mf.Check(Mf.ActivateObject(device, Mf.MediaSourceInterface, out var source), "activating the camera");
                    return source;
                }
                finally
                {
                    Mf.Release(ref device);
                    for (var i = 1; i < count; i++)
                    {
                        var other = devices[i];
                        Mf.Release(ref other);
                    }
                }
            }
            finally
            {
                Mf.CoTaskMemFree((nint)devices);
            }
        }
        finally
        {
            Mf.Release(ref attributes);
        }
    }

    private static nint CreateReader(nint source)
    {
        Mf.Check(Mf.MFCreateAttributes(out var attributes, 1), "MFCreateAttributes");
        try
        {
            // Let the reader convert and scale: a camera that only offers MJPEG or YUY2 still gives NV12.
            Mf.SetUInt32(attributes, Mf.EnableVideoProcessing, 1);
            Mf.Check(Mf.MFCreateSourceReaderFromMediaSource(source, attributes, out var reader), "MFCreateSourceReaderFromMediaSource");
            return reader;
        }
        finally
        {
            Mf.Release(ref attributes);
        }
    }

    private void Configure(int width, int height, int framesPerSecond)
    {
        Mf.Check(Mf.SetStreamSelection(_reader, Mf.FirstVideoStream, true), "SetStreamSelection");
        Mf.Check(Mf.MFCreateMediaType(out var type), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(type, Mf.MajorType, Mf.MediaTypeVideo);
            Mf.SetGuid(type, Mf.Subtype, Mf.VideoFormatNv12);
            Mf.SetUInt64(type, Mf.FrameSize, Mf.Pack(width, height));
            Mf.SetUInt64(type, Mf.FrameRate, Mf.Pack(framesPerSecond, 1));
            Mf.SetUInt32(type, Mf.InterlaceMode, 2);
            if (Mf.SetCurrentMediaType(_reader, Mf.FirstVideoStream, type) < 0)
            {
                // The size was refused: take what the camera does offer and let the caller scale.
                Mf.Check(Mf.MFCreateMediaType(out var plain), "MFCreateMediaType");
                try
                {
                    Mf.SetGuid(plain, Mf.MajorType, Mf.MediaTypeVideo);
                    Mf.SetGuid(plain, Mf.Subtype, Mf.VideoFormatNv12);
                    Mf.Check(Mf.SetCurrentMediaType(_reader, Mf.FirstVideoStream, plain), "SetCurrentMediaType");
                }
                finally
                {
                    Mf.Release(ref plain);
                }
            }
        }
        finally
        {
            Mf.Release(ref type);
        }

        Mf.Check(Mf.GetCurrentMediaType(_reader, Mf.FirstVideoStream, out var current), "GetCurrentMediaType");
        try
        {
            if (Mf.GetUInt64(current, Mf.FrameSize, out var size) == Mf.SOk)
            {
                Width = (int)(size >> 32);
                Height = (int)(size & 0xFFFF_FFFF);
            }

            _stride = Mf.GetUInt32(current, Mf.DefaultStride, out var stride) == Mf.SOk ? Math.Abs((int)stride) : Width;
        }
        finally
        {
            Mf.Release(ref current);
        }
    }

    /// <summary>Copies one picture out, dropping the padding the device leaves at the end of each row.</summary>
    private ReadOnlyMemory<byte> Read(nint sample)
    {
        if (Width <= 0 || Height <= 0)
        {
            return default;
        }

        Mf.Check(Mf.ConvertToContiguousBuffer(sample, out var buffer), "ConvertToContiguousBuffer");
        try
        {
            Mf.Check(Mf.Lock(buffer, out var data, out _, out var length), "IMFMediaBuffer::Lock");
            try
            {
                var stride = _stride > 0 ? _stride : Width;
                var rows = Height + (Height / 2);
                var source = new ReadOnlySpan<byte>(data, (int)length);
                if (source.Length < rows * stride)
                {
                    return default;
                }

                var wanted = VideoPicture.Nv12Length(Width, Height);
                if (_picture.Length != wanted)
                {
                    _picture = new byte[wanted];
                }

                for (var row = 0; row < rows; row++)
                {
                    source.Slice(row * stride, Width).CopyTo(_picture.AsSpan(row * Width));
                }

                return _picture;
            }
            finally
            {
                Mf.Unlock(buffer);
            }
        }
        finally
        {
            Mf.Release(ref buffer);
        }
    }
}
