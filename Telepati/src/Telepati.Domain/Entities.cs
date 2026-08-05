using System.ComponentModel.DataAnnotations;

namespace Telepati.Domain;

/// <summary>
/// Every persisted entity carries a GUID key and creation timestamp. GUID keys keep rows
/// mergeable across shards (see <c>ShardResolver</c>) without a central sequence.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public interface ISoftDelete
{
    bool IsDeleted { get; set; }
    DateTimeOffset? DeletedAt { get; set; }
}

// ---------------------------------------------------------------------------
// Identity & profile
// ---------------------------------------------------------------------------

public class User : BaseEntity, ISoftDelete
{
    [MaxLength(64)] public string Username { get; set; } = string.Empty;
    [MaxLength(256)] public string Email { get; set; } = string.Empty;
    [MaxLength(32)] public string? PhoneNumber { get; set; }
    [MaxLength(128)] public string DisplayName { get; set; } = string.Empty;
    [MaxLength(512)] public string? AvatarUrl { get; set; }
    [MaxLength(512)] public string? About { get; set; }
    [MaxLength(256)] public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.User;
    public UserPresence Presence { get; set; } = UserPresence.Offline;
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsBot { get; set; }

    // Two-factor is optional per user and can be disabled globally from admin settings.
    public bool TwoFactorEnabled { get; set; }
    [MaxLength(128)] public string? TwoFactorSecret { get; set; }

    // Public half of the user's identity key pair, used for optional end-to-end encryption.
    [MaxLength(1024)] public string? PublicKey { get; set; }

    /// <summary>Last known coordinates, only stored when the client granted location permission.</summary>
    public double? LastLatitude { get; set; }
    public double? LastLongitude { get; set; }
    public bool ShareLocationForDiscovery { get; set; }

    [MaxLength(16)] public string PreferredLanguage { get; set; } = "id";
    [MaxLength(32)] public string PreferredTheme { get; set; } = "system";

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<UserSession> Sessions { get; set; } = new List<UserSession>();
    public ICollection<ChatMember> Memberships { get; set; } = new List<ChatMember>();
    public ICollection<Contact> Contacts { get; set; } = new List<Contact>();
}

/// <summary>A logged-in device. Users may hold several at once (multi-device support).</summary>
public class UserSession : BaseEntity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    [MaxLength(128)] public string DeviceName { get; set; } = string.Empty;
    [MaxLength(64)] public string DeviceType { get; set; } = "web";
    [MaxLength(64)] public string? Platform { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    [MaxLength(256)] public string? UserAgent { get; set; }
    [MaxLength(512)] public string RefreshTokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset LastActiveAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsRevoked { get; set; }
    public TransportKind Transport { get; set; } = TransportKind.SignalR;
}

public class Contact : BaseEntity
{
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public Guid ContactUserId { get; set; }
    public User? ContactUser { get; set; }

    /// <summary>Local nickname that overrides the contact's own display name.</summary>
    [MaxLength(128)] public string? Alias { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsMuted { get; set; }
}

public class ContactRequest : BaseEntity
{
    public Guid RequesterId { get; set; }
    public User? Requester { get; set; }
    public Guid TargetId { get; set; }
    public User? Target { get; set; }
    [MaxLength(512)] public string? Message { get; set; }
    public ContactRequestState State { get; set; } = ContactRequestState.Pending;
}

public class BlockedUser : BaseEntity
{
    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }
    public Guid BlockedUserId { get; set; }
    public User? Blocked { get; set; }
    [MaxLength(512)] public string? Reason { get; set; }
}

public class UserReport : BaseEntity
{
    public Guid ReporterId { get; set; }
    public User? Reporter { get; set; }
    public Guid ReportedUserId { get; set; }
    public User? ReportedUser { get; set; }
    public Guid? MessageId { get; set; }
    public ReportReason Reason { get; set; }
    [MaxLength(2000)] public string? Details { get; set; }
    public ReportState State { get; set; } = ReportState.Open;
    [MaxLength(2000)] public string? ResolutionNote { get; set; }
    public Guid? ResolvedById { get; set; }
}

// ---------------------------------------------------------------------------
// Conversations
// ---------------------------------------------------------------------------

/// <summary>
/// One row backs every conversation kind — direct, group, channel, bot and saved messages —
/// so message storage, delivery and search need only one code path. <see cref="ChatType"/>
/// decides which rules apply on top.
/// </summary>
public class Chat : BaseEntity, ISoftDelete
{
    public ChatType Type { get; set; }
    [MaxLength(128)] public string? Title { get; set; }
    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(512)] public string? AvatarUrl { get; set; }
    /// <summary>Public @handle for channels; null for private chats.</summary>
    [MaxLength(64)] public string? Handle { get; set; }

    public Guid? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public bool IsPublic { get; set; }
    public bool IsEncrypted { get; set; }
    /// <summary>When true only admins and moderators can post (channels, announcement groups).</summary>
    public bool OnlyAdminsCanPost { get; set; }
    public int MemberCount { get; set; }

    public Guid? LastMessageId { get; set; }
    public DateTimeOffset? LastMessageAt { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<ChatMember> Members { get; set; } = new List<ChatMember>();
    public ICollection<Message> Messages { get; set; } = new List<Message>();
}

