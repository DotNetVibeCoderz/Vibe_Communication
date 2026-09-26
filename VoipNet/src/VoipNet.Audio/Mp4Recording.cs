using System.Buffers.Binary;
using System.Text;

namespace VoipNet.Audio;

/// <summary>A writer that takes a call's own encoded video next to its audio.</summary>
internal interface ICallVideoWriter : IDisposable
{
    /// <summary>Appends one encoded video frame.</summary>
    void WriteVideo(ReadOnlySpan<byte> frame, bool keyframe, uint timestamp);

    /// <summary>Appends 16-bit PCM audio, interleaved when the file is stereo.</summary>
    void WriteAudio(ReadOnlySpan<short> samples);
}

/// <summary>
/// Writes a call to an MP4 file: the peer's H.264 exactly as it was encoded, next to 16-bit PCM audio.
/// Nothing is re-encoded, so a recording costs almost no CPU and keeps whatever quality the call had.
/// </summary>
/// <remarks>
/// MP4 gives each sample its own duration, so a call's real timing survives — RTP timestamps become
/// sample durations, and a frame that arrived late stays late instead of being averaged away as it is
/// in an AVI. Two things follow from what the SDK carries. The audio is PCM, not AAC, because there is
/// no AAC encoder here; players read it, but the file is as large as a WAV. And H.264 has to be
/// rewritten from the Annex B form it arrives in into the length-prefixed form MP4 stores, with the
/// parameter sets lifted into the sample description, so recording only starts at the first frame that
/// carries a sequence parameter set — an earlier frame could not be decoded on its own anyway.
/// </remarks>
public sealed class Mp4Writer : ICallVideoWriter
{
    /// <summary>The clock RTP video runs on, and the one the video track is written in.</summary>
    private const uint VideoTimescale = 90_000;

    /// <summary>The movie clock: milliseconds, which is what every player expects to find here.</summary>
    private const uint MovieTimescale = 1_000;

    /// <summary>A frame's duration when the call gives nothing better: 1/30 s.</summary>
    private const uint DefaultFrameDuration = 3_000;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Lock _gate = new();
    private readonly List<(long Offset, int Size, bool Keyframe, uint Timestamp)> _video = [];
    private readonly List<(long Offset, int Frames)> _audio = [];
    private byte[]? _sps;
    private byte[]? _pps;
    private long _mdatStart;
    private long _audioFrames;
    private long _audioFramesBeforeVideo = -1;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>Creates a writer for a file path.</summary>
    /// <param name="path">Destination file.</param>
    /// <param name="sampleRate">Audio sample rate in hertz.</param>
    /// <param name="channels">Audio channel count.</param>
    public Mp4Writer(string path, int sampleRate, int channels = 1)
        : this(File.Create(path), sampleRate, channels, ownsStream: true)
    {
    }

    /// <summary>Creates a writer over an existing stream, which must be seekable.</summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="sampleRate">Audio sample rate in hertz.</param>
    /// <param name="channels">Audio channel count.</param>
    /// <param name="ownsStream">Whether disposal should close the stream.</param>
    public Mp4Writer(Stream stream, int sampleRate, int channels = 1, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("An MP4 file is finished by patching the size of its media box, so the stream must be seekable.", nameof(stream));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        SampleRate = sampleRate > 0 ? sampleRate : 8000;
        Channels = Math.Max(channels, 1);

        WriteFileType();
        // The media box comes first so samples can be appended as they arrive; its length is only known
        // at the end, so it is written with a 64-bit size that gets patched on disposal.
        _mdatStart = _stream.Position;
        WriteUInt32(1);
        WriteFourCc("mdat");
        WriteUInt64(0);
    }

    /// <summary>Audio sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Audio channel count.</summary>
    public int Channels { get; }

    /// <summary>Video frames written so far.</summary>
    public int VideoFrames => _video.Count;

    /// <summary>Audio written so far.</summary>
    public TimeSpan AudioDuration => TimeSpan.FromSeconds(_audioFrames / (double)Math.Max(SampleRate, 1));

    /// <summary>Whether a sequence parameter set has been seen, without which no video can be stored.</summary>
    public bool HasVideo => _sps is not null;

