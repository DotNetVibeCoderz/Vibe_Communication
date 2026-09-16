using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoipNet.Audio;

/// <summary>Container written by <see cref="CallRecorder"/>.</summary>
public enum RecordingFormat
{
    /// <summary>Uncompressed 16-bit PCM in a RIFF/WAVE container.</summary>
    Wav,

    /// <summary>MP3 at 64 kbit/s per channel. Falls back to WAV when the encoder is unavailable.</summary>
    Mp3,
}

/// <summary>How the two directions of a call are stored.</summary>
public enum RecordingLayout
{
    /// <summary>Both directions mixed into one mono track.</summary>
    Mono,

    /// <summary>Stereo: the caller on the left channel, this endpoint on the right.</summary>
    Stereo,
}

/// <summary>Writes 16-bit PCM to a RIFF/WAVE file and fixes up the header on disposal.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private long _dataBytes;
    private bool _disposed;

    /// <summary>Creates a writer for a file path.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="channels">Number of channels.</param>
    public WavWriter(string path, int sampleRate, int channels = 1)
        : this(File.Create(path), sampleRate, channels, ownsStream: true)
    {
    }

    /// <summary>Creates a writer over an existing stream.</summary>
    /// <param name="stream">Destination stream, which must be seekable to finalise the header.</param>
    /// <param name="sampleRate">Sample rate in hertz.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="ownsStream">Whether disposal should close the stream.</param>
    public WavWriter(Stream stream, int sampleRate, int channels = 1, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _ownsStream = ownsStream;
        SampleRate = sampleRate;
        Channels = channels;
        WriteHeader();
    }

    /// <summary>Sample rate of the file.</summary>
    public int SampleRate { get; }

    /// <summary>Channel count of the file.</summary>
    public int Channels { get; }

    /// <summary>Audio written so far.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(_dataBytes / (double)(2 * Channels * Math.Max(SampleRate, 1)));

    /// <summary>Appends samples (interleaved when the file is stereo).</summary>
    /// <param name="samples">PCM samples.</param>
    public void Write(ReadOnlySpan<short> samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = Pcm.AsBytes(samples);
        _stream.Write(bytes);
        _dataBytes += bytes.Length;
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 0);
        "WAVE"u8.CopyTo(header[8..]);
        "fmt "u8.CopyTo(header[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], (short)Channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * Channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], (short)(Channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], 0);
        _stream.Write(header);
    }

    /// <summary>Finalises the header and releases the stream.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_stream.CanSeek)
        {
            _stream.Seek(4, SeekOrigin.Begin);
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, (int)(36 + _dataBytes));
            _stream.Write(size);
            _stream.Seek(40, SeekOrigin.Begin);
            BinaryPrimitives.WriteInt32LittleEndian(size, (int)_dataBytes);
            _stream.Write(size);
        }

        _stream.Flush();
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }
}

/// <summary>Reads 16-bit PCM from a RIFF/WAVE file.</summary>
public static class WavReader
{
    /// <summary>Reads a mono or stereo 16-bit WAV file.</summary>
    /// <param name="path">File to read.</param>
    /// <returns>The samples (interleaved for stereo), the sample rate and the channel count.</returns>
    public static (short[] Samples, int SampleRate, int Channels) Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int sampleRate = 8000, channels = 1, bits = 16;
        while (stream.Position < stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            var next = stream.Position + size;
            if (id == "fmt ")
            {
                reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
            }
            else if (id == "data")
            {
                if (bits != 16)
                {
                    throw new NotSupportedException($"Only 16-bit PCM is supported, this file is {bits}-bit.");
                }

                var bytes = reader.ReadBytes(size);
                return (Pcm.AsSamples(bytes).ToArray(), sampleRate, channels);
            }

            stream.Position = next + (next % 2);
        }

        throw new InvalidDataException("The file has no data chunk.");
    }
}

/// <summary>
/// Records both directions of a call. Inbound and outbound frames arrive separately, so they are
/// paired up before being written; when one side is quiet its gap is filled with silence.
/// </summary>
public sealed class CallRecorder : IDisposable
{
    private readonly VoipCall _call;
    private readonly RecordingLayout _layout;
    private readonly ILogger _logger;
    private readonly Queue<short> _inbound = new();
    private readonly Queue<short> _outbound = new();
    private readonly Lock _gate = new();
    private readonly Stream? _mp3Stream;
    private IDisposable? _mp3Writer;
    private WavWriter? _wav;
    private int _sampleRate;
    private bool _disposed;

