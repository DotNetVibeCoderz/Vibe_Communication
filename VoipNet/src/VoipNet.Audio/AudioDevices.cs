using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.EXT;
using EnumerationExtension = Silk.NET.OpenAL.Extensions.Enumeration.Enumeration;
using EnumerationStrings = Silk.NET.OpenAL.Extensions.Enumeration.GetEnumerationContextStringList;

namespace VoipNet.Audio;

/// <summary>Lists the microphones and speakers OpenAL can see.</summary>
public static class AudioDevices
{
    /// <summary>True when an OpenAL implementation could be loaded on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                using var context = ALContext.GetApi(soft: true);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Names of the available speakers. The first entry is the system default.</summary>
    public static IReadOnlyList<string> Playback() => Query(EnumerationStrings.DeviceSpecifiers);

    /// <summary>Names of the available microphones. The first entry is the system default.</summary>
    public static IReadOnlyList<string> Capture()
    {
        try
        {
            using var alc = ALContext.GetApi(soft: true);
            unsafe
            {
                if (alc.TryGetExtension<CaptureEnumerationEnumeration>(null, out var enumeration))
                {
                    return [.. enumeration.GetStringList(Silk.NET.OpenAL.Extensions.EXT.Enumeration.GetCaptureContextStringList.CaptureDeviceSpecifiers)];
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            // No audio stack on this machine (headless container, for example).
        }

        return [];
    }

    private static IReadOnlyList<string> Query(EnumerationStrings kind)
    {
        try
        {
            using var alc = ALContext.GetApi(soft: true);
            unsafe
            {
                if (alc.TryGetExtension<EnumerationExtension>(null, out var enumeration))
                {
                    return [.. enumeration.GetStringList(kind)];
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            // Ignored: callers fall back to a silent device.
        }

        return [];
    }
}

/// <summary>Captures microphone audio in fixed size frames.</summary>
public sealed class MicrophoneStream : IDisposable
{
    private readonly ALContext _alc;
    private readonly AudioCapture<BufferFormat> _capture;
    private readonly Thread _thread;
    private readonly int _frameSamples;
    private volatile bool _running;

    /// <summary>Opens a microphone.</summary>
    /// <param name="sampleRate">Capture rate in hertz.</param>
    /// <param name="frameMs">Frame size delivered to <see cref="FrameCaptured"/>.</param>
    /// <param name="deviceName">Device name, or null for the system default.</param>
    public MicrophoneStream(int sampleRate = 16000, int frameMs = 20, string? deviceName = null)
    {
        SampleRate = sampleRate;
        _frameSamples = sampleRate * frameMs / 1000;
        FrameMs = frameMs;
        _alc = ALContext.GetApi(soft: true);
        unsafe
        {
            if (!_alc.TryGetExtension<Capture>(null, out var capture))
            {
                throw new NotSupportedException("This OpenAL implementation cannot capture audio.");
            }

            _capture = capture.CreateCapture<BufferFormat>(deviceName!, (uint)sampleRate, BufferFormat.Mono16, _frameSamples * 8);
        }

        _running = true;
        _capture.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "voipnet-microphone" };
        _thread.Start();
    }

    /// <summary>Capture sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Length of each captured frame in milliseconds, which is also the capture latency.</summary>
    public int FrameMs { get; }

    /// <summary>Set to true to deliver silence instead of the microphone signal.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Raised for each captured frame, on a background thread.</summary>
    public event AudioFrameCapturedHandler? FrameCaptured;

    private void Loop()
    {
        var buffer = new short[_frameSamples];
        while (_running)
        {
            if (_capture.AvailableSamples < _frameSamples)
            {
                Thread.Sleep(5);
                continue;
            }

            _capture.CaptureSamples(_frameSamples, in buffer);
            if (IsMuted)
            {
                Array.Clear(buffer);
            }

            FrameCaptured?.Invoke(buffer.AsSpan(0, _frameSamples), SampleRate);
        }
    }

    /// <summary>Stops capturing and releases the device.</summary>
    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _thread.Join(500);
        try
        {
            _capture.Stop();
            _capture.Dispose();
        }
        finally
        {
            _alc.Dispose();
        }
    }
}

/// <summary>Receives a captured microphone frame.</summary>
/// <param name="samples">16-bit mono PCM.</param>
/// <param name="sampleRate">Sample rate of the frame.</param>
public delegate void AudioFrameCapturedHandler(ReadOnlySpan<short> samples, int sampleRate);

/// <summary>Plays 16-bit mono PCM through the speakers with a small streaming queue.</summary>
public sealed class SpeakerStream : IDisposable
{
    private const int BufferCount = 8;

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Queue<short[]> _pending = new();
    private readonly Lock _gate = new();
    private readonly Thread _thread;
    private readonly uint _source;
    private readonly uint[] _buffers = new uint[BufferCount];
    private readonly Queue<uint> _free = new();
    private volatile bool _running;
    /// Samples handed to the device or waiting for it, which is the playback latency.
    private int _queuedSamples;
    private unsafe Device* _device;
    private unsafe Context* _context;

    /// <summary>Opens a speaker device.</summary>
    /// <param name="sampleRate">Playback rate in hertz.</param>
    /// <param name="deviceName">Device name, or null for the system default.</param>
    public unsafe SpeakerStream(int sampleRate = 16000, string? deviceName = null)
    {
        SampleRate = sampleRate;
        _alc = ALContext.GetApi(soft: true);
        _al = AL.GetApi(soft: true);
        _device = _alc.OpenDevice(deviceName ?? string.Empty);
        if (_device is null)
        {
            throw new InvalidOperationException("No audio output device is available.");
        }

        _context = _alc.CreateContext(_device, null);
        _alc.MakeContextCurrent(_context);
        _source = _al.GenSource();
        fixed (uint* buffers = _buffers)
        {
            _al.GenBuffers(BufferCount, buffers);
        }

        foreach (var b in _buffers)
        {
            _free.Enqueue(b);
        }

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "voipnet-speaker" };
        _thread.Start();
    }

    /// <summary>Playback sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Playback volume, where 1.0 is unity gain.</summary>
    public float Volume
    {
        get;
        set
        {
            field = value;
            _al.SetSourceProperty(_source, SourceFloat.Gain, value);
        }
    } = 1.0f;

    /// <summary>Queues audio for playback.</summary>
    /// <param name="samples">16-bit mono PCM at <see cref="SampleRate"/>.</param>
    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            // Bound the queue so a burst of audio cannot grow into seconds of latency.
            if (_pending.Count > 50)
            {
                _pending.Dequeue();
            }

            _pending.Enqueue(samples.ToArray());
            _queuedSamples += samples.Length;
        }
    }

    /// <summary>How much audio is queued for playback, in milliseconds.</summary>
    public int QueuedMs
    {
        get
        {
            lock (_gate)
            {
                return _queuedSamples * 1000 / Math.Max(SampleRate, 1);
            }
        }
    }

    /// <summary>Drops everything queued but not yet played.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
            _queuedSamples = 0;
        }
    }

    private unsafe void Loop()
    {
        while (_running)
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
            while (processed-- > 0)
            {
                uint done;
                _al.SourceUnqueueBuffers(_source, 1, &done);
                _al.GetBufferProperty(done, GetBufferInteger.Size, out var bytes);
                _free.Enqueue(done);
                lock (_gate)
                {
                    _queuedSamples = Math.Max(0, _queuedSamples - (bytes / sizeof(short)));
                }
            }

            short[]? block = null;
            lock (_gate)
            {
                if (_pending.Count > 0 && _free.Count > 0)
                {
                    block = _pending.Dequeue();
                }
            }

            if (block is null)
            {
                Thread.Sleep(5);
                continue;
            }

            var buffer = _free.Dequeue();
            fixed (short* data = block)
            {
                _al.BufferData(buffer, BufferFormat.Mono16, data, block.Length * sizeof(short), SampleRate);
            }

            _al.SourceQueueBuffers(_source, 1, &buffer);
            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
            if ((SourceState)state != SourceState.Playing)
            {
                _al.SourcePlay(_source);
            }
        }
    }

    /// <summary>Stops playback and releases the device.</summary>
    public unsafe void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _thread.Join(500);
        _al.SourceStop(_source);
        _al.DeleteSource(_source);
        fixed (uint* buffers = _buffers)
        {
            _al.DeleteBuffers(BufferCount, buffers);
        }

        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
        _al.Dispose();
        _alc.Dispose();
    }
}

