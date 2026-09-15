using System.Text.Json.Serialization;
using Rumble.Net.Models;

namespace Rumble.Net.Events;

/// <summary>Connection lifecycle state.</summary>
public enum ConnectionState
{
    /// <summary>Not connected.</summary>
    Disconnected = 0,
    /// <summary>Establishing TCP/TLS.</summary>
    Connecting = 1,
    /// <summary>Authenticating and receiving the initial state.</summary>
    Synchronizing = 2,
    /// <summary>Connected and synchronized.</summary>
    Connected = 3,
    /// <summary>Connection lost; retrying.</summary>
    Reconnecting = 4,
}

/// <summary>
/// Base type of all events published by the native core. Unknown future event types deserialize
/// to this base type so older SDK versions keep working with newer native libraries.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType)]
[JsonDerivedType(typeof(StateChangedEvent), "StateChanged")]
[JsonDerivedType(typeof(ConnectingEvent), "Connecting")]
[JsonDerivedType(typeof(ServerCertificateEvent), "ServerCertificate")]
[JsonDerivedType(typeof(ConnectedEvent), "Connected")]
[JsonDerivedType(typeof(DisconnectedEvent), "Disconnected")]
[JsonDerivedType(typeof(RejectedEvent), "Rejected")]
[JsonDerivedType(typeof(KickedEvent), "Kicked")]
[JsonDerivedType(typeof(ChannelAddedEvent), "ChannelAdded")]
[JsonDerivedType(typeof(ChannelUpdatedEvent), "ChannelUpdated")]
[JsonDerivedType(typeof(ChannelRemovedEvent), "ChannelRemoved")]
[JsonDerivedType(typeof(UserJoinedEvent), "UserJoined")]
[JsonDerivedType(typeof(UserUpdatedEvent), "UserUpdated")]
[JsonDerivedType(typeof(UserMovedEvent), "UserMoved")]
[JsonDerivedType(typeof(UserLeftEvent), "UserLeft")]
[JsonDerivedType(typeof(UserTalkingEvent), "UserTalking")]
[JsonDerivedType(typeof(TextMessageEvent), "TextMessage")]
[JsonDerivedType(typeof(PermissionDeniedEvent), "PermissionDenied")]
[JsonDerivedType(typeof(PermissionsUpdatedEvent), "PermissionsUpdated")]
[JsonDerivedType(typeof(ServerConfigUpdatedEvent), "ServerConfigUpdated")]
[JsonDerivedType(typeof(PingUpdatedEvent), "PingUpdated")]
[JsonDerivedType(typeof(UserStatsEvent), "UserStats")]
[JsonDerivedType(typeof(BanListEvent), "BanList")]
[JsonDerivedType(typeof(RegisteredUsersEvent), "RegisteredUsers")]
[JsonDerivedType(typeof(AclEvent), "Acl")]
[JsonDerivedType(typeof(UsersQueriedEvent), "UsersQueried")]
[JsonDerivedType(typeof(ContextActionModifiedEvent), "ContextActionModified")]
[JsonDerivedType(typeof(PluginDataEvent), "PluginData")]
public record RumbleEvent;

/// <summary>An event type not known to this SDK version (emitted by a newer native core).</summary>
public sealed record UnknownEvent(string Type) : RumbleEvent;

/// <summary>The connection state changed.</summary>
public sealed record StateChangedEvent(ConnectionState State) : RumbleEvent;

/// <summary>A connection attempt started.</summary>
public sealed record ConnectingEvent(string Host, ushort Port, uint Attempt) : RumbleEvent;

/// <summary>The server presented its certificate (use for trust-on-first-use).</summary>
public sealed record ServerCertificateEvent(string Sha1Fingerprint, string Sha256Fingerprint) : RumbleEvent;

/// <summary>Authenticated and synchronized.</summary>
public sealed record ConnectedEvent(uint Session, string WelcomeText, ServerInfo Server) : RumbleEvent;

/// <summary>The connection closed.</summary>
public sealed record DisconnectedEvent(string Reason, bool WillReconnect) : RumbleEvent;

/// <summary>The server rejected the login.</summary>
public sealed record RejectedEvent(string RejectType, string Reason) : RumbleEvent;

