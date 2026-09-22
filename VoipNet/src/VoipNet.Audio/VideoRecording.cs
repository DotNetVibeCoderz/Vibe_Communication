using System.Buffers.Binary;
using System.Text;

namespace VoipNet.Audio;

/// <summary>
/// Writes a call to an AVI file: the video exactly as the peer encoded it (H.264 in Annex B form or
/// VP8) next to 16-bit PCM audio. Nothing is re-encoded, so a recording costs almost no CPU and the
/// video keeps whatever quality the call had.
/// </summary>
/// <remarks>
/// AVI describes a stream by a single nominal frame rate, which a call does not have: frames arrive
/// when the far end sends them. The header is therefore written with a placeholder and patched on
/// disposal with the rate the call actually ran at, which is what players use to lay the file out.
/// MP4 with AAC audio would need an AAC encoder, which this SDK does not carry; see PLAN 1.3.
/// </remarks>
public sealed class AviWriter : IDisposable
{
    /// Bytes inside the 'hdrl' list: its own four-character code, the main header, and one stream list
    /// for video and one for audio.
    private const int VideoStreamListBytes = 4 + (8 + 56) + (8 + 40);
    private const int AudioStreamListBytes = 4 + (8 + 56) + (8 + 18);
    private const int HeaderListBytes = 4 + (8 + 56) + (8 + VideoStreamListBytes) + (8 + AudioStreamListBytes);

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly List<(uint ChunkId, uint Offset, uint Length, bool Keyframe)> _index = [];
    private readonly Lock _gate = new();
    private long _moviStart;
    private long _moviBytes;
    private int _videoFrames;
    private long _audioSamples;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>Creates a writer for a file path.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="codec">Video codec four-character code, for example <c>H264</c> or <c>VP80</c>.</param>
    /// <param name="sampleRate">Audio sample rate in hertz.</param>
    /// <param name="channels">Audio channel count.</param>
    public AviWriter(string path, string codec, int sampleRate, int channels = 1)
        : this(File.Create(path), codec, sampleRate, channels, ownsStream: true)
    {
    }

    /// <summary>Creates a writer over an existing stream, which must be seekable.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="codec">Video codec four-character code.</param>
    /// <param name="sampleRate">Audio sample rate in hertz.</param>
    /// <param name="channels">Audio channel count.</param>
    /// <param name="ownsStream">Whether disposal should close the stream.</param>
    public AviWriter(Stream stream, string codec, int sampleRate, int channels = 1, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(codec);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("An AVI file is finished by patching its header, so the stream must be seekable.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        Codec = codec.Length >= 4 ? codec[..4].ToUpperInvariant() : codec.ToUpperInvariant().PadRight(4);
        SampleRate = sampleRate;
        Channels = channels;
        WriteHeader(fps: 25, frames: 0);
        _moviStart = _stream.Position;
    }

    /// <summary>Video codec four-character code stored in the file.</summary>
    public string Codec { get; }

    /// <summary>Audio sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Audio channel count.</summary>
    public int Channels { get; }

    /// <summary>Video frames written so far.</summary>
    public int VideoFrames => _videoFrames;

    /// <summary>Audio written so far.</summary>
    public TimeSpan AudioDuration => TimeSpan.FromSeconds(_audioSamples / (double)Math.Max(SampleRate * Channels, 1));

    /// <summary>Appends one encoded video frame.</summary>
    /// <param name="frame">The frame exactly as it came off the wire.</param>
    /// <param name="keyframe">True when the frame can be decoded on its own.</param>
    public void WriteVideo(ReadOnlySpan<byte> frame, bool keyframe)
    {
        if (frame.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_width == 0)
            {
                (_width, _height) = ReadDimensions(frame);
            }

            WriteChunk(FourCc("00dc"), frame, keyframe);
            _videoFrames++;
        }
    }

    /// <summary>Appends 16-bit PCM audio, interleaved when the file is stereo.</summary>
    /// <param name="samples">Audio samples.</param>
    public void WriteAudio(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
            }

