using Microsoft.Extensions.Logging;

namespace Rumble.Net;

/// <summary>How the server certificate is validated.</summary>
public enum TlsVerification
{
    /// <summary>Accept any certificate; inspect <see cref="Events.ServerCertificateEvent"/> for trust-on-first-use.</summary>
    AcceptAll,
    /// <summary>Only accept the certificate whose SHA-1 fingerprint equals <see cref="RumbleClientOptions.PinnedFingerprint"/>.</summary>
    Pinned,
    /// <summary>Validate against public certificate authorities.</summary>
    WebPki,
}

/// <summary>Audio pipeline mode.</summary>
public enum AudioMode
{
    /// <summary>Received voice is ignored and nothing is transmitted.</summary>
    Disabled = 0,
    /// <summary>No devices: decoded frames go to <see cref="Audio.RumbleAudio.AudioFrameReceived"/>, outgoing audio is pushed with <see cref="Audio.RumbleAudio.SendPcm(ReadOnlySpan{float})"/>.</summary>
    Headless = 1,
    /// <summary>Microphone and speakers through the platform audio backend.</summary>
    Devices = 2,
}

/// <summary>When the local user transmits.</summary>
public enum TransmitMode
{
    /// <summary>Always transmit.</summary>
    Continuous = 0,
    /// <summary>Transmit when speech is detected.</summary>
    VoiceActivity = 1,
    /// <summary>Transmit while push-to-talk is held.</summary>
    PushToTalk = 2,
}

/// <summary>Audio options applied when the client connects.</summary>
public sealed class AudioOptions
{
    /// <summary>Audio mode. Default: <see cref="AudioMode.Disabled"/>.</summary>
    public AudioMode Mode { get; set; } = AudioMode.Disabled;

    /// <summary>Input device id (see <see cref="RumbleNative.GetAudioDevices"/>); null = system default.</summary>
    public string? InputDeviceId { get; set; }

    /// <summary>Output device id; null = system default.</summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>Transmit mode.</summary>
    public TransmitMode TransmitMode { get; set; } = TransmitMode.VoiceActivity;

    /// <summary>Opus bitrate in bits per second (8 000 – 510 000).</summary>
    public int Bitrate { get; set; } = 48_000;

    /// <summary>10 ms frames per packet: 1, 2, 4 or 6.</summary>
    public int FramesPerPacket { get; set; } = 2;

    /// <summary>Opus complexity 0–10.</summary>
    public int Complexity { get; set; } = 8;

    /// <summary>Opus in-band forward error correction.</summary>
    public bool InbandFec { get; set; } = true;

    /// <summary>Expected packet loss percentage used to tune FEC.</summary>
    public int ExpectedPacketLoss { get; set; } = 5;

    /// <summary>Voice activity threshold in dBFS.</summary>
    public float VoiceActivityThresholdDb { get; set; } = -45f;

    /// <summary>Frames to keep transmitting after speech stops.</summary>
    public int VoiceActivityHoldFrames { get; set; } = 25;

    /// <summary>Optional noise gate threshold in dBFS.</summary>
    public float? NoiseGateDb { get; set; }

    /// <summary>Remove DC offset from the microphone.</summary>
    public bool DcFilter { get; set; } = true;

    /// <summary>Playback master volume (1 = unchanged).</summary>
    public float MasterVolume { get; set; } = 1f;
}

/// <summary>Options for <see cref="RumbleClient"/>.</summary>
public sealed class RumbleClientOptions
{
    /// <summary>Server host name or IP address.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Server port.</summary>
    public int Port { get; set; } = 64738;

    /// <summary>User name.</summary>
    public string Username { get; set; } = "RumbleUser";

    /// <summary>Server or account password.</summary>
    public string? Password { get; set; }

    /// <summary>Access tokens for ACL groups.</summary>
    public IList<string> Tokens { get; set; } = [];

    /// <summary>PEM client certificate (identity for registered users).</summary>
    public string? CertificatePem { get; set; }

