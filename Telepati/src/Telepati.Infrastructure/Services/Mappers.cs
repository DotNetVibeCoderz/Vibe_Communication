using Telepati.Domain;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

/// <summary>
/// Entity → DTO projection. Attachment URLs are resolved through <see cref="IStorageService"/>
/// here rather than stored on the row, so the same message renders correctly no matter which
/// storage provider is configured.
/// </summary>
public static class Mappers
{
    public static UserDto ToDto(this User user, double? distanceKm = null) => new()
    {
        Id = user.Id,
        Username = user.Username,
        DisplayName = user.DisplayName,
        Email = user.Email,
        PhoneNumber = user.PhoneNumber,
        AvatarUrl = user.AvatarUrl,
        About = user.About,
        Role = (int)user.Role,
        Presence = (int)user.Presence,
        LastSeenAt = user.LastSeenAt,
        IsBot = user.IsBot,
        TwoFactorEnabled = user.TwoFactorEnabled,
        DistanceKm = distanceKm
    };

    public static async Task<MessageDto> ToDtoAsync(this Message message, IStorageService storage, string? chatTitleForDirect = null, CancellationToken ct = default)
    {
        var attachments = new List<AttachmentDto>(message.Attachments.Count);
        foreach (var attachment in message.Attachments)
        {
            attachments.Add(new AttachmentDto(
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.SizeBytes,
                await storage.GetUrlAsync(attachment.StorageKey, ct),
                attachment.ThumbnailKey is null ? null : await storage.GetUrlAsync(attachment.ThumbnailKey, ct),
                attachment.Width,
                attachment.Height,
                attachment.DurationSeconds));
        }

        var reactions = message.Reactions
            .GroupBy(r => r.Emoji)
            .Select(g => new ReactionDto(g.Key, g.Count(), g.Select(r => r.UserId).ToList()))
            .ToList();

        return new MessageDto
        {
            Id = message.Id,
            ChatId = message.ChatId,
            SenderId = message.SenderId,
            SenderName = message.Sender?.DisplayName ?? chatTitleForDirect ?? "Unknown",
            SenderAvatarUrl = message.Sender?.AvatarUrl,
            Type = (int)message.Type,
            Content = message.EncryptedContent ?? message.Content,
            IsEncrypted = message.EncryptedContent is not null,
            ReplyToMessageId = message.ReplyToMessageId,
            ReplyToPreview = Truncate(message.ReplyToMessage?.Content, 120),
            ForwardedFromUserId = message.ForwardedFromUserId,
            IsPinned = message.IsPinned,
            IsEdited = message.IsEdited,
            IsBotMessage = message.IsBotMessage,
            DeliveryState = (int)message.DeliveryState,
            CreatedAt = message.CreatedAt,
            Latitude = message.Latitude,
            Longitude = message.Longitude,
            Attachments = attachments,
            Reactions = reactions,
            MentionedUserIds = message.Mentions.Select(m => m.MentionedUserId).ToList()
        };
    }

    public static ChatDto ToDto(this Chat chat, ChatMember? membership, string? resolvedTitle = null, MessageDto? lastMessage = null) => new()
    {
        Id = chat.Id,
        Type = (int)chat.Type,
        Title = resolvedTitle ?? chat.Title ?? string.Empty,
        Description = chat.Description,
        AvatarUrl = chat.AvatarUrl,
        Handle = chat.Handle,
        IsEncrypted = chat.IsEncrypted,
        IsPublic = chat.IsPublic,
        OnlyAdminsCanPost = chat.OnlyAdminsCanPost,
        MemberCount = chat.MemberCount,
        UnreadCount = membership?.UnreadCount ?? 0,
        IsMuted = membership?.IsMuted ?? false,
        IsPinned = membership?.IsPinned ?? false,
        IsArchived = membership?.IsArchived ?? false,
        MyRole = (int)(membership?.Role ?? ChatMemberRole.Member),
        LastMessage = lastMessage,
        LastMessageAt = chat.LastMessageAt
    };

    public static ThemeDto ToDto(this ThemeDefinition theme) => new(
        theme.Id, theme.Name, theme.Description, theme.PrimaryColor, theme.SecondaryColor,
        theme.AccentColor, theme.BackgroundColor, theme.SurfaceColor, theme.TextColor,
        theme.IconSet, theme.IsDark, theme.IsActive, theme.IsSeasonal, theme.ActiveFrom, theme.ActiveTo);

    public static ActivityLogDto ToDto(this ActivityLog log) => new(
        log.Id, log.UserId, log.User?.DisplayName, (int)log.Kind, log.Description, log.IpAddress, log.CreatedAt);

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max] + "…";
}
