namespace Rumble.Net.Models;

/// <summary>Server information and configuration.</summary>
public sealed record ServerInfo
{
    /// <summary>Server version, e.g. <c>1.5.735</c>.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Server release string.</summary>
    public string Release { get; init; } = string.Empty;

    /// <summary>Server operating system.</summary>
    public string Os { get; init; } = string.Empty;

    /// <summary>Server operating system version.</summary>
    public string OsVersion { get; init; } = string.Empty;

    /// <summary>Welcome message (may be HTML).</summary>
    public string WelcomeText { get; init; } = string.Empty;

    /// <summary>Maximum bandwidth per user in bits/s.</summary>
    public uint MaxBandwidth { get; init; }

    /// <summary>Maximum users.</summary>
    public uint MaxUsers { get; init; }

    /// <summary>Whether HTML messages are allowed.</summary>
    public bool AllowHtml { get; init; }

    /// <summary>Maximum text message length.</summary>
    public uint MessageLength { get; init; }

    /// <summary>Maximum image message length.</summary>
    public uint ImageMessageLength { get; init; }

    /// <summary>Whether recording is allowed.</summary>
    public bool RecordingAllowed { get; init; }

    /// <summary>Administrator suggests positional audio.</summary>
    public bool? SuggestedPositional { get; init; }

    /// <summary>Administrator suggests push-to-talk.</summary>
    public bool? SuggestedPushToTalk { get; init; }

    /// <summary>Server supports Opus.</summary>
    public bool Opus { get; init; }

    /// <summary>Local user permissions in the root channel.</summary>
    public ulong RootPermissions { get; init; }

    /// <summary>SHA-1 fingerprint of the server certificate.</summary>
    public string? CertificateFingerprint { get; init; }
}

/// <summary>Serializable copy of the native state cache.</summary>
public sealed record ServerSnapshot
{
    /// <summary>Local session id.</summary>
    public uint? Session { get; init; }

    /// <summary>Initial synchronization finished.</summary>
    public bool Synchronized { get; init; }

    /// <summary>Server info.</summary>
    public ServerInfo Server { get; init; } = new();

    /// <summary>All channels.</summary>
    public IReadOnlyList<Channel> Channels { get; init; } = [];

    /// <summary>All users.</summary>
    public IReadOnlyList<User> Users { get; init; } = [];

    /// <summary>Average TCP ping in milliseconds.</summary>
    public float TcpPingMs { get; init; }

    /// <summary>Average UDP ping in milliseconds.</summary>
    public float UdpPingMs { get; init; }

    /// <summary>Voice is flowing over UDP (otherwise tunnelled through TCP).</summary>
    public bool UdpActive { get; init; }
}

/// <summary>Result of <see cref="RumbleClient.QueryServerAsync"/>.</summary>
public sealed record ServerQueryResult(string Version, uint Users, uint MaxUsers, uint MaxBandwidth, float PingMs);

/// <summary>Audio device information.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsInput, bool IsOutput, bool IsDefaultInput, bool IsDefaultOutput);

/// <summary>A generated client identity.</summary>
public sealed record GeneratedCertificate(string CertificatePem, string PrivateKeyPem, string Sha1Fingerprint);

/// <summary>A native benchmark measurement.</summary>
public sealed record BenchmarkResult(string Name, string Unit, uint Iterations, double NanosecondsPerOperation, double OperationsPerSecond);

/// <summary>Packet statistics.</summary>
public sealed record PacketStats(uint Good, uint Late, uint Lost, uint Resync);

/// <summary>A server ban entry.</summary>
public sealed record BanEntry(
    string Address,
    uint Mask,
    string? Name = null,
    string? CertificateHash = null,
    string? Reason = null,
    string? Start = null,
    uint? DurationSeconds = null);

/// <summary>A registered user account.</summary>
public sealed record RegisteredUser(uint UserId, string? Name, string? LastSeen, uint? LastChannel);

/// <summary>An ACL group definition.</summary>
public sealed record AclGroup(
    string Name,
    bool Inherited,
    bool Inherit,
    bool Inheritable,
    IReadOnlyList<uint> Add,
    IReadOnlyList<uint> Remove,
    IReadOnlyList<uint> InheritedMembers);

/// <summary>An ACL rule.</summary>
public sealed record AclEntry(bool ApplyHere, bool ApplySubs, bool Inherited, uint? UserId, string? Group, uint Grant, uint Deny);

/// <summary>A whisper/shout target entry.</summary>
public sealed record VoiceTargetEntry
{
    /// <summary>Target user sessions.</summary>
    public IReadOnlyList<uint> Sessions { get; init; } = [];

    /// <summary>Target channel.</summary>
    public uint? ChannelId { get; init; }

    /// <summary>Restrict to an ACL group.</summary>
    public string? Group { get; init; }

    /// <summary>Include linked channels.</summary>
    public bool Links { get; init; }

    /// <summary>Include sub-channels.</summary>
    public bool Children { get; init; }
}

/// <summary>Mumble permission bits.</summary>
[Flags]
public enum Permissions : uint
{
    /// <summary>No permissions.</summary>
    None = 0x0,
    /// <summary>Write ACL.</summary>
    Write = 0x1,
    /// <summary>Traverse.</summary>
    Traverse = 0x2,
    /// <summary>Enter.</summary>
    Enter = 0x4,
    /// <summary>Speak.</summary>
    Speak = 0x8,
    /// <summary>Mute/deafen others.</summary>
    MuteDeafen = 0x10,
    /// <summary>Move users.</summary>
    Move = 0x20,
    /// <summary>Make channels.</summary>
    MakeChannel = 0x40,
    /// <summary>Link channels.</summary>
    LinkChannel = 0x80,
    /// <summary>Whisper.</summary>
    Whisper = 0x100,
    /// <summary>Send text messages.</summary>
    TextMessage = 0x200,
    /// <summary>Make temporary channels.</summary>
    MakeTempChannel = 0x400,
    /// <summary>Listen to channels.</summary>
    Listen = 0x800,
    /// <summary>Kick users (root only).</summary>
    Kick = 0x10000,
    /// <summary>Ban users (root only).</summary>
    Ban = 0x20000,
    /// <summary>Register users (root only).</summary>
    Register = 0x40000,
    /// <summary>Register self (root only).</summary>
    SelfRegister = 0x80000,
    /// <summary>Reset user content (root only).</summary>
    ResetUserContent = 0x100000,
}