public class ChatMember : BaseEntity
{
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public ChatMemberRole Role { get; set; } = ChatMemberRole.Member;
    public bool IsMuted { get; set; }
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeftAt { get; set; }

    /// <summary>Anchor for unread counts — everything newer than this is unread.</summary>
    public Guid? LastReadMessageId { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    public int UnreadCount { get; set; }
}

public class Message : BaseEntity, ISoftDelete
{
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }
    public Guid SenderId { get; set; }
    public User? Sender { get; set; }

    public MessageType Type { get; set; } = MessageType.Text;
    public string? Content { get; set; }
    /// <summary>Ciphertext when the chat has end-to-end encryption enabled; <see cref="Content"/> stays null.</summary>
    public string? EncryptedContent { get; set; }

    public Guid? ReplyToMessageId { get; set; }
    public Message? ReplyToMessage { get; set; }
    public Guid? ForwardedFromMessageId { get; set; }
    public Guid? ForwardedFromUserId { get; set; }

    public bool IsPinned { get; set; }
    public bool IsEdited { get; set; }
    public DateTimeOffset? EditedAt { get; set; }
    public MessageDeliveryState DeliveryState { get; set; } = MessageDeliveryState.Sent;

    /// <summary>Set for messages produced by a bot so clients can render them differently.</summary>
    public bool IsBotMessage { get; set; }
    public Guid? BroadcastId { get; set; }

    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<MessageAttachment> Attachments { get; set; } = new List<MessageAttachment>();
    public ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();
    public ICollection<MessageReceipt> Receipts { get; set; } = new List<MessageReceipt>();
    public ICollection<MessageMention> Mentions { get; set; } = new List<MessageMention>();
}

public class MessageAttachment : BaseEntity
{
    /// <summary>
    /// Null while the file is uploaded but not yet attached to a message. The upload endpoint
    /// creates the row first so a client can attach several files to one send.
    /// </summary>
    public Guid? MessageId { get; set; }
    public Message? Message { get; set; }

    [MaxLength(256)] public string FileName { get; set; } = string.Empty;
    [MaxLength(128)] public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    /// <summary>Provider-relative object key; resolve through <c>IStorageService</c> for a URL.</summary>
    [MaxLength(1024)] public string StorageKey { get; set; } = string.Empty;
    [MaxLength(1024)] public string? ThumbnailKey { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationSeconds { get; set; }
    [MaxLength(64)] public string? Checksum { get; set; }
}

public class MessageReaction : BaseEntity
{
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public Guid UserId { get; set; }
    [MaxLength(32)] public string Emoji { get; set; } = string.Empty;
}

/// <summary>Per-recipient delivery state; the source for message delivery reports.</summary>
public class MessageReceipt : BaseEntity
{
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public Guid UserId { get; set; }
    public MessageDeliveryState State { get; set; } = MessageDeliveryState.Sent;
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    [MaxLength(512)] public string? FailureReason { get; set; }
}

public class MessageMention : BaseEntity
{
    public Guid MessageId { get; set; }
    public Message? Message { get; set; }
    public Guid MentionedUserId { get; set; }
    public bool IsNotified { get; set; }
}

// ---------------------------------------------------------------------------
// Status, calls, channels, broadcasts
// ---------------------------------------------------------------------------

public class StatusPost : BaseEntity, ISoftDelete
{
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public StatusMediaType MediaType { get; set; }
    [MaxLength(2000)] public string? Caption { get; set; }
    [MaxLength(1024)] public string? StorageKey { get; set; }
    [MaxLength(32)] public string? BackgroundColor { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddHours(24);
    public int ViewCount { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public ICollection<StatusView> Views { get; set; } = new List<StatusView>();
}

public class StatusView : BaseEntity
{
    public Guid StatusPostId { get; set; }
    public StatusPost? StatusPost { get; set; }
    public Guid ViewerId { get; set; }
}

public class CallSession : BaseEntity
{
    public Guid ChatId { get; set; }
    public Chat? Chat { get; set; }
    public Guid InitiatorId { get; set; }
    public CallType Type { get; set; }
    public CallState State { get; set; } = CallState.Ringing;
    public DateTimeOffset? AnsweredAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int DurationSeconds { get; set; }
    /// <summary>Comma-separated user ids that joined; group calls may hold several.</summary>
    [MaxLength(2000)] public string Participants { get; set; } = string.Empty;
}

public class Broadcast : BaseEntity
{
    public Guid SenderId { get; set; }
    public User? Sender { get; set; }
    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public BroadcastState State { get; set; } = BroadcastState.Draft;
    public int RecipientCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<BroadcastRecipient> Recipients { get; set; } = new List<BroadcastRecipient>();
}

public class BroadcastRecipient : BaseEntity
{
    public Guid BroadcastId { get; set; }
    public Broadcast? Broadcast { get; set; }
    public Guid UserId { get; set; }
    public MessageDeliveryState State { get; set; } = MessageDeliveryState.Pending;
    [MaxLength(512)] public string? FailureReason { get; set; }
}

// ---------------------------------------------------------------------------
// Stickers
// ---------------------------------------------------------------------------

public class StickerPack : BaseEntity
{
    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(1024)] public string? CoverKey { get; set; }
    public bool IsBuiltIn { get; set; }
    public Guid? CreatedById { get; set; }
    public ICollection<Sticker> Stickers { get; set; } = new List<Sticker>();
}

public class Sticker : BaseEntity
{
    public Guid StickerPackId { get; set; }
    public StickerPack? Pack { get; set; }
    [MaxLength(1024)] public string StorageKey { get; set; } = string.Empty;
    [MaxLength(32)] public string? Emoji { get; set; }
    [MaxLength(128)] public string? Keywords { get; set; }
}

// ---------------------------------------------------------------------------
// Bot
// ---------------------------------------------------------------------------

/// <summary>
/// One conversation with the bot. Direct chats key on <see cref="UserId"/>, group chats key
/// on <see cref="ChatId"/>, which is what isolates the histories from one another.
/// </summary>
public class BotSession : BaseEntity
{
    public Guid? ChatId { get; set; }
    public Guid? UserId { get; set; }
    [MaxLength(64)] public string BotHandle { get; set; } = "bacot";

