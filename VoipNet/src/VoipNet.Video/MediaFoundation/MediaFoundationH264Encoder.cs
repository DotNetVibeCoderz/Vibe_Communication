using System.Runtime.Versioning;

namespace VoipNet.Video.MediaFoundation;

/// <summary>
/// H.264 encoding through the Media Foundation transform Windows ships with, which uses the GPU
/// where the driver offers one and its own software encoder where it does not.
/// </summary>
/// <remarks>
/// The transform is set up for a call rather than for a file: low latency on, constant bitrate, and
/// no B-frames, so a picture goes out as soon as it is encoded and arrives in the order it was taken.
/// Output is the Annex B byte stream the RTP packetiser takes, one access unit per sample.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MediaFoundationH264Encoder : IVideoEncoder
{
    private const uint InprocServer = 1;
    private const int NotAccepting = unchecked((int)0xC00D_36B5);

    private readonly VideoEncoderOptions _options;
    private readonly Lock _gate = new();
    private nint _transform;
    private nint _codec;
    private uint _outputSize;
    private bool _providesSamples;
    private bool _forceKeyframe;
    private bool _disposed;

    internal MediaFoundationH264Encoder(VideoEncoderOptions options)
    {
        if (options.Width <= 0 || options.Height <= 0 || options.Width % 2 != 0 || options.Height % 2 != 0)
        {
            throw new ArgumentException("An H.264 picture has an even width and height.", nameof(options));
        }

        _options = options;
        Mf.EnsureStarted();
        Mf.Check(Mf.CoCreateInstance(Mf.H264EncoderClass, 0, InprocServer, Mf.TransformInterface, out _transform), "creating the H.264 encoder");

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
    public int Width => _options.Width;

    /// <inheritdoc />
    public int Height => _options.Height;

    /// <inheritdoc />
    public void RequestKeyframe()
    {
        lock (_gate)
        {
            _forceKeyframe = true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EncodedVideoFrame> Encode(VideoPicture picture)
    {
        var expected = VideoPicture.Nv12Length(_options.Width, _options.Height);
        if (picture.Data.Length != expected)
        {
            throw new ArgumentException($"An NV12 picture of {_options.Width}x{_options.Height} is {expected} bytes, not {picture.Data.Length}.", nameof(picture));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var frames = new List<EncodedVideoFrame>();
            if (_forceKeyframe && _codec != 0)
            {
                // Answering a PLI: the next picture has to be one a decoder can start on.
                Mf.SetCodecUInt32(_codec, Mf.ForceKeyFrame, 1);
                _forceKeyframe = false;
            }

            var sample = CreateSample(picture.Data.Span, picture.Timestamp);
            try
            {
                var hr = Mf.ProcessInput(_transform, 0, sample, 0);
                if (hr == NotAccepting)
                {
                    // The transform is holding finished work: take it, then the picture is welcome.
                    Collect(frames);
                    hr = Mf.ProcessInput(_transform, 0, sample, 0);
                }

                Mf.Check(hr, "ProcessInput");
            }
            finally
            {
                Mf.Release(ref sample);
            }

            Collect(frames);
            return frames;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EncodedVideoFrame> Drain()
    {
        lock (_gate)
        {
            var frames = new List<EncodedVideoFrame>();
            if (_disposed)
            {
                return frames;
            }

            Mf.ProcessMessage(_transform, Mf.MessageNotifyEndOfStream, 0);
            Mf.ProcessMessage(_transform, Mf.MessageCommandDrain, 0);
            Collect(frames);
            return frames;
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

            Mf.Release(ref _codec);
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

        // The output type comes first: this encoder decides what input it will take from what it has
        // been asked to produce.
        Mf.Check(Mf.MFCreateMediaType(out var output), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(output, Mf.MajorType, Mf.MediaTypeVideo);
            Mf.SetGuid(output, Mf.Subtype, Mf.VideoFormatH264);
            Mf.SetUInt32(output, Mf.AverageBitrate, (uint)_options.BitsPerSecond);
            Mf.SetUInt64(output, Mf.FrameSize, Mf.Pack(_options.Width, _options.Height));
            Mf.SetUInt64(output, Mf.FrameRate, Mf.Pack(_options.FramesPerSecond, 1));
            Mf.SetUInt64(output, Mf.PixelAspectRatio, Mf.Pack(1, 1));
            Mf.SetUInt32(output, Mf.InterlaceMode, 2); // progressive
            Mf.SetUInt32(output, Mf.Mpeg2Profile, 66); // baseline: what every browser decodes
            Mf.Check(Mf.SetOutputType(_transform, 0, output, 0), "SetOutputType");
        }
        finally
        {
            Mf.Release(ref output);
        }

        Mf.Check(Mf.MFCreateMediaType(out var input), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(input, Mf.MajorType, Mf.MediaTypeVideo);
            Mf.SetGuid(input, Mf.Subtype, Mf.VideoFormatNv12);
            Mf.SetUInt64(input, Mf.FrameSize, Mf.Pack(_options.Width, _options.Height));
            Mf.SetUInt64(input, Mf.FrameRate, Mf.Pack(_options.FramesPerSecond, 1));
            Mf.SetUInt64(input, Mf.PixelAspectRatio, Mf.Pack(1, 1));
            Mf.SetUInt32(input, Mf.InterlaceMode, 2);
            Mf.Check(Mf.SetInputType(_transform, 0, input, 0), "SetInputType");
        }
        finally
        {
            Mf.Release(ref input);
        }

        // These are advice, not requirements: a driver that does not take one still encodes.
        if (Mf.QueryInterface(_transform, Mf.CodecApiInterface, out _codec) == Mf.SOk)
        {
            Mf.SetCodecBool(_codec, Mf.LowLatencyMode, true);
            Mf.SetCodecUInt32(_codec, Mf.RateControlMode, 0); // constant bitrate
            Mf.SetCodecUInt32(_codec, Mf.MeanBitRate, (uint)_options.BitsPerSecond);
            var gop = (uint)Math.Clamp(_options.KeyframeInterval.TotalSeconds * _options.FramesPerSecond, 1, 600);
            Mf.SetCodecUInt32(_codec, Mf.GopSize, gop);
        }

        Mf.Check(Mf.GetOutputStreamInfo(_transform, 0, out var info), "GetOutputStreamInfo");
        _providesSamples = (info.Flags & Mf.OutputStreamProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, (uint)(_options.Width * _options.Height));
        Mf.Check(Mf.ProcessMessage(_transform, Mf.MessageNotifyBeginStreaming, 0), "NotifyBeginStreaming");
        Mf.ProcessMessage(_transform, Mf.MessageNotifyStartOfStream, 0);
    }

    private nint CreateSample(ReadOnlySpan<byte> nv12, TimeSpan timestamp)
    {
        Mf.Check(Mf.MFCreateMemoryBuffer((uint)nv12.Length, out var buffer), "MFCreateMemoryBuffer");
        nint sample = 0;
        try
        {
            Mf.Check(Mf.Lock(buffer, out var data, out _, out _), "IMFMediaBuffer::Lock");
            nv12.CopyTo(new Span<byte>(data, nv12.Length));
            Mf.Unlock(buffer);
            Mf.SetCurrentLength(buffer, (uint)nv12.Length);

            Mf.Check(Mf.MFCreateSample(out sample), "MFCreateSample");
            Mf.AddBuffer(sample, buffer);
            Mf.SetSampleTime(sample, timestamp.Ticks);
            Mf.SetSampleDuration(sample, TimeSpan.TicksPerSecond / Math.Max(_options.FramesPerSecond, 1));
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

    /// <summary>Takes whatever the transform has finished, as Annex B access units.</summary>
    private void Collect(List<EncodedVideoFrame> frames)
    {
        while (true)
        {
            nint sample = 0;
            if (!_providesSamples)
            {
                Mf.Check(Mf.MFCreateSample(out sample), "MFCreateSample");
                Mf.Check(Mf.MFCreateMemoryBuffer(_outputSize, out var buffer), "MFCreateMemoryBuffer");
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

            try
            {
                Mf.Check(hr, "ProcessOutput");
                var produced = data.Sample;
                if (produced == 0)
                {
                    return;
                }

                var bytes = Read(produced);
                Mf.GetSampleTime(produced, out var time);
                if (bytes.Length > 0)
                {
                    frames.Add(new EncodedVideoFrame(bytes, IsKeyframe(bytes), TimeSpan.FromTicks(time)));
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

    private static byte[] Read(nint sample)
    {
        Mf.Check(Mf.ConvertToContiguousBuffer(sample, out var buffer), "ConvertToContiguousBuffer");
        try
        {
            Mf.Check(Mf.Lock(buffer, out var data, out _, out var length), "IMFMediaBuffer::Lock");
            var bytes = new ReadOnlySpan<byte>(data, (int)length).ToArray();
            Mf.Unlock(buffer);
            return bytes;
        }
        finally
        {
            Mf.Release(ref buffer);
        }
    }

    /// <summary>An access unit a decoder can start on carries an IDR slice, and usually its parameter sets.</summary>
    private static bool IsKeyframe(ReadOnlySpan<byte> frame)
    {
        for (var i = 0; i + 3 < frame.Length; i++)
        {
            if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 1)
            {
                continue;
            }

            var type = frame[i + 3] & 0x1F;
            if (type is 5 or 7)
            {
                return true;
            }
        }

        return false;
    }
}
