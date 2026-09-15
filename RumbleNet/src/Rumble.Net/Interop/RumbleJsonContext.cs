using System.Text.Json.Serialization;
using Rumble.Net.Events;
using Rumble.Net.Models;

namespace Rumble.Net.Interop;

/// <summary>Source-generated JSON metadata (reflection-free, trimming and AOT friendly).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(RumbleEvent))]
[JsonSerializable(typeof(ServerSnapshot))]
[JsonSerializable(typeof(NativeClientConfig))]
[JsonSerializable(typeof(NativeCaptureConfig))]
[JsonSerializable(typeof(RumbleCommand))]
[JsonSerializable(typeof(List<AudioDevice>))]
[JsonSerializable(typeof(GeneratedCertificate))]
[JsonSerializable(typeof(ServerQueryResult))]
[JsonSerializable(typeof(List<BenchmarkResult>))]
internal sealed partial class RumbleJsonContext : JsonSerializerContext;

/// <summary>Mirror of the Rust <c>ClientConfig</c>.</summary>
internal sealed record NativeClientConfig
{
    public required string Host { get; init; }
    public ushort Port { get; init; }
    public required string Username { get; init; }
    public string? Password { get; init; }
    public IReadOnlyList<string> Tokens { get; init; } = [];
    public string? CertificatePem { get; init; }
    public string? PrivateKeyPem { get; init; }
    public TlsVerification TlsVerification { get; init; }
    public string? PinnedFingerprint { get; init; }
    public bool AutoReconnect { get; init; }
    public uint MaxReconnectAttempts { get; init; }
    public ulong ReconnectMinDelayMs { get; init; }
    public ulong ReconnectMaxDelayMs { get; init; }
    public ulong ConnectTimeoutMs { get; init; }
    public ulong PingIntervalMs { get; init; }
    public ulong PingTimeoutMs { get; init; }
    public bool ForceTcpVoice { get; init; }
    public bool IsBot { get; init; }
    public string? ClientRelease { get; init; }
    public bool PositionalTransmit { get; init; }
}

/// <summary>Mirror of the Rust capture configuration DTO.</summary>
internal sealed record NativeCaptureConfig(
    int Bitrate,
    int FramesPerPacket,
    int Complexity,
    bool InbandFec,
    int ExpectedPacketLoss,
    float VadThresholdDb,
    uint VadHoldFrames,
    float? NoiseGateDb,
    bool DcFilter);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DisconnectCommand), "Disconnect")]
[JsonDerivedType(typeof(JoinChannelCommand), "JoinChannel")]
[JsonDerivedType(typeof(SendTextMessageCommand), "SendTextMessage")]
[JsonDerivedType(typeof(SetSelfMuteCommand), "SetSelfMute")]
[JsonDerivedType(typeof(SetSelfDeafCommand), "SetSelfDeaf")]
[JsonDerivedType(typeof(SetCommentCommand), "SetComment")]
[JsonDerivedType(typeof(SetTextureCommand), "SetTexture")]
[JsonDerivedType(typeof(RegisterSelfCommand), "RegisterSelf")]
[JsonDerivedType(typeof(SetRecordingCommand), "SetRecording")]
[JsonDerivedType(typeof(MoveUserCommand), "MoveUser")]
[JsonDerivedType(typeof(SetUserMuteCommand), "SetUserMute")]
[JsonDerivedType(typeof(SetUserDeafCommand), "SetUserDeaf")]
[JsonDerivedType(typeof(SetPrioritySpeakerCommand), "SetPrioritySpeaker")]
[JsonDerivedType(typeof(SetUserSuppressedCommand), "SetUserSuppressed")]
[JsonDerivedType(typeof(KickUserCommand), "KickUser")]
[JsonDerivedType(typeof(BanUserCommand), "BanUser")]
[JsonDerivedType(typeof(CreateChannelCommand), "CreateChannel")]
[JsonDerivedType(typeof(UpdateChannelCommand), "UpdateChannel")]
[JsonDerivedType(typeof(RemoveChannelCommand), "RemoveChannel")]
[JsonDerivedType(typeof(LinkChannelsCommand), "LinkChannels")]
[JsonDerivedType(typeof(UnlinkChannelsCommand), "UnlinkChannels")]
[JsonDerivedType(typeof(ListenToChannelsCommand), "ListenToChannels")]
[JsonDerivedType(typeof(RegisterVoiceTargetCommand), "RegisterVoiceTarget")]
[JsonDerivedType(typeof(RequestUserStatsCommand), "RequestUserStats")]
[JsonDerivedType(typeof(RequestBanListCommand), "RequestBanList")]
[JsonDerivedType(typeof(SetBanListCommand), "SetBanList")]
[JsonDerivedType(typeof(RequestRegisteredUsersCommand), "RequestRegisteredUsers")]
[JsonDerivedType(typeof(RequestAclCommand), "RequestAcl")]
[JsonDerivedType(typeof(QueryPermissionsCommand), "QueryPermissions")]
[JsonDerivedType(typeof(QueryUsersCommand), "QueryUsers")]
[JsonDerivedType(typeof(RequestBlobCommand), "RequestBlob")]
[JsonDerivedType(typeof(SendPluginDataCommand), "SendPluginData")]
[JsonDerivedType(typeof(ExecuteContextActionCommand), "ExecuteContextAction")]
internal abstract record RumbleCommand;