            WriteChunk(FourCc("01wb"), bytes, keyframe: true);
            _audioSamples += samples.Length;
        }
    }

    /// <summary>Writes the index, patches the header with the real frame rate and closes the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WriteIndex();

            // The frame rate the call really ran at: frames over the audio clock, which is the only
            // timeline both streams share.
            var seconds = AudioDuration.TotalSeconds;
            var fps = seconds > 0.5 && _videoFrames > 1 ? Math.Clamp(_videoFrames / seconds, 1, 120) : 25;
            var end = _stream.Position;
            _stream.Position = 0;
            WriteHeader(fps, _videoFrames);
            // The header ends with 'LIST <size> movi', whose size only the finished file knows.
            _stream.Position = _moviStart - 8;
            WriteUInt32((uint)(_moviBytes + 4));
            _stream.Position = 4;
            WriteUInt32((uint)(end - 8)); // RIFF size
            _stream.Position = end;
            _stream.Flush();
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }

    private void WriteChunk(uint id, ReadOnlySpan<byte> payload, bool keyframe)
    {
        var offset = (uint)(_stream.Position - _moviStart + 4); // offsets in idx1 are relative to 'movi'
        WriteUInt32(id);
        WriteUInt32((uint)payload.Length);
        _stream.Write(payload);
        if (payload.Length % 2 == 1)
        {
            _stream.WriteByte(0); // chunks are word aligned
        }

        _moviBytes += 8 + payload.Length + (payload.Length % 2);
        _index.Add((id, offset, (uint)payload.Length, keyframe));
    }

    private void WriteIndex()
    {
        WriteUInt32(FourCc("idx1"));
        WriteUInt32((uint)(_index.Count * 16));
        foreach (var (id, offset, length, keyframe) in _index)
        {
            WriteUInt32(id);
            WriteUInt32(keyframe ? 0x10u : 0u); // AVIIF_KEYFRAME
            WriteUInt32(offset);
            WriteUInt32(length);
        }
    }

    private void WriteHeader(double fps, int frames)
    {
        var microsPerFrame = (uint)Math.Round(1_000_000 / Math.Max(fps, 1));
        var width = _width > 0 ? _width : 640;
        var height = _height > 0 ? _height : 360;

        WriteUInt32(FourCc("RIFF"));
        WriteUInt32(0); // patched on disposal
        WriteUInt32(FourCc("AVI "));

        WriteUInt32(FourCc("LIST"));
        WriteUInt32((uint)HeaderListBytes);
        WriteUInt32(FourCc("hdrl"));

        // Main header (avih).
        WriteUInt32(FourCc("avih"));
        WriteUInt32(56);
        WriteUInt32(microsPerFrame);
        WriteUInt32(0); // maximum bytes per second, unknown
        WriteUInt32(0); // padding granularity
        WriteUInt32(0x10); // AVIF_HASINDEX
        WriteUInt32((uint)frames);
        WriteUInt32(0); // initial frames
        WriteUInt32(2); // streams
        WriteUInt32(0); // suggested buffer size
        WriteUInt32((uint)width);
        WriteUInt32((uint)height);
        for (var i = 0; i < 4; i++)
        {
            WriteUInt32(0); // reserved
        }

        // Video stream.
        WriteUInt32(FourCc("LIST"));
        WriteUInt32(VideoStreamListBytes);
        WriteUInt32(FourCc("strl"));
        WriteStreamHeader("vids", Codec, scale: 1, rate: (uint)Math.Round(fps), length: (uint)frames, sampleSize: 0);
        WriteUInt32(FourCc("strf"));
        WriteUInt32(40);
        WriteUInt32(40); // BITMAPINFOHEADER size
        WriteUInt32((uint)width);
        WriteUInt32((uint)height);
        WriteUInt16(1); // planes
        WriteUInt16(24); // bit count
        WriteUInt32(FourCc(Codec));
        WriteUInt32((uint)(width * height * 3));
        for (var i = 0; i < 4; i++)
        {
            WriteUInt32(0);
        }

        // Audio stream.
        WriteUInt32(FourCc("LIST"));
        WriteUInt32(AudioStreamListBytes);
        WriteUInt32(FourCc("strl"));
        var blockAlign = (uint)(2 * Channels);
        WriteStreamHeader("auds", "\0\0\0\0", scale: blockAlign, rate: (uint)(SampleRate * blockAlign), length: (uint)(_audioSamples / Math.Max(Channels, 1)), sampleSize: blockAlign);
        WriteUInt32(FourCc("strf"));
        WriteUInt32(18);
        WriteUInt16(1); // WAVE_FORMAT_PCM
        WriteUInt16((ushort)Channels);
        WriteUInt32((uint)SampleRate);
        WriteUInt32((uint)(SampleRate * blockAlign));
        WriteUInt16((ushort)blockAlign);
        WriteUInt16(16); // bits per sample
        WriteUInt16(0); // no extra data

        WriteUInt32(FourCc("LIST"));
        WriteUInt32(0); // 'movi' size, patched on disposal
        WriteUInt32(FourCc("movi"));
    }

    private void WriteStreamHeader(string type, string handler, uint scale, uint rate, uint length, uint sampleSize)
    {
        WriteUInt32(FourCc("strh"));
        WriteUInt32(56);
        WriteUInt32(FourCc(type));
        WriteUInt32(FourCc(handler));
        WriteUInt32(0); // flags
        WriteUInt16(0); // priority
        WriteUInt16(0); // language
        WriteUInt32(0); // initial frames
        WriteUInt32(Math.Max(scale, 1));
        WriteUInt32(Math.Max(rate, 1));
        WriteUInt32(0); // start
        WriteUInt32(length);
        WriteUInt32(0); // suggested buffer size
        WriteUInt32(0xFFFFFFFF); // quality: default
        WriteUInt32(sampleSize);
        WriteUInt32(0); // rcFrame left/top
        WriteUInt32(0); // rcFrame right/bottom
    }

    /// <summary>
    /// Reads the picture size out of an H.264 sequence parameter set, so the file says what it really
    /// holds. Anything else falls back to a sensible default: players read the size from the stream.
    /// </summary>
    private static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> frame)
    {
        var sps = FindSps(frame);
        if (sps.IsEmpty)
        {
            return (0, 0);
        }

        try
        {
            var reader = new RbspReader(sps);
            reader.Skip(8); // profile_idc
            reader.Skip(8); // constraint flags and reserved bits
            reader.Skip(8); // level_idc
            var profile = sps[0];
            reader.ReadUnsigned(); // seq_parameter_set_id
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                var chroma = reader.ReadUnsigned();
                if (chroma == 3)
                {
                    reader.Skip(1); // separate_colour_plane_flag
                }

                reader.ReadUnsigned(); // bit_depth_luma_minus8
                reader.ReadUnsigned(); // bit_depth_chroma_minus8
                reader.Skip(1); // qpprime_y_zero_transform_bypass_flag
                if (reader.ReadBit() == 1)
                {
                    // seq_scaling_matrix_present_flag: the lists are not needed for the size.
                    return (0, 0);
                }
            }

            reader.ReadUnsigned(); // log2_max_frame_num_minus4
            var pocType = reader.ReadUnsigned();
            if (pocType == 0)
            {
                reader.ReadUnsigned(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (pocType == 1)
            {
                reader.Skip(1);
                reader.ReadSigned();
                reader.ReadSigned();
                var cycle = reader.ReadUnsigned();
                for (var i = 0; i < cycle; i++)
                {
                    reader.ReadSigned();
                }
            }

            reader.ReadUnsigned(); // max_num_ref_frames
            reader.Skip(1); // gaps_in_frame_num_value_allowed_flag
            var widthMbs = reader.ReadUnsigned() + 1;
            var heightMapUnits = reader.ReadUnsigned() + 1;
            var frameMbsOnly = reader.ReadBit();
            var width = (int)(widthMbs * 16);
            var height = (int)((2 - frameMbsOnly) * heightMapUnits * 16);
            return width is > 0 and <= 8192 && height is > 0 and <= 8192 ? (width, height) : (0, 0);
        }
        catch (InvalidOperationException)
        {
            // A truncated parameter set says nothing about the size, which is not worth failing over.
            return (0, 0);
        }
    }

    /// <summary>Finds the SPS payload (NAL type 7) in an Annex B access unit.</summary>
    private static ReadOnlySpan<byte> FindSps(ReadOnlySpan<byte> frame)
    {
        for (var i = 0; i + 4 < frame.Length; i++)
        {
            var isStart = frame[i] == 0 && frame[i + 1] == 0 && (frame[i + 2] == 1 || (frame[i + 2] == 0 && frame[i + 3] == 1));
            if (!isStart)
            {
                continue;
            }

            var header = frame[i + 2] == 1 ? i + 3 : i + 4;
            if (header < frame.Length && (frame[header] & 0x1F) == 7)
            {
                var end = frame.Length;
                for (var j = header + 1; j + 3 < frame.Length; j++)
                {
                    if (frame[j] == 0 && frame[j + 1] == 0 && (frame[j + 2] == 1 || frame[j + 2] == 0))
                    {
                        end = j;
                        break;
                    }
                }

                return frame[(header + 1)..end];
            }
        }

        return default;
    }

    private static uint FourCc(string code) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(code.Length >= 4 ? code[..4] : code.PadRight(4, '\0')));

    private void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _stream.Write(buffer);
    }
}

