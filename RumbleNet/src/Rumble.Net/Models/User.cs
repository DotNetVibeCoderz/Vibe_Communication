using System.Text.Json.Serialization;

namespace Rumble.Net.Models;

/// <summary>A connected user (immutable snapshot with live navigation).</summary>
public sealed class User
{
    /// <summary>Session id (unique per connection).</summary>
    public uint Session { get; init; }

    /// <summary>Registered user id, or <c>null</c> for unregistered users.</summary>
    public uint? UserId { get; init; }

    /// <summary>Display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Current channel id.</summary>
    public uint ChannelId { get; init; }

    /// <summary>Muted by an administrator.</summary>
    public bool Mute { get; init; }

    /// <summary>Deafened by an administrator.</summary>
    public bool Deaf { get; init; }

    /// <summary>Suppressed by the server (e.g. no speak permission).</summary>
    public bool Suppress { get; init; }

    /// <summary>Muted by the user.</summary>
    public bool SelfMute { get; init; }

    /// <summary>Deafened by the user.</summary>
    public bool SelfDeaf { get; init; }

    /// <summary>Priority speaker.</summary>
    public bool PrioritySpeaker { get; init; }

    /// <summary>Currently recording.</summary>
    public bool Recording { get; init; }

    /// <summary>User comment (may be HTML).</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>SHA-1 of a large comment that was not sent inline.</summary>
    public string? CommentHash { get; init; }

    /// <summary>Avatar image bytes, if sent.</summary>
    public byte[]? Texture { get; init; }

    /// <summary>SHA-1 of a large avatar that was not sent inline.</summary>
    public string? TextureHash { get; init; }

    /// <summary>SHA-1 hash of the user's certificate.</summary>
    public string? CertificateHash { get; init; }

    /// <summary>Channels this user listens to.</summary>
    public IReadOnlyList<uint> ListeningChannels { get; init; } = [];

    [JsonIgnore]
    internal ServerModel? Model { get; set; }

    /// <summary>The user's current channel.</summary>
    [JsonIgnore]
    public Channel? Channel => Model?.GetChannel(ChannelId);

    /// <summary>True if this is the local user.</summary>
    [JsonIgnore]
    public bool IsSelf => Model?.Session == Session;

    /// <summary>True if the user has a registered account.</summary>
    [JsonIgnore]
    public bool IsRegistered => UserId is not null;

    /// <summary>True while voice from this user is being received.</summary>
    [JsonIgnore]
    public bool IsTalking => Model?.IsTalking(Session) ?? false;

    /// <summary>True if muted in any way.</summary>
    [JsonIgnore]
    public bool IsMuted => Mute || SelfMute || Suppress;

    /// <summary>True if deafened in any way.</summary>
    [JsonIgnore]
    public bool IsDeafened => Deaf || SelfDeaf;

    internal User WithModel(ServerModel model)
    {
        Model = model;
        return this;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} (session {Session})";
}
