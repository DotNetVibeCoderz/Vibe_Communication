using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Rumble.Net.Interop;
using Rumble.Net.Models;

namespace Rumble.Net.Audio;

/// <summary>A decoded 10 ms mono 48 kHz frame from one speaker. Only valid during the callback.</summary>
public readonly ref struct AudioFrame
{
    internal AudioFrame(uint session, ReadOnlySpan<float> samples, Vector3? position, bool concealed)
    {
        Session = session;
        Samples = samples;
        Position = position;
        IsConcealed = concealed;
    }

    /// <summary>Speaker session id.</summary>
    public uint Session { get; }

    /// <summary>480 mono samples at 48 kHz (do not store the span).</summary>
    public ReadOnlySpan<float> Samples { get; }

    /// <summary>Speaker position when positional audio data was sent.</summary>
    public Vector3? Position { get; }

    /// <summary>True if the frame was synthesized by packet loss concealment.</summary>
    public bool IsConcealed { get; }

    /// <summary>Root-mean-square level (0–1).</summary>
    public float Rms => AudioMath.Rms(Samples);

    /// <summary>Level in dBFS.</summary>
    public float LevelDb => AudioMath.ToDecibels(Rms);
}

/// <summary>Handler for <see cref="RumbleAudio.AudioFrameReceived"/>.</summary>
public delegate void AudioFrameHandler(in AudioFrame frame);

/// <summary>Positional audio distance model.</summary>
/// <param name="MinDistance">Distance (m) without attenuation.</param>
/// <param name="MaxDistance">Distance (m) at which <paramref name="MinVolume"/> is reached.</param>
/// <param name="MinVolume">Volume beyond the maximum distance (0–1).</param>
/// <param name="RearAttenuation">Extra attenuation for sources behind the listener (0–1).</param>
public sealed record PositionalAudioSettings(float MinDistance = 1f, float MaxDistance = 15f, float MinVolume = 0.1f, float RearAttenuation = 0.25f);

/// <summary>Audio pipeline controls of a <see cref="RumbleClient"/>.</summary>
public sealed class RumbleAudio
{
    private readonly RumbleClient _client;
    private readonly Lock _lock = new();
    private AudioFrameHandler? _frameHandlers;
    private ImmutableArray<IAudioFilter> _filters = [];
    private TransmitMode _transmitMode;
    private bool _pushToTalk;
    private int _voiceTarget;
    private float _masterVolume;
    private PositionalAudioSettings? _positional;
    private (Vector3 Position, Vector3 Forward, Vector3 Up)? _listener;
    private readonly Dictionary<uint, float> _userVolumes = [];
    private readonly Dictionary<uint, bool> _userMuted = [];

    internal RumbleAudio(RumbleClient client)
    {
        _client = client;
        _transmitMode = client.Options.Audio.TransmitMode;
        _masterVolume = client.Options.Audio.MasterVolume;
        Mode = client.Options.Audio.Mode;
    }

    /// <summary>Current audio mode.</summary>
    public AudioMode Mode { get; private set; }

    /// <summary>Transmit mode.</summary>
    public TransmitMode TransmitMode
    {
        get => _transmitMode;
        set
        {
            _transmitMode = value;
            Invoke(h => NativeMethods.rumble_audio_set_transmit_mode(h, (int)value));
        }
    }

    /// <summary>Push-to-talk key state (used with <see cref="TransmitMode.PushToTalk"/>).</summary>
    public bool PushToTalk
    {
        get => _pushToTalk;
        set
        {
            _pushToTalk = value;
            Invoke(h => NativeMethods.rumble_audio_set_push_to_talk(h, value ? 1 : 0));
        }
    }