/// <summary>Reads the bit-oriented syntax of an H.264 parameter set (RBSP with emulation prevention).</summary>
internal ref struct RbspReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bit;

    public RbspReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bit = 0;
    }

    public void Skip(int bits) => _bit += bits;

    public uint ReadBit()
    {
        var index = _bit / 8;
        if (index >= _data.Length)
        {
            throw new InvalidOperationException("The parameter set ended early.");
        }

        var value = (uint)((_data[index] >> (7 - (_bit % 8))) & 1);
        _bit++;
        return value;
    }

    /// <summary>Exp-Golomb coded unsigned integer (ue(v)).</summary>
    public uint ReadUnsigned()
    {
        var zeros = 0;
        while (ReadBit() == 0)
        {
            if (++zeros > 31)
            {
                throw new InvalidOperationException("The parameter set is not valid Exp-Golomb.");
            }
        }

        var value = 1u;
        for (var i = 0; i < zeros; i++)
        {
            value = (value << 1) | ReadBit();
        }

        return value - 1;
    }

    /// <summary>Exp-Golomb coded signed integer (se(v)).</summary>
    public int ReadSigned()
    {
        var value = ReadUnsigned();
        var magnitude = (int)((value + 1) / 2);
        return value % 2 == 0 ? -magnitude : magnitude;
    }
}
