using System.Collections.Concurrent;
using VoipNet.Audio;
using VoipNet.Video;

namespace VoipNet.Enterprise.Recording;

/// <summary>
/// Records the whole room to one MP4: every participant decoded, laid out in a grid, encoded once,
/// with their voices mixed into the sound track.
/// </summary>
/// <remarks>
/// This is the trade the engine does not make. It forwards one participant's video untouched, which
/// costs nothing and keeps the quality the sender chose; a recording wants everybody at once, and
/// that means decoding each of them, composing a picture and encoding it again. The cost is real —
/// one decode per participant and one encode per frame — so it runs only while somebody is recording.
///
/// The picture is composed on its own clock rather than when frames happen to arrive, so a
/// participant whose video stalls simply stays as they were instead of stopping the recording.
/// </remarks>
public sealed class ConferenceRecorder : IDisposable
{
    /// <summary>How many arrived frames wait to be decoded per participant before the oldest is dropped.</summary>
    private const int Backlog = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Source> _sources = [];
    private readonly VideoCompositor _compositor;
    private readonly IVideoEncoder _encoder;
    private readonly Mp4Writer _writer;
    private readonly Thread _video;
    private readonly Thread _audio;
    private readonly int _framesPerSecond;
    private readonly int _sampleRate;
    private volatile bool _running = true;
    private DateTime _started = DateTime.UtcNow;

    private ConferenceRecorder(string path, int width, int height, int framesPerSecond, int sampleRate)
    {
        Path = path;
        _framesPerSecond = framesPerSecond;
        _sampleRate = sampleRate;
        _compositor = new VideoCompositor(width, height);
        _encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
        {
            Width = width,
            Height = height,
            FramesPerSecond = framesPerSecond,
            BitsPerSecond = 1_500_000,
        });