    /// <summary>Appends one encoded video frame.</summary>
    /// <param name="frame">The frame exactly as it came off the wire, in Annex B form.</param>
    /// <param name="keyframe">True when the frame can be decoded on its own.</param>
    /// <param name="timestamp">The frame's RTP timestamp, at 90 kHz.</param>
    public void WriteVideo(ReadOnlySpan<byte> frame, bool keyframe, uint timestamp)
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

            var sample = H264.ToLengthPrefixed(frame, ref _sps, ref _pps);
            if (_sps is null || sample.Length == 0)
            {
                // Nothing here can be decoded yet: a player would have no picture to start from.
                return;
            }

            if (_width == 0)
            {
                (_width, _height) = H264.ReadDimensions(_sps);
            }

            if (_video.Count == 0)
            {
                _audioFramesBeforeVideo = _audioFrames;
            }

            var offset = _stream.Position;
            _stream.Write(sample);
            _video.Add((offset, sample.Length, keyframe, timestamp));
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

            var frames = samples.Length / Channels;
            if (frames == 0)
            {
                return;
            }

            var bytes = new byte[frames * Channels * 2];
            for (var i = 0; i < frames * Channels; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
            }

            var offset = _stream.Position;
            _stream.Write(bytes);
            _audio.Add((offset, frames));
            _audioFrames += frames;
        }
    }

    /// <summary>Patches the media box, writes the sample tables and closes the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var end = _stream.Position;
            _stream.Position = _mdatStart + 8;
            WriteUInt64((ulong)(end - _mdatStart));
            _stream.Position = end;
            _stream.Write(BuildMovie());
            _stream.Flush();
            if (_ownsStream)
            {
                _stream.Dispose();
            }
        }
    }

    /// <summary>How long each video sample is shown, in the video timescale.</summary>
    private List<uint> VideoDurations()
    {
        var durations = new List<uint>(_video.Count);
        for (var i = 0; i < _video.Count; i++)
        {
            uint duration;
            if (i + 1 < _video.Count)
            {
                // Unchecked so a timestamp that wraps past 2^32 still gives the gap, not a huge number.
                var delta = unchecked(_video[i + 1].Timestamp - _video[i].Timestamp);
                duration = delta is > 0 and <= VideoTimescale * 10 ? delta : DefaultFrameDuration;
            }
            else
            {
                duration = durations.Count > 0 ? durations[^1] : DefaultFrameDuration;
            }

            durations.Add(duration);
        }

        return durations;
    }

    private byte[] BuildMovie()
    {
        var durations = VideoDurations();
        var videoDuration = durations.Aggregate(0L, (total, d) => total + d);
        var movieDuration = Math.Max(
            _audioFrames * MovieTimescale / Math.Max(SampleRate, 1),
            videoDuration * MovieTimescale / VideoTimescale);

        var traks = new List<byte[]>();
        if (_video.Count > 0)
        {
            traks.Add(VideoTrack(durations, videoDuration));
        }

        if (_audio.Count > 0)
        {
            traks.Add(AudioTrack());
        }

        var header = Box(
            "mvhd",
            FullBox(0, 0),
            UInt32(0),
            UInt32(0),
            UInt32(MovieTimescale),
            UInt32((uint)movieDuration),
            UInt32(0x0001_0000), // rate: 1.0
            UInt16(0x0100), // volume: 1.0
            UInt16(0),
            new byte[8],
            Matrix(),
            new byte[24],
            UInt32((uint)(traks.Count + 1)));

        return Box("moov", [header, .. traks]);
    }

    private byte[] VideoTrack(List<uint> durations, long videoDuration)
    {
        var track = Box(
            "tkhd",
            FullBox(0, 3), // enabled and in the movie
            UInt32(0),
            UInt32(0),
            UInt32(1), // track id
            UInt32(0),
            UInt32((uint)(videoDuration * MovieTimescale / VideoTimescale)),
            new byte[8],
            UInt16(0), // layer
            UInt16(0), // alternate group
            UInt16(0), // volume: silent, this is the picture
            UInt16(0),
            Matrix(),
            UInt32((uint)Width << 16),
            UInt32((uint)Height << 16));

        // Audio that arrived before the first frame would otherwise drag the picture forward, because
        // both tracks start at zero. An empty edit holds the video back by exactly that much.
        var lead = _audioFramesBeforeVideo > 0
            ? (uint)(_audioFramesBeforeVideo * MovieTimescale / Math.Max(SampleRate, 1))
            : 0;
        var edits = lead > 0
            ? Box("edts", Box(
                "elst",
                FullBox(0, 0),
                UInt32(2),
                UInt32(lead),
                UInt32(0xFFFF_FFFF), // an empty edit: nothing is shown yet
                UInt32(0x0001_0000),
                UInt32((uint)(videoDuration * MovieTimescale / VideoTimescale)),
                UInt32(0),
                UInt32(0x0001_0000)))
            : [];

        var sampleTable = Box(
            "stbl",
            Box("stsd", FullBox(0, 0), UInt32(1), VisualSampleEntry()),
            TimeToSample(durations),
            SyncSamples(),
            SampleToChunk(_video.Select(v => 1).ToList()),
            Box("stsz", FullBox(0, 0), UInt32(0), UInt32((uint)_video.Count), Concat(_video.Select(v => UInt32((uint)v.Size)))),
            ChunkOffsets(_video.Select(v => v.Offset)));

        var media = Box(
            "mdia",
            MediaHeader(VideoTimescale, videoDuration),
            Handler("vide", "VideoHandler"),
            Box(
                "minf",
                Box("vmhd", FullBox(0, 1), UInt16(0), new byte[6]),
                DataInformation(),
                sampleTable));

        return Box("trak", [track, .. edits.Length > 0 ? new[] { edits } : [], media]);
    }

    private byte[] AudioTrack()
    {
        var track = Box(
            "tkhd",
            FullBox(0, 3),
            UInt32(0),
            UInt32(0),
            UInt32(2), // track id
            UInt32(0),
            UInt32((uint)(_audioFrames * MovieTimescale / Math.Max(SampleRate, 1))),
            new byte[8],
            UInt16(0),
            UInt16(0),
            UInt16(0x0100), // full volume
            UInt16(0),
            Matrix(),
            UInt32(0),
            UInt32(0));

        var sampleTable = Box(
            "stbl",
            Box("stsd", FullBox(0, 0), UInt32(1), AudioSampleEntry()),
            // Every PCM frame lasts exactly one tick of its own clock, so one entry describes them all.
            Box("stts", FullBox(0, 0), UInt32(1), UInt32((uint)_audioFrames), UInt32(1)),
            SampleToChunk(_audio.Select(a => a.Frames).ToList()),
            Box("stsz", FullBox(0, 0), UInt32((uint)(2 * Channels)), UInt32((uint)_audioFrames)),
            ChunkOffsets(_audio.Select(a => a.Offset)));

        var media = Box(
            "mdia",
            MediaHeader((uint)SampleRate, _audioFrames),
            Handler("soun", "SoundHandler"),
            Box(
                "minf",
                Box("smhd", FullBox(0, 0), UInt16(0), UInt16(0)),
                DataInformation(),
                sampleTable));

        return Box("trak", track, media);
    }

    private int Width => _width > 0 ? _width : 640;

    private int Height => _height > 0 ? _height : 360;

    private byte[] VisualSampleEntry()
    {
        var compressor = new byte[32];
        var name = "VoipNet H.264"u8;
        compressor[0] = (byte)name.Length;
        name.CopyTo(compressor.AsSpan(1));

        var configuration = Box(
            "avcC",
            [
                1, // configuration version
                _sps is { Length: > 1 } ? _sps[1] : (byte)0x42, // profile
                _sps is { Length: > 2 } ? _sps[2] : (byte)0xE0, // profile compatibility
                _sps is { Length: > 3 } ? _sps[3] : (byte)0x1E, // level
                0xFF, // four-byte NAL lengths
                0xE1, // one parameter set
            ],
            UInt16((ushort)(_sps?.Length ?? 0)),
            _sps ?? [],
            [(byte)(_pps is null ? 0 : 1)],
            _pps is null ? [] : UInt16((ushort)_pps.Length),
            _pps ?? []);

        return Box(
            "avc1",
            new byte[6], // reserved
            UInt16(1), // data reference index
            new byte[16], // predefined and reserved
            UInt16((ushort)Width),
            UInt16((ushort)Height),
            UInt32(0x0048_0000), // 72 dpi horizontal
            UInt32(0x0048_0000), // 72 dpi vertical
            UInt32(0),
            UInt16(1), // frames per sample
            compressor,
            UInt16(0x0018), // depth: colour, no alpha
            UInt16(0xFFFF),
            configuration);
    }

    /// <summary>
    /// Little-endian PCM ('sowt'). MP4 has no entry for raw PCM of its own that players agree on, so
    /// this is the QuickTime one, which every common player and ffmpeg read.
    /// </summary>
    private byte[] AudioSampleEntry() => Box(
        "sowt",
        new byte[6],
        UInt16(1), // data reference index
        UInt32(0), // version and revision
        UInt32(0), // vendor
        UInt16((ushort)Channels),
        UInt16(16), // bits per sample
        UInt16(0),
        UInt16(0),
        UInt32((uint)SampleRate << 16));

    private static byte[] MediaHeader(uint timescale, long duration) => Box(
        "mdhd",
        FullBox(0, 0),
        UInt32(0),
        UInt32(0),
        UInt32(timescale),
        UInt32((uint)duration),
        UInt16(0x55C4), // language: undetermined
        UInt16(0));

    private static byte[] Handler(string kind, string name) => Box(
        "hdlr",
        FullBox(0, 0),
        UInt32(0),
        FourCc(kind),
        new byte[12],
        Encoding.ASCII.GetBytes(name + "\0"));

    private static byte[] DataInformation() => Box(
        "dinf",
        Box("dref", FullBox(0, 0), UInt32(1), Box("url ", FullBox(0, 1))));

    private static byte[] TimeToSample(List<uint> durations)
    {
        // Runs of frames with the same duration collapse into one entry, which is most of a call.
        var entries = new List<byte[]>();
        var count = 0;
        for (var i = 0; i < durations.Count; i++)
        {
            count++;
            if (i + 1 == durations.Count || durations[i + 1] != durations[i])
            {
                entries.Add(Concat([UInt32((uint)count), UInt32(durations[i])]));
                count = 0;
            }
        }

        return Box("stts", FullBox(0, 0), UInt32((uint)entries.Count), Concat(entries));
    }

    private byte[] SyncSamples()
    {
        var keyframes = _video.Index().Where(s => s.Item.Keyframe).Select(s => (uint)(s.Index + 1)).ToList();
        // Every frame being a keyframe is the same as saying nothing, and saying nothing is smaller.
        return keyframes.Count == _video.Count
            ? []
            : Box("stss", FullBox(0, 0), UInt32((uint)keyframes.Count), Concat(keyframes.Select(UInt32)));
    }

    private static byte[] SampleToChunk(List<int> samplesPerChunk)
    {
        var entries = new List<byte[]>();
        for (var i = 0; i < samplesPerChunk.Count; i++)
        {
            if (i == 0 || samplesPerChunk[i] != samplesPerChunk[i - 1])
            {
                entries.Add(Concat([UInt32((uint)(i + 1)), UInt32((uint)samplesPerChunk[i]), UInt32(1)]));
            }
        }

        return Box("stsc", FullBox(0, 0), UInt32((uint)entries.Count), Concat(entries));
    }

    /// <summary>64-bit chunk offsets, so a long recording does not run out of room at 4 GB.</summary>
    private static byte[] ChunkOffsets(IEnumerable<long> offsets)
    {
        var list = offsets.ToList();
        return Box("co64", FullBox(0, 0), UInt32((uint)list.Count), Concat(list.Select(o => UInt64((ulong)o))));
    }

    private static byte[] Matrix()
    {
        var matrix = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(matrix.AsSpan(0), 0x0001_0000);
        BinaryPrimitives.WriteUInt32BigEndian(matrix.AsSpan(16), 0x0001_0000);
        BinaryPrimitives.WriteUInt32BigEndian(matrix.AsSpan(32), 0x4000_0000);
        return matrix;
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var length = 8 + parts.Sum(p => p.Length);
        var box = new byte[length];
        BinaryPrimitives.WriteInt32BigEndian(box, length);
        FourCc(type).CopyTo(box.AsSpan(4));
        var at = 8;
        foreach (var part in parts)
        {
            part.CopyTo(box.AsSpan(at));
            at += part.Length;
        }

        return box;
    }

    private static byte[] FullBox(byte version, uint flags) =>
        [version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags];

    private static byte[] FourCc(string code) => Encoding.ASCII.GetBytes(code.PadRight(4)[..4]);

    private static byte[] UInt16(ushort value) => [(byte)(value >> 8), (byte)value];

    private static byte[] UInt32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] UInt64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        var list = parts.ToList();
        var result = new byte[list.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in list)
        {
            part.CopyTo(result.AsSpan(at));
            at += part.Length;
        }

        return result;
    }

    private void WriteFileType() => _stream.Write(Box(
        "ftyp",
        FourCc("isom"),
        UInt32(0x200),
        FourCc("isom"),
        FourCc("iso2"),
        FourCc("avc1"),
        FourCc("mp41")));

    private void WriteUInt32(uint value) => _stream.Write(UInt32(value));

    private void WriteUInt64(ulong value) => _stream.Write(UInt64(value));

    private void WriteFourCc(string code) => _stream.Write(FourCc(code));
}