    /// <summary>Voice target: 0 normal talking, 1–30 registered targets, 31 server loopback.</summary>
    public int VoiceTarget
    {
        get => _voiceTarget;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 31);
            _voiceTarget = value;
            Invoke(h => NativeMethods.rumble_audio_set_voice_target(h, value));
        }
    }

    /// <summary>Playback master volume (1 = unchanged).</summary>
    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Max(0, value);
            Invoke(h => NativeMethods.rumble_audio_set_master_volume(h, _masterVolume));
        }
    }

    /// <summary>Current microphone level in dBFS.</summary>
    public float InputLevelDb => _client.Handle is { } h ? NativeMethods.rumble_audio_input_level(h) : -96f;

    /// <summary>True while voice is being transmitted.</summary>
    public bool IsTransmitting => _client.Handle is { } h && NativeMethods.rumble_audio_is_transmitting(h) != 0;

    /// <summary>Positional audio settings, or null when disabled.</summary>
    public PositionalAudioSettings? Positional => _positional;

    /// <summary>Raised on the audio thread for every decoded speaker frame (onAudioFrame). Keep handlers fast.</summary>
    public event AudioFrameHandler AudioFrameReceived
    {
        add
        {
            lock (_lock)
            {
                var install = _frameHandlers is null;
                _frameHandlers += value;
                if (install)
                {
                    InstallFrameCallback();
                }
            }
        }
        remove
        {
            lock (_lock)
            {
                _frameHandlers -= value;
                if (_frameHandlers is null)
                {
                    InstallFrameCallback();
                }
            }
        }
    }

    /// <summary>Capture filters, applied in order to every microphone/pushed frame before encoding.</summary>
    public IReadOnlyList<IAudioFilter> CaptureFilters => _filters;

    /// <summary>Adds a capture filter.</summary>
    public void AddCaptureFilter(IAudioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_lock)
        {
            _filters = _filters.Add(filter);
            InstallCaptureCallback();
        }
    }

    /// <summary>Removes a capture filter.</summary>
    public bool RemoveCaptureFilter(IAudioFilter filter)
    {
        lock (_lock)
        {
            var before = _filters.Length;
            _filters = _filters.Remove(filter);
            InstallCaptureCallback();
            return _filters.Length != before;
        }
    }

    /// <summary>Switches the audio mode (rebuilds the pipeline and applies the current capture options).</summary>
    public void SetMode(AudioMode mode, string? inputDeviceId = null, string? outputDeviceId = null)
    {
        Mode = mode;
        _client.Options.Audio.Mode = mode;
        if (inputDeviceId is not null)
        {
            _client.Options.Audio.InputDeviceId = inputDeviceId;
        }

        if (outputDeviceId is not null)
        {
            _client.Options.Audio.OutputDeviceId = outputDeviceId;
        }

        if (_client.Handle is { } h)
        {
            ApplyCaptureConfig(h);
            ApplyMode(h);
        }
    }

    /// <summary>Re-applies <see cref="RumbleClientOptions.Audio"/> (bitrate, VAD, devices…).</summary>
    public void ApplyOptions() => SetMode(_client.Options.Audio.Mode);

    /// <summary>Local volume for a user (does not affect others).</summary>
    public void SetUserVolume(User user, float volume)
    {
        lock (_lock)
        {
            _userVolumes[user.Session] = volume;
        }

        Invoke(h => NativeMethods.rumble_audio_set_user_volume(h, user.Session, volume));
    }

    /// <summary>Local volume for a user.</summary>
    public float GetUserVolume(User user)
    {
        lock (_lock)
        {
            return _userVolumes.GetValueOrDefault(user.Session, 1f);
        }
    }

    /// <summary>Locally mutes a user (does not affect others).</summary>
    public void SetUserMuted(User user, bool muted)
    {
        lock (_lock)
        {
            _userMuted[user.Session] = muted;
        }

        Invoke(h => NativeMethods.rumble_audio_set_user_muted(h, user.Session, muted ? 1 : 0));
    }

    /// <summary>True if the user is locally muted.</summary>
    public bool IsUserMuted(User user)
    {
        lock (_lock)
        {
            return _userMuted.GetValueOrDefault(user.Session);
        }
    }

    /// <summary>Enables positional audio (stereo panning + distance attenuation).</summary>
    public void EnablePositionalAudio(PositionalAudioSettings? settings = null)
    {
        _positional = settings ?? new PositionalAudioSettings();
        Invoke(ApplyPositional);
    }

    /// <summary>Disables positional audio.</summary>
    public void DisablePositionalAudio()
    {
        _positional = null;
        Invoke(ApplyPositional);
    }

    /// <summary>Sets the listener pose (meters; +X right, +Y up, +Z forward).</summary>
    public unsafe void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        _listener = (position, forward, up);
        Invoke(ApplyListener);
    }

    /// <summary>Sets the listener position facing +Z.</summary>
    public void SetListener(Vector3 position) => SetListener(position, Vector3.UnitZ, Vector3.UnitY);

    /// <summary>Transmits mono 48 kHz PCM (requires <see cref="AudioMode.Headless"/>).</summary>
    public unsafe void SendPcm(ReadOnlySpan<float> samples)
    {
        var h = _client.RequireHandle();
        fixed (float* p = samples)
        {
            Native.Check(NativeMethods.rumble_audio_push_pcm_f32(h, p, (nuint)samples.Length));
        }
    }

    /// <summary>Transmits mono 48 kHz 16-bit PCM (requires <see cref="AudioMode.Headless"/>).</summary>
    public unsafe void SendPcm(ReadOnlySpan<short> samples)
    {
        var h = _client.RequireHandle();
        fixed (short* p = samples)
        {
            Native.Check(NativeMethods.rumble_audio_push_pcm_i16(h, p, (nuint)samples.Length));
        }
    }

    /// <summary>Ends the current transmission (sends the terminator packet).</summary>
    public void EndTransmission() => Invoke(h => NativeMethods.rumble_audio_end_transmission(h));

    /// <summary>Lists audio devices.</summary>
    public static IReadOnlyList<AudioDevice> GetDevices() => RumbleNative.GetAudioDevices();

    // ------------------------------------------------------------------ native plumbing

    private void Invoke(Func<RumbleClientSafeHandle, int> call)
    {
        if (_client.Handle is { } h)
        {
            Native.Check(call(h));
        }
    }

    private void Invoke(Action<RumbleClientSafeHandle> call)
    {
        if (_client.Handle is { } h)
        {
            call(h);
        }
    }

    internal void ApplyAll(RumbleClientSafeHandle h)
    {
        lock (_lock)
        {
            Native.Check(NativeMethods.rumble_audio_set_transmit_mode(h, (int)_transmitMode));
            Native.Check(NativeMethods.rumble_audio_set_push_to_talk(h, _pushToTalk ? 1 : 0));
            Native.Check(NativeMethods.rumble_audio_set_voice_target(h, _voiceTarget));
            Native.Check(NativeMethods.rumble_audio_set_master_volume(h, _masterVolume));
            foreach (var (session, volume) in _userVolumes)
            {
                NativeMethods.rumble_audio_set_user_volume(h, session, volume);
            }

            foreach (var (session, muted) in _userMuted)
            {
                NativeMethods.rumble_audio_set_user_muted(h, session, muted ? 1 : 0);
            }

            ApplyPositional(h);
            ApplyListener(h);
            InstallFrameCallback();
            InstallCaptureCallback();
            ApplyCaptureConfig(h);
        }

        ApplyMode(h);
    }

    private unsafe void ApplyCaptureConfig(RumbleClientSafeHandle h)
    {
        var o = _client.Options.Audio;
        var config = new NativeCaptureConfig(
            o.Bitrate, o.FramesPerPacket, o.Complexity, o.InbandFec, o.ExpectedPacketLoss,
            o.VoiceActivityThresholdDb, (uint)Math.Max(0, o.VoiceActivityHoldFrames), o.NoiseGateDb, o.DcFilter);
        var json = Native.Json(config, RumbleJsonContext.Default.NativeCaptureConfig);
        fixed (byte* p = json.Span)
        {
            Native.Check(NativeMethods.rumble_audio_set_capture_config(h, p, (nuint)json.Span.Length));
        }
    }

    private unsafe void ApplyMode(RumbleClientSafeHandle h)
    {
        var o = _client.Options.Audio;
        using var input = new Native.Utf8Buffer(o.InputDeviceId);
        using var output = new Native.Utf8Buffer(o.OutputDeviceId);
        fixed (byte* pi = input.Span)
        fixed (byte* po = output.Span)
        {
            Native.Check(NativeMethods.rumble_audio_set_mode(h, (int)Mode, pi, (nuint)input.Span.Length, po, (nuint)output.Span.Length));
        }

        _client.Logger.LogDebug("Audio mode set to {Mode}", Mode);
    }

    private void ApplyPositional(RumbleClientSafeHandle h)
    {
        var s = _positional ?? new PositionalAudioSettings();
        Native.Check(NativeMethods.rumble_audio_set_positional(h, _positional is null ? 0 : 1, s.MinDistance, s.MaxDistance, s.MinVolume, s.RearAttenuation));
    }

    private unsafe void ApplyListener(RumbleClientSafeHandle h)
    {
        if (_listener is not { } l)
        {
            return;
        }

        var pose = stackalloc float[9]
        {
            l.Position.X, l.Position.Y, l.Position.Z,
            l.Forward.X, l.Forward.Y, l.Forward.Z,
            l.Up.X, l.Up.Y, l.Up.Z,
        };
        Native.Check(NativeMethods.rumble_audio_set_listener(h, pose));
    }

    private unsafe void InstallFrameCallback()
    {
        if (_client.Handle is not { } h)
        {
            return;
        }

        if (_frameHandlers is null)
        {
            NativeMethods.rumble_audio_set_frame_callback(h, null, 0);
        }
        else
        {
            Native.Check(NativeMethods.rumble_audio_set_frame_callback(h, &OnFrame, _client.CallbackUserData));
        }
    }

    private unsafe void InstallCaptureCallback()
    {
        if (_client.Handle is not { } h)
        {
            return;
        }

        if (_filters.IsEmpty)
        {
            NativeMethods.rumble_audio_set_capture_callback(h, null, 0);
        }
        else
        {
            Native.Check(NativeMethods.rumble_audio_set_capture_callback(h, &OnCapture, _client.CallbackUserData));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnFrame(nint userData, uint session, float* samples, nuint count, float* position, int concealed)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not RumbleClient client || client.Audio._frameHandlers is not { } handlers)
            {
                return;
            }

            Vector3? pos = position is null ? null : new Vector3(position[0], position[1], position[2]);
            var frame = new AudioFrame(session, new ReadOnlySpan<float>(samples, (int)count), pos, concealed != 0);
            handlers(in frame);
        }
        catch
        {
            // Exceptions must not cross into native code.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnCapture(nint userData, float* samples, nuint count)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not RumbleClient client)
            {
                return;
            }

            var span = new Span<float>(samples, (int)count);
            foreach (var filter in client.Audio._filters)
            {
                filter.Process(span);
            }
        }
        catch
        {
            // Exceptions must not cross into native code.
        }
    }
}