        _writer = new Mp4Writer(path, sampleRate);
        _video = new Thread(ComposeLoop) { IsBackground = true, Name = "room recording video" };
        _audio = new Thread(MixLoop) { IsBackground = true, Name = "room recording audio" };
        _video.Start();
        _audio.Start();
    }

    /// <summary>Whether this machine has the codec to record with.</summary>
    public static bool IsSupported => VideoCodecs.IsH264Available;

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>How long the recording has been running.</summary>
    public TimeSpan Duration => DateTime.UtcNow - _started;

    /// <summary>Starts a recording, or returns null where there is no codec to make one with.</summary>
    /// <param name="path">Destination MP4.</param>
    /// <param name="width">Width of the composed picture.</param>
    /// <param name="height">Height of the composed picture.</param>
    /// <param name="framesPerSecond">How often the room is composed.</param>
    /// <param name="sampleRate">Sample rate of the mixed sound track.</param>
    public static ConferenceRecorder? Start(string path, int width = 960, int height = 540, int framesPerSecond = 12, int sampleRate = 48000) =>
        IsSupported ? new ConferenceRecorder(path, width, height, framesPerSecond, sampleRate) : null;

    /// <summary>Adds a call: its picture joins the grid and its voice the sound track.</summary>
    /// <param name="call">A leg of the conference, as the bridge sees it.</param>
    public void Add(VoipCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        lock (_gate)
        {
            if (!_running || _sources.ContainsKey(call.Id))
            {
                return;
            }

            var source = new Source(call, _sources.Count);
            _sources[call.Id] = source;
            call.VideoFrameReceived += source.OnVideo;
            call.AudioReceived += source.OnAudio;
            source.Start();
        }
    }

    /// <summary>Takes a call out again when it leaves.</summary>
    /// <param name="call">The leg that left.</param>
    public void Remove(VoipCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        lock (_gate)
        {
            if (_sources.Remove(call.Id, out var source))
            {
                call.VideoFrameReceived -= source.OnVideo;
                call.AudioReceived -= source.OnAudio;
                source.Dispose();
            }
        }
    }

    /// <summary>Stops recording and finishes the file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            foreach (var (_, source) in _sources)
            {
                source.Call.VideoFrameReceived -= source.OnVideo;
                source.Call.AudioReceived -= source.OnAudio;
            }
        }

        _video.Join(TimeSpan.FromSeconds(3));
        _audio.Join(TimeSpan.FromSeconds(3));

        lock (_gate)
        {
            foreach (var (_, source) in _sources)
            {
                source.Dispose();
            }

            _sources.Clear();
        }

        foreach (var frame in _encoder.Drain())
        {
            _writer.WriteVideo(frame.Data.Span, frame.Keyframe, (uint)(frame.Timestamp.TotalSeconds * 90_000));
        }

        _encoder.Dispose();
        _writer.Dispose();
    }

    private void ComposeLoop()
    {
        var interval = TimeSpan.FromSeconds(1.0 / _framesPerSecond);
        var next = DateTime.UtcNow + interval;
        while (_running)
        {
            var wait = next - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }

            next += interval;
            List<VideoPicture> pictures;
            lock (_gate)
            {
                // In join order, so the grid does not shuffle itself while somebody is watching.
                pictures = _sources.Values.OrderBy(s => s.Order).Select(s => s.Latest).OfType<VideoPicture>().ToList();
            }

            if (pictures.Count == 0)
            {
                continue;
            }

            var elapsed = DateTime.UtcNow - _started;
            var composed = _compositor.Compose(pictures, CompositorLayout.Grid, elapsed);
            foreach (var frame in _encoder.Encode(composed))
            {
                _writer.WriteVideo(frame.Data.Span, frame.Keyframe, (uint)(frame.Timestamp.TotalSeconds * 90_000));
            }
        }
    }

    private void MixLoop()
    {
        // Twenty milliseconds at a time, which is what the engine hands over anyway.
        var frame = _sampleRate / 50;
        var mixed = new short[frame];
        while (_running)
        {
            Thread.Sleep(20);
            Array.Clear(mixed);
            var heard = false;
            lock (_gate)
            {
                foreach (var source in _sources.Values)
                {
                    if (source.Take(mixed))
                    {
                        heard = true;
                    }
                }
            }

            // Silence is written too: a gap in the sound track would pull the picture out of step.
            _ = heard;
            _writer.WriteAudio(mixed);
        }
    }

    /// <summary>One participant as the recording sees them: a decoder, their last picture, their voice.</summary>
    private sealed class Source(VoipCall call, int order) : IDisposable
    {
        private readonly BlockingCollection<byte[]> _frames = new(Backlog);
        private readonly ConcurrentQueue<short> _audio = new();
        private readonly Lock _picture = new();
        private Thread? _decoder;
        private VideoPicture? _latest;
        private volatile bool _running = true;

        internal VoipCall Call { get; } = call;

        /// <summary>Where this one sits in the grid: the order they joined, so nothing shuffles.</summary>
        internal int Order { get; } = order;

        internal VideoPicture? Latest
        {
            get
            {
                lock (_picture)
                {
                    return _latest;
                }
            }
        }

        internal void Start()
        {
            _decoder = new Thread(Decode) { IsBackground = true, Name = $"decode call {Call.Id}" };
            _decoder.Start();
        }

        internal void OnVideo(VoipCall sender, uint timestamp, bool keyframe, ReadOnlySpan<byte> frame, string content)
        {
            if (!_running || content != "main")
            {
                return;
            }

            while (_frames.Count >= Backlog && _frames.TryTake(out _))
            {
            }

            _frames.TryAdd(frame.ToArray());
        }

        internal void OnAudio(VoipCall sender, AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples)
        {
            if (!_running || direction != AudioDirection.Inbound)
            {
                return;
            }

            // A second of backlog is already far more than a recording can use.
            if (_audio.Count > sampleRate)
            {
                return;
            }

            foreach (var sample in samples)
            {
                _audio.Enqueue(sample);
            }
        }

        /// <summary>Adds this participant's next slice of sound to the mix, if they have any.</summary>
        internal bool Take(short[] into)
        {
            if (_audio.Count < into.Length)
            {
                return false;
            }

            for (var i = 0; i < into.Length && _audio.TryDequeue(out var sample); i++)
            {
                into[i] = (short)Math.Clamp(into[i] + sample, short.MinValue, short.MaxValue);
            }

            return true;
        }

        public void Dispose()
        {
            _running = false;
            _frames.CompleteAdding();
            _decoder?.Join(TimeSpan.FromSeconds(2));
            _frames.Dispose();
        }

        private void Decode()
        {
            using var decoder = VideoCodecs.CreateH264Decoder();
            foreach (var frame in _frames.GetConsumingEnumerable())
            {
                if (!_running)
                {
                    return;
                }

                foreach (var picture in decoder.Decode(frame, TimeSpan.Zero))
                {
                    lock (_picture)
                    {
                        _latest = picture;
                    }
                }
            }
        }
    }
}