    /// <summary>Null means "use the persona from configuration"; set by <c>#newpersona</c>.</summary>
    public string? SystemPromptOverride { get; set; }
    public int TurnCount { get; set; }
    public int EstimatedTokens { get; set; }
    /// <summary>Rolling summary written by auto-compaction when the context window fills up.</summary>
    public string? CompactedSummary { get; set; }
    public DateTimeOffset LastInteractionAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<BotMessage> Messages { get; set; } = new List<BotMessage>();
}

public class BotMessage : BaseEntity
{
    public Guid BotSessionId { get; set; }
    public BotSession? Session { get; set; }
    /// <summary>system | user | assistant | tool</summary>
    [MaxLength(16)] public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public int TokenCount { get; set; }
    /// <summary>Set to false by auto-compaction once the turn is folded into the summary.</summary>
    public bool IsActive { get; set; } = true;
    [MaxLength(128)] public string? FunctionName { get; set; }
}

// ---------------------------------------------------------------------------
// Administration
// ---------------------------------------------------------------------------

public class ActivityLog : BaseEntity
{
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public ActivityKind Kind { get; set; }
    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    [MaxLength(256)] public string? UserAgent { get; set; }
    [MaxLength(64)] public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
}

/// <summary>
/// Runtime override for any key in appsettings. Anything written here wins over the JSON file,
/// which is what lets the admin app change configuration without a redeploy.
/// </summary>
public class AppSetting : BaseEntity
{
    [MaxLength(256)] public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    [MaxLength(64)] public string Category { get; set; } = "General";
    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(32)] public string ValueType { get; set; } = "string";
    public bool IsSecret { get; set; }
}

/// <summary>
/// Seasonal / festive skin defined as master data: pick colours and an icon set, set a date
/// window, and the clients pick it up. Admins can also force one active out of season.
/// </summary>
public class ThemeDefinition : BaseEntity
{
    [MaxLength(64)] public string Name { get; set; } = string.Empty;
    [MaxLength(256)] public string? Description { get; set; }
    [MaxLength(32)] public string PrimaryColor { get; set; } = "#6C5CE7";
    [MaxLength(32)] public string SecondaryColor { get; set; } = "#00CEC9";
    [MaxLength(32)] public string AccentColor { get; set; } = "#FD79A8";
    [MaxLength(32)] public string BackgroundColor { get; set; } = "#FFFFFF";
    [MaxLength(32)] public string SurfaceColor { get; set; } = "#F5F6FA";
    [MaxLength(32)] public string TextColor { get; set; } = "#2D3436";
    /// <summary>Emoji or icon-font key shown as the seasonal accent (e.g. 🌙 for Lebaran).</summary>
    [MaxLength(64)] public string? IconSet { get; set; }
    [MaxLength(512)] public string? BackgroundPatternUrl { get; set; }
    public bool IsDark { get; set; }
    public bool IsActive { get; set; }
    public bool IsSeasonal { get; set; }
    public DateTimeOffset? ActiveFrom { get; set; }
    public DateTimeOffset? ActiveTo { get; set; }
}