/// <summary>Reading an H.264 elementary stream: where the units are, and what the picture size is.</summary>
internal static class H264
{
    /// <summary>
    /// Rewrites an Annex B access unit into the four-byte length prefixes MP4 stores, keeping any
    /// parameter sets it carries for the sample description.
    /// </summary>
    internal static byte[] ToLengthPrefixed(ReadOnlySpan<byte> frame, ref byte[]? sps, ref byte[]? pps)
    {
        var units = new List<byte[]>();
        foreach (var range in Units(frame))
        {
            var unit = frame[range].ToArray();
            if (unit.Length == 0)
            {
                continue;
            }

            switch (unit[0] & 0x1F)
            {
                case 7:
                    sps ??= unit;
                    break;
                case 8:
                    pps ??= unit;
                    break;
            }

            units.Add(unit);
        }

        var total = units.Sum(u => u.Length + 4);
        var sample = new byte[total];
        var at = 0;
        foreach (var unit in units)
        {
            BinaryPrimitives.WriteInt32BigEndian(sample.AsSpan(at), unit.Length);
            unit.CopyTo(sample.AsSpan(at + 4));
            at += unit.Length + 4;
        }

        return sample;
    }

    /// <summary>The NAL units in an Annex B access unit, without their start codes.</summary>
    internal static List<Range> Units(ReadOnlySpan<byte> frame)
    {
        var units = new List<Range>();
        var start = -1;
        for (var i = 0; i + 2 < frame.Length; i++)
        {
            if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 1)
            {
                continue;
            }

            if (start >= 0)
            {
                // A start code may be preceded by the fourth zero byte of a long one.
                var end = i > start && frame[i - 1] == 0 ? i - 1 : i;
                units.Add(start..end);
            }

            start = i + 3;
            i += 2;
        }