/// <summary>The local user was kicked or banned.</summary>
public sealed record KickedEvent(uint? Actor, string Reason, bool Ban) : RumbleEvent;

/// <summary>A channel was created (or listed during synchronization).</summary>
public sealed record ChannelAddedEvent(Channel Channel) : RumbleEvent;

/// <summary>A channel changed.</summary>
public sealed record ChannelUpdatedEvent(Channel Channel) : RumbleEvent;

/// <summary>A channel was removed.</summary>
public sealed record ChannelRemovedEvent(uint ChannelId) : RumbleEvent;

/// <summary>A user connected (or was listed during synchronization).</summary>
public sealed record UserJoinedEvent(User User) : RumbleEvent;

/// <summary>A user's state changed.</summary>
public sealed record UserUpdatedEvent(User User, uint? Actor) : RumbleEvent;

/// <summary>A user moved between channels.</summary>
public sealed record UserMovedEvent(uint Session, uint FromChannelId, uint ToChannelId, uint? Actor) : RumbleEvent;

/// <summary>A user disconnected, was kicked or banned.</summary>
public sealed record UserLeftEvent(uint Session, uint? Actor, string? Reason, bool Ban) : RumbleEvent;

/// <summary>A user started or stopped talking.</summary>
public sealed record UserTalkingEvent(uint Session, bool Talking) : RumbleEvent;

/// <summary>A text message was received.</summary>
public sealed record TextMessageEvent(
    uint? Actor,
    IReadOnlyList<uint> Sessions,
    IReadOnlyList<uint> ChannelIds,
    IReadOnlyList<uint> TreeIds,
    string Message) : RumbleEvent
{
    /// <summary>True if sent directly to users (not to a channel).</summary>
    public bool IsPrivate => ChannelIds.Count == 0 && TreeIds.Count == 0;
}

/// <summary>The server denied an operation.</summary>
public sealed record PermissionDeniedEvent(
    string DenyType,
    string? Reason,
    uint? Permission,
    uint? ChannelId,
    uint? Session,
    string? Name) : RumbleEvent;

/// <summary>Permissions for a channel were reported.</summary>
public sealed record PermissionsUpdatedEvent(uint? ChannelId, uint? Permissions, bool Flush) : RumbleEvent;

/// <summary>Server configuration changed.</summary>
public sealed record ServerConfigUpdatedEvent(ServerInfo Server) : RumbleEvent;

/// <summary>Periodic ping statistics.</summary>
public sealed record PingUpdatedEvent(float TcpPingMs, float UdpPingMs, bool UdpActive, PacketStats Local, PacketStats Remote) : RumbleEvent;

/// <summary>Statistics of a user.</summary>
public sealed record UserStatsEvent(
    uint Session,
    bool StatsOnly,
    float TcpPingMs,
    float UdpPingMs,
    uint? OnlineSeconds,
    uint? IdleSeconds,
    uint? Bandwidth,
    string? Version,
    string? Os,
    string? Address,
    bool StrongCertificate,
    bool Opus,
    PacketStats FromClient,
    PacketStats FromServer) : RumbleEvent;

/// <summary>The server ban list.</summary>
public sealed record BanListEvent(IReadOnlyList<BanEntry> Bans) : RumbleEvent;

/// <summary>Registered user accounts.</summary>
public sealed record RegisteredUsersEvent(IReadOnlyList<RegisteredUser> Users) : RumbleEvent;

/// <summary>ACL of a channel.</summary>
public sealed record AclEvent(uint ChannelId, bool InheritAcls, IReadOnlyList<AclGroup> Groups, IReadOnlyList<AclEntry> Acls) : RumbleEvent;

/// <summary>Result of a user id/name query.</summary>
public sealed record UsersQueriedEvent(IReadOnlyList<uint> Ids, IReadOnlyList<string> Names) : RumbleEvent;

/// <summary>A server-defined context menu action was added or removed.</summary>
public sealed record ContextActionModifiedEvent(string Action, string? Text, uint Context, bool Remove) : RumbleEvent;

/// <summary>Plugin data sent by another client.</summary>
public sealed record PluginDataEvent(uint? SenderSession, string DataId, byte[] Data) : RumbleEvent;