internal sealed record DisconnectCommand : RumbleCommand;
internal sealed record JoinChannelCommand(uint ChannelId) : RumbleCommand;
internal sealed record SendTextMessageCommand(IReadOnlyList<uint> ChannelIds, IReadOnlyList<uint> Sessions, IReadOnlyList<uint> TreeIds, string Message) : RumbleCommand;
internal sealed record SetSelfMuteCommand(bool Mute) : RumbleCommand;
internal sealed record SetSelfDeafCommand(bool Deaf) : RumbleCommand;
internal sealed record SetCommentCommand(string Comment) : RumbleCommand;
internal sealed record SetTextureCommand(byte[] Texture) : RumbleCommand;
internal sealed record RegisterSelfCommand : RumbleCommand;
internal sealed record SetRecordingCommand(bool Recording) : RumbleCommand;
internal sealed record MoveUserCommand(uint Session, uint ChannelId) : RumbleCommand;
internal sealed record SetUserMuteCommand(uint Session, bool Mute) : RumbleCommand;
internal sealed record SetUserDeafCommand(uint Session, bool Deaf) : RumbleCommand;
internal sealed record SetPrioritySpeakerCommand(uint Session, bool Priority) : RumbleCommand;
internal sealed record SetUserSuppressedCommand(uint Session, bool Suppress) : RumbleCommand;
internal sealed record KickUserCommand(uint Session, string? Reason) : RumbleCommand;
internal sealed record BanUserCommand(uint Session, string? Reason) : RumbleCommand;
internal sealed record CreateChannelCommand(uint ParentId, string Name, string? Description, bool Temporary, int? Position, uint? MaxUsers) : RumbleCommand;
internal sealed record UpdateChannelCommand(uint ChannelId, string? Name, string? Description, uint? ParentId, int? Position, uint? MaxUsers) : RumbleCommand;
internal sealed record RemoveChannelCommand(uint ChannelId) : RumbleCommand;
internal sealed record LinkChannelsCommand(uint ChannelId, IReadOnlyList<uint> Targets) : RumbleCommand;
internal sealed record UnlinkChannelsCommand(uint ChannelId, IReadOnlyList<uint> Targets) : RumbleCommand;
internal sealed record ListenToChannelsCommand(IReadOnlyList<uint> Add, IReadOnlyList<uint> Remove) : RumbleCommand;
internal sealed record RegisterVoiceTargetCommand(uint Id, IReadOnlyList<VoiceTargetEntry> Targets) : RumbleCommand;
internal sealed record RequestUserStatsCommand(uint Session, bool StatsOnly) : RumbleCommand;
internal sealed record RequestBanListCommand : RumbleCommand;
internal sealed record SetBanListCommand(IReadOnlyList<BanEntry> Bans) : RumbleCommand;
internal sealed record RequestRegisteredUsersCommand : RumbleCommand;
internal sealed record RequestAclCommand(uint ChannelId) : RumbleCommand;
internal sealed record QueryPermissionsCommand(uint ChannelId) : RumbleCommand;
internal sealed record QueryUsersCommand(IReadOnlyList<uint> Ids, IReadOnlyList<string> Names) : RumbleCommand;
internal sealed record RequestBlobCommand(IReadOnlyList<uint> SessionTextures, IReadOnlyList<uint> SessionComments, IReadOnlyList<uint> ChannelDescriptions) : RumbleCommand;
internal sealed record SendPluginDataCommand(IReadOnlyList<uint> Receivers, string DataId, byte[] Data) : RumbleCommand;
internal sealed record ExecuteContextActionCommand(string Action, uint? Session, uint? ChannelId) : RumbleCommand;