        if (start >= 0 && start < frame.Length)
        {
            units.Add(start..frame.Length);
        }

        return units;
    }

    /// <summary>
    /// Reads the picture size out of an H.264 sequence parameter set, so a file says what it really
    /// holds. Anything else falls back to a sensible default: players read the size from the stream.
    /// </summary>
    internal static (int Width, int Height) ReadDimensions(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 4 || (sps[0] & 0x1F) != 7)
        {
            sps = FindSps(sps);
        }
        else
        {
            sps = sps[1..];
        }

        if (sps.IsEmpty)
        {
            return (0, 0);
        }

        try
        {
            var reader = new RbspReader(sps);
            var profile = sps[0];
            reader.Skip(8); // profile_idc
            reader.Skip(8); // constraint flags and reserved bits
            reader.Skip(8); // level_idc
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
            if (frameMbsOnly == 0)
            {
                reader.Skip(1); // mb_adaptive_frame_field_flag
            }

            reader.Skip(1); // direct_8x8_inference_flag
            var width = (int)(widthMbs * 16);
            var height = (int)((2 - frameMbsOnly) * heightMapUnits * 16);

            // A picture is encoded in whole sixteen-pixel blocks and cropped back to its real size, so
            // 360 rows are stored as 368 and trimmed. Without this a recording would claim the padding.
            if (reader.ReadBit() == 1)
            {
                var left = (int)reader.ReadUnsigned();
                var right = (int)reader.ReadUnsigned();
                var top = (int)reader.ReadUnsigned();
                var bottom = (int)reader.ReadUnsigned();
                // The crop is counted in colour samples, which in 4:2:0 are two luma samples wide and,
                // for a progressive picture, two tall.
                width -= (left + right) * 2;
                height -= (top + bottom) * 2 * (int)(2 - frameMbsOnly);
            }

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
        foreach (var range in Units(frame))
        {
            var unit = frame[range];
            if (unit.Length > 1 && (unit[0] & 0x1F) == 7)
            {
                return unit[1..];
            }
        }

        return default;
    }
}