/// <summary>
/// Connects a call to the local microphone and speakers, converting sample rates in both
/// directions. This is what turns the SDK into a working softphone.
/// </summary>
public sealed class CallAudioBridge : IDisposable
{
    private readonly VoipCall _call;
    private readonly MicrophoneStream? _microphone;
    private readonly SpeakerStream? _speaker;
    private readonly ILogger _logger;
    private readonly List<short> _scratch = new(2048);
    private AudioResampler? _toSpeaker;
    private int _reportedDelayMs = -1;
    private bool _disposed;

    private CallAudioBridge(VoipCall call, MicrophoneStream? microphone, SpeakerStream? speaker, ILogger logger)
    {
        _call = call;
        _microphone = microphone;
        _speaker = speaker;
        _logger = logger;

        if (_microphone is not null)
        {
            _microphone.FrameCaptured += OnMicrophoneFrame;
        }

        call.AudioReceived += OnCallAudio;
        call.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Opens the default microphone and speakers for a call. When no audio device is available
    /// the bridge stays silent instead of failing, so headless hosts keep working.
    /// </summary>
    /// <param name="call">Call to attach to.</param>
    /// <param name="deviceSampleRate">Rate used with the audio devices.</param>
    /// <param name="logger">Optional logger.</param>
    public static CallAudioBridge Attach(VoipCall call, int deviceSampleRate = 16000, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        logger ??= NullLogger.Instance;
        MicrophoneStream? microphone = null;
        SpeakerStream? speaker = null;
        try
        {
            microphone = new MicrophoneStream(deviceSampleRate);
            speaker = new SpeakerStream(deviceSampleRate);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or DllNotFoundException or TypeInitializationException)
        {
            logger.LogWarning(ex, "No audio devices available; the call will run without local audio");
            microphone?.Dispose();
            speaker?.Dispose();
            microphone = null;
            speaker = null;
        }

        return new CallAudioBridge(call, microphone, speaker, logger);
    }

    /// <summary>True when both a microphone and a speaker were opened.</summary>
    public bool HasDevices => _microphone is not null && _speaker is not null;

    /// <summary>Mutes the microphone locally, without telling the remote party.</summary>
    public bool IsMicrophoneMuted
    {
        get => _microphone?.IsMuted ?? true;
        set
        {
            if (_microphone is not null)
            {
                _microphone.IsMuted = value;
            }
        }
    }

    /// <summary>Speaker volume, where 1.0 is unity gain.</summary>
    public float Volume
    {
        get => _speaker?.Volume ?? 0;
        set
        {
            if (_speaker is not null)
            {
                _speaker.Volume = value;
            }
        }
    }

    /// <summary>
    /// Tells the engine how far the microphone lags the speaker, which is what the echo canceller
    /// needs to line the two up: the audio still queued for playback plus one captured frame.
    /// Reported only when it moves, since it changes slowly and every call crosses into native code.
    /// </summary>
    private void ReportDelay()
    {
        if (_speaker is null)
        {
            return;
        }

        var delay = _speaker.QueuedMs + (_microphone?.FrameMs ?? 20);
        if (Math.Abs(delay - _reportedDelayMs) < 10)
        {
            return;
        }

        _reportedDelayMs = delay;
        try
        {
            _call.SetAudioDelay(delay);
        }
        catch (VoipException)
        {
            // The call ended between the frame and this report; the next call reports again.
        }
    }

    private void OnMicrophoneFrame(ReadOnlySpan<short> samples, int sampleRate)
    {
        if (_disposed || !_call.IsActive)
        {
            return;
        }

        try
        {
            // The engine resamples on its own, so send the microphone rate straight through.
            _call.SendAudio(samples, sampleRate);
            ReportDelay();
        }
        catch (VoipException ex)
        {
            _logger.LogDebug(ex, "Dropping a microphone frame: the call is no longer accepting audio");
        }
    }

    private void OnCallAudio(VoipCall call, AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples)
    {
        if (direction != AudioDirection.Inbound || _speaker is null || _disposed)
        {
            return;
        }

        if (sampleRate == _speaker.SampleRate)
        {
            _speaker.Write(samples);
            return;
        }

        _toSpeaker ??= new AudioResampler(sampleRate, _speaker.SampleRate);
        _scratch.Clear();
        _toSpeaker.Process(samples, _scratch);
        _speaker.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_scratch));
    }

    private void OnStateChanged(object? sender, CallStateEventArgs e)
    {
        if (e.State == CallState.Terminated)
        {
            Dispose();
        }
        else if (e.State is CallState.OnHold or CallState.RemoteHold)
        {
            _speaker?.Clear();
        }
    }

    /// <summary>Detaches from the call and releases the devices.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _call.AudioReceived -= OnCallAudio;
        _call.StateChanged -= OnStateChanged;
        if (_microphone is not null)
        {
            _microphone.FrameCaptured -= OnMicrophoneFrame;
            _microphone.Dispose();
        }

        _speaker?.Dispose();
    }
}