    /// <summary>PEM private key matching <see cref="CertificatePem"/>.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Server certificate validation policy.</summary>
    public TlsVerification TlsVerification { get; set; } = TlsVerification.AcceptAll;

    /// <summary>Pinned SHA-1 fingerprint used with <see cref="TlsVerification.Pinned"/>.</summary>
    public string? PinnedFingerprint { get; set; }

    /// <summary>Reconnect automatically after the connection is lost (after the first successful connect).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Maximum reconnect attempts (0 = unlimited).</summary>
    public int MaxReconnectAttempts { get; set; }

    /// <summary>Initial reconnect delay (doubles per attempt).</summary>
    public TimeSpan ReconnectMinDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum reconnect delay.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for DNS, TCP and TLS during a connection attempt.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Heartbeat interval.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The connection is considered lost without a heartbeat reply within this time.</summary>
    public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default timeout for request/response style operations.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Tunnel voice through TCP (for networks that block UDP).</summary>
    public bool ForceTcpVoice { get; set; }

    /// <summary>Identify as a bot to the server.</summary>
    public bool IsBot { get; set; }

    /// <summary>Client release string shown to other users.</summary>
    public string? ClientRelease { get; set; }

    /// <summary>Include the listener position in outgoing voice (positional audio for others).</summary>
    public bool PositionalTransmit { get; set; }

    /// <summary>Audio options.</summary>
    public AudioOptions Audio { get; set; } = new();

    /// <summary>Logger factory for SDK and native core diagnostics.</summary>
    public ILoggerFactory? LoggerFactory { get; set; }

    /// <summary>Minimum level forwarded from the native core.</summary>
    public LogLevel NativeLogLevel { get; set; } = LogLevel.Information;

    /// <summary>Validates the options, throwing <see cref="ArgumentException"/> when invalid.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host must be set.", nameof(Host));
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new ArgumentException("Username must be set.", nameof(Username));
        }

        if (Port is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be between 1 and 65535.");
        }

        if ((CertificatePem is null) != (PrivateKeyPem is null))
        {
            throw new ArgumentException("CertificatePem and PrivateKeyPem must be provided together.", nameof(CertificatePem));
        }

        if (TlsVerification == TlsVerification.Pinned && string.IsNullOrWhiteSpace(PinnedFingerprint))
        {
            throw new ArgumentException("PinnedFingerprint is required for pinned verification.", nameof(PinnedFingerprint));
        }

        if (PingTimeout <= PingInterval)
        {
            throw new ArgumentException("PingTimeout must be greater than PingInterval.", nameof(PingTimeout));
        }
    }

    internal Interop.NativeClientConfig ToNative() => new()
    {
        Host = Host,
        Port = (ushort)Port,
        Username = Username,
        Password = Password,
        Tokens = [.. Tokens],
        CertificatePem = CertificatePem,
        PrivateKeyPem = PrivateKeyPem,
        TlsVerification = TlsVerification,
        PinnedFingerprint = PinnedFingerprint,
        AutoReconnect = AutoReconnect,
        MaxReconnectAttempts = (uint)Math.Max(0, MaxReconnectAttempts),
        ReconnectMinDelayMs = (ulong)ReconnectMinDelay.TotalMilliseconds,
        ReconnectMaxDelayMs = (ulong)ReconnectMaxDelay.TotalMilliseconds,
        ConnectTimeoutMs = (ulong)ConnectTimeout.TotalMilliseconds,
        PingIntervalMs = (ulong)PingInterval.TotalMilliseconds,
        PingTimeoutMs = (ulong)PingTimeout.TotalMilliseconds,
        ForceTcpVoice = ForceTcpVoice,
        IsBot = IsBot,
        ClientRelease = ClientRelease ?? $"Rumble.Net {typeof(RumbleClient).Assembly.GetName().Version?.ToString(3)}",
        PositionalTransmit = PositionalTransmit,
    };
}