    private CallRecorder(VoipCall call, string path, RecordingFormat format, RecordingLayout layout, ILogger? logger)
    {
        _call = call;
        _layout = layout;
        _logger = logger ?? NullLogger.Instance;
        Path = path;
        Format = format;
        _sampleRate = call.SampleRate > 0 ? call.SampleRate : 8000;

        if (format == RecordingFormat.Mp3)
        {
            try
            {
                _mp3Stream = File.Create(path);
                _mp3Writer = Mp3Encoder.Create(_mp3Stream, _sampleRate, layout == RecordingLayout.Stereo ? 2 : 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MP3 encoding is unavailable, recording {Path} as WAV instead", path);
                _mp3Writer = null;
                _mp3Stream?.Dispose();
                _mp3Stream = null;
                Format = RecordingFormat.Wav;
                Path = System.IO.Path.ChangeExtension(path, ".wav");
            }
        }

        if (Format == RecordingFormat.Wav)
        {
            _wav = new WavWriter(Path, _sampleRate, layout == RecordingLayout.Stereo ? 2 : 1);
        }

        call.AudioReceived += OnAudio;
        call.StateChanged += OnStateChanged;
    }

    /// <summary>Starts recording a call.</summary>
    /// <param name="call">The call to record.</param>
    /// <param name="path">Destination file.</param>
    /// <param name="format">Container to write.</param>
    /// <param name="layout">Whether to keep the directions apart in stereo.</param>
    /// <param name="logger">Optional logger.</param>
    public static CallRecorder Start(
        VoipCall call,
        string path,
        RecordingFormat format = RecordingFormat.Wav,
        RecordingLayout layout = RecordingLayout.Stereo,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new CallRecorder(call, path, format, layout, logger);
    }

    /// <summary>File being written. It may differ from the requested path if MP3 was unavailable.</summary>
    public string Path { get; private set; }

    /// <summary>Container actually in use.</summary>
    public RecordingFormat Format { get; private set; }

    /// <summary>Audio written so far.</summary>
    public TimeSpan Duration { get; private set; }

    private void OnStateChanged(object? sender, CallStateEventArgs e)
    {
        if (e.State == CallState.Terminated)
        {
            Dispose();
        }
    }

    private void OnAudio(VoipCall call, AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _sampleRate = sampleRate;
            var queue = direction == AudioDirection.Inbound ? _inbound : _outbound;
            foreach (var s in samples)
            {
                queue.Enqueue(s);
            }

            // Do not let one direction run away when the other is silent (hold, mute, half duplex).
            var maxLag = sampleRate / 2;
            if (_inbound.Count > maxLag && _outbound.Count == 0)
            {
                Pad(_outbound, _inbound.Count);
            }
            else if (_outbound.Count > maxLag && _inbound.Count == 0)
            {
                Pad(_inbound, _outbound.Count);
            }

            WritePairs();
        }
    }

    private static void Pad(Queue<short> queue, int target)
    {
        while (queue.Count < target)
        {
            queue.Enqueue(0);
        }
    }

    private void WritePairs()
    {
        var count = Math.Min(_inbound.Count, _outbound.Count);
        if (count == 0)
        {
            return;
        }

        var buffer = new short[_layout == RecordingLayout.Stereo ? count * 2 : count];
        for (var i = 0; i < count; i++)
        {
            var inbound = _inbound.Dequeue();
            var outbound = _outbound.Dequeue();
            if (_layout == RecordingLayout.Stereo)
            {
                buffer[i * 2] = inbound;
                buffer[(i * 2) + 1] = outbound;
            }
            else
            {
                buffer[i] = (short)Math.Clamp(inbound + outbound, short.MinValue, short.MaxValue);
            }
        }

        if (_mp3Writer is not null)
        {
            Mp3Encoder.Write(_mp3Writer, buffer);
        }
        else
        {
            _wav?.Write(buffer);
        }

        Duration += TimeSpan.FromSeconds(count / (double)Math.Max(_sampleRate, 1));
    }

    /// <summary>Stops recording and finalises the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _call.AudioReceived -= OnAudio;
            _call.StateChanged -= OnStateChanged;
            _wav?.Dispose();
            _mp3Writer?.Dispose();
            _mp3Stream?.Dispose();
            _wav = null;
            _mp3Writer = null;
        }

        _logger.LogInformation("Recording saved to {Path} ({Duration:mm\\:ss})", Path, Duration);
    }
}

/// <summary>Thin wrapper over NAudio.Lame so the dependency stays isolated and optional.</summary>
internal static class Mp3Encoder
{
    internal static IDisposable Create(Stream output, int sampleRate, int channels)
    {
        var format = new NAudio.Wave.WaveFormat(sampleRate, 16, channels);
        return new NAudio.Lame.LameMP3FileWriter(output, format, 64 * channels);
    }

    internal static void Write(IDisposable writer, short[] samples)
    {
        var bytes = Pcm.ToBytes(samples);
        ((NAudio.Lame.LameMP3FileWriter)writer).Write(bytes, 0, bytes.Length);
    }
}
