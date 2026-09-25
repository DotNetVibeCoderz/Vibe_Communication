using System.Runtime.Versioning;

namespace VoipNet.Video.MediaFoundation;

/// <summary>H.264 decoding through the Media Foundation transform Windows ships with.</summary>
/// <remarks>
/// A decoder learns the picture size from the stream, not from the application, so the first frames
/// come back with the transform saying its output type has changed. That is handled here rather than
/// being passed on: the caller sees pictures, each one carrying the size it really is.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MediaFoundationH264Decoder : IVideoDecoder
{
    private const uint InprocServer = 1;
    private const int NotAccepting = unchecked((int)0xC00D_36B5);

    private readonly Lock _gate = new();
    private nint _transform;
    private int _width;
    private int _height;
    private int _stride;
    private uint _outputSize;
    private bool _providesSamples;
    private bool _disposed;

    internal MediaFoundationH264Decoder()
    {
        Mf.EnsureStarted();
        Mf.Check(Mf.CoCreateInstance(Mf.H264DecoderClass, 0, InprocServer, Mf.TransformInterface, out _transform), "creating the H.264 decoder");

        try
        {
            Configure();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public string Codec => "H264";

    /// <inheritdoc />
    public IReadOnlyList<VideoPicture> Decode(ReadOnlySpan<byte> frame, TimeSpan timestamp)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var pictures = new List<VideoPicture>();
            if (frame.IsEmpty)
            {
                return pictures;
            }

            var sample = CreateSample(frame, timestamp);
            try
            {
                var hr = Mf.ProcessInput(_transform, 0, sample, 0);
                if (hr == NotAccepting)
                {
                    Collect(pictures);
                    hr = Mf.ProcessInput(_transform, 0, sample, 0);
                }

                Mf.Check(hr, "ProcessInput");
            }
            finally
            {
                Mf.Release(ref sample);
            }

            Collect(pictures);
            return pictures;
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
            if (_transform != 0)
            {
                Mf.ProcessMessage(_transform, Mf.MessageNotifyEndStreaming, 0);
            }

            Mf.Release(ref _transform);
        }
    }

    private void Configure()
    {
        if (Mf.GetAttributes(_transform, out var attributes) == Mf.SOk)
        {
            Mf.SetUInt32(attributes, Mf.LowLatency, 1);
            Mf.Release(ref attributes);
        }

        Mf.Check(Mf.MFCreateMediaType(out var input), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(input, Mf.MajorType, Mf.MediaTypeVideo);
            Mf.SetGuid(input, Mf.Subtype, Mf.VideoFormatH264);
            Mf.SetUInt32(input, Mf.InterlaceMode, 2);
            Mf.Check(Mf.SetInputType(_transform, 0, input, 0), "SetInputType");
        }
        finally
        {
            Mf.Release(ref input);
        }

        SelectOutputType();
        Mf.Check(Mf.ProcessMessage(_transform, Mf.MessageNotifyBeginStreaming, 0), "NotifyBeginStreaming");
        Mf.ProcessMessage(_transform, Mf.MessageNotifyStartOfStream, 0);
    }

    /// <summary>Takes the transform's NV12 output type, and with it whatever size the stream turned out to be.</summary>
    private void SelectOutputType()
    {
        for (uint index = 0; ; index++)
        {
            if (Mf.GetOutputAvailableType(_transform, 0, index, out var type) != Mf.SOk)
            {
                throw new InvalidOperationException("The H.264 decoder offers no NV12 output.");
            }

            try
            {
                if (Mf.GetItemGuid(type, Mf.Subtype) != Mf.VideoFormatNv12)
                {
                    continue;
                }

                Mf.Check(Mf.SetOutputType(_transform, 0, type, 0), "SetOutputType");
            }
            finally
            {
                Mf.Release(ref type);
            }

            break;
        }

        ReadOutputSize();
    }

    private void ReadOutputSize()
    {
        Mf.Check(Mf.GetOutputCurrentType(_transform, 0, out var current), "GetOutputCurrentType");
        try
        {
            if (Mf.GetUInt64(current, Mf.FrameSize, out var size) == Mf.SOk)
            {
                _width = (int)(size >> 32);
                _height = (int)(size & 0xFFFF_FFFF);
            }

            _stride = Mf.GetUInt32(current, Mf.DefaultStride, out var stride) == Mf.SOk ? Math.Abs((int)stride) : _width;
        }
        finally
        {
            Mf.Release(ref current);
        }

        Mf.Check(Mf.GetOutputStreamInfo(_transform, 0, out var info), "GetOutputStreamInfo");
        _providesSamples = (info.Flags & Mf.OutputStreamProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, (uint)Math.Max(VideoPicture.Nv12Length(Math.Max(_stride, _width), _height), 1));
    }

    private nint CreateSample(ReadOnlySpan<byte> frame, TimeSpan timestamp)
    {
        Mf.Check(Mf.MFCreateMemoryBuffer((uint)frame.Length, out var buffer), "MFCreateMemoryBuffer");
        nint sample = 0;
        try
        {
            Mf.Check(Mf.Lock(buffer, out var data, out _, out _), "IMFMediaBuffer::Lock");
            frame.CopyTo(new Span<byte>(data, frame.Length));
            Mf.Unlock(buffer);
            Mf.SetCurrentLength(buffer, (uint)frame.Length);

            Mf.Check(Mf.MFCreateSample(out sample), "MFCreateSample");
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, timestamp.Ticks);
            return sample;
        }
        catch
        {
            Mf.Release(ref sample);
            throw;
        }
        finally
        {
            Mf.Release(ref buffer);
        }
    }

    private void Collect(List<VideoPicture> pictures)
    {
        while (true)
        {
            nint sample = 0;
            if (!_providesSamples)
            {
                Mf.Check(Mf.MFCreateSample(out sample), "MFCreateSample");
                Mf.Check(Mf.MFCreateMemoryBuffer(Math.Max(_outputSize, 4096), out var buffer), "MFCreateMemoryBuffer");
                Mf.AddBuffer(sample, buffer);
                Mf.Release(ref buffer);
            }

            var data = new Mf.OutputDataBuffer { StreamId = 0, Sample = sample };
            var hr = Mf.ProcessOutput(_transform, 0, ref data, out _);
            if (hr == Mf.NeedMoreInput)
            {
                Mf.Release(ref sample);
                return;
            }

            if (hr == Mf.StreamChange)
            {
                // The stream said how big the picture is, which it could not know before.
                Mf.Release(ref sample);
                SelectOutputType();
                continue;
            }

            try
            {
                Mf.Check(hr, "ProcessOutput");
                var produced = data.Sample;
                if (produced == 0)
                {
                    return;
                }

                Mf.GetSampleTime(produced, out var time);
                var picture = Read(produced);
                if (!picture.IsEmpty)
                {
                    pictures.Add(new VideoPicture(_width, _height, picture, TimeSpan.FromTicks(time)));
                }
            }
            finally
            {
                if (data.Sample != sample)
                {
                    var produced = data.Sample;
                    Mf.Release(ref produced);
                }

                Mf.Release(ref sample);
                if (data.Events != 0)
                {
                    var events = data.Events;
                    Mf.Release(ref events);
                }
            }
        }
    }

    /// <summary>Copies one picture out, dropping the padding a decoder leaves at the end of each row.</summary>
    private ReadOnlyMemory<byte> Read(nint sample)
    {
        if (_width <= 0 || _height <= 0)
        {
            return default;
        }

        Mf.Check(Mf.ConvertToContiguousBuffer(sample, out var buffer), "ConvertToContiguousBuffer");
        try
        {
            Mf.Check(Mf.Lock(buffer, out var data, out _, out var length), "IMFMediaBuffer::Lock");
            try
            {
                var stride = _stride > 0 ? _stride : _width;
                var source = new ReadOnlySpan<byte>(data, (int)length);
                var picture = new byte[VideoPicture.Nv12Length(_width, _height)];
                var rows = _height + (_height / 2); // luma, then the half-height colour plane
                if (source.Length < rows * stride)
                {
                    return default;
                }

                for (var row = 0; row < rows; row++)
                {
                    source.Slice(row * stride, _width).CopyTo(picture.AsSpan(row * _width));
                }

                return picture;
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
