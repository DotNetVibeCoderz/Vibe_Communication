using Google.Protobuf.WellKnownTypes;
using Telepati.Shared.Contracts;
using Telepati.Shared.Grpc;

namespace Telepati.Server.Realtime;

/// <summary>
/// DTO ↔ protobuf conversion. Protobuf has no nullable scalars, so a missing GUID travels as an
/// empty string and a missing number as zero; these helpers keep that convention in one place.
/// </summary>
public static class GrpcMappers
{
    public static string Str(this Guid? value) => value?.ToString() ?? string.Empty;
    public static string Str(this Guid value) => value.ToString();
    public static string OrEmpty(this string? value) => value ?? string.Empty;

    public static Guid? ToGuidOrNull(this string value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;

    public static Guid ToGuid(this string value) =>
        Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty;

    public static Timestamp? ToTimestamp(this DateTimeOffset? value) =>
        value is null ? null : Timestamp.FromDateTimeOffset(value.Value);

    public static Timestamp ToTimestamp(this DateTimeOffset value) => Timestamp.FromDateTimeOffset(value);

    public static GrpcUser ToGrpc(this UserDto user) => new()
    {
        Id = user.Id.Str(),
        Username = user.Username,
        DisplayName = user.DisplayName,
        Email = user.Email,
        PhoneNumber = user.PhoneNumber.OrEmpty(),
        AvatarUrl = user.AvatarUrl.OrEmpty(),
        About = user.About.OrEmpty(),
        Role = user.Role,
        Presence = user.Presence,
        LastSeenAt = user.LastSeenAt.ToTimestamp(),
        IsBot = user.IsBot,
        DistanceKm = user.DistanceKm ?? 0
    };

    public static GrpcMessage ToGrpc(this MessageDto message)
    {
        var result = new GrpcMessage
        {
            Id = message.Id.Str(),
            ChatId = message.ChatId.Str(),
            SenderId = message.SenderId.Str(),
            SenderName = message.SenderName,
            SenderAvatarUrl = message.SenderAvatarUrl.OrEmpty(),
            Type = message.Type,
            Content = message.Content.OrEmpty(),
            IsEncrypted = message.IsEncrypted,
            ReplyToMessageId = message.ReplyToMessageId.Str(),
            ReplyToPreview = message.ReplyToPreview.OrEmpty(),
            ForwardedFromUserId = message.ForwardedFromUserId.Str(),
            ForwardedFromName = message.ForwardedFromName.OrEmpty(),
            IsPinned = message.IsPinned,
            IsEdited = message.IsEdited,
            IsBotMessage = message.IsBotMessage,
            DeliveryState = message.DeliveryState,
            CreatedAt = message.CreatedAt.ToTimestamp(),
            Latitude = message.Latitude ?? 0,
            Longitude = message.Longitude ?? 0
        };

        foreach (var attachment in message.Attachments)
        {
            result.Attachments.Add(new GrpcAttachment
            {
                Id = attachment.Id.Str(),
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                SizeBytes = attachment.SizeBytes,
                Url = attachment.Url,
                ThumbnailUrl = attachment.ThumbnailUrl.OrEmpty(),
                Width = attachment.Width ?? 0,
                Height = attachment.Height ?? 0,
                DurationSeconds = attachment.DurationSeconds ?? 0
            });
        }

        foreach (var reaction in message.Reactions)
        {
            var item = new GrpcReaction { Emoji = reaction.Emoji, Count = reaction.Count };
            item.UserIds.AddRange(reaction.UserIds.Select(id => id.Str()));
            result.Reactions.Add(item);
        }

        result.MentionedUserIds.AddRange(message.MentionedUserIds.Select(id => id.Str()));
        return result;
    }

    public static GrpcChat ToGrpc(this ChatDto chat) => new()
    {
        Id = chat.Id.Str(),
        Type = chat.Type,
        Title = chat.Title,
        Description = chat.Description.OrEmpty(),
        AvatarUrl = chat.AvatarUrl.OrEmpty(),
        Handle = chat.Handle.OrEmpty(),
        IsEncrypted = chat.IsEncrypted,
        IsPublic = chat.IsPublic,
        OnlyAdminsCanPost = chat.OnlyAdminsCanPost,
        MemberCount = chat.MemberCount,
        UnreadCount = chat.UnreadCount,
        IsMuted = chat.IsMuted,
        IsPinned = chat.IsPinned,
        IsArchived = chat.IsArchived,
        MyRole = chat.MyRole,
        LastMessage = chat.LastMessage?.ToGrpc(),
        LastMessageAt = chat.LastMessageAt.ToTimestamp()
    };

    public static SendMessageRequest ToDto(this GrpcSendMessageRequest request) => new()
    {
        ChatId = request.ChatId.ToGuid(),
        Type = request.Type,
        Content = request.Content,
        ReplyToMessageId = request.ReplyToMessageId.ToGuidOrNull(),
        ForwardedFromMessageId = request.ForwardedFromMessageId.ToGuidOrNull(),
        AttachmentIds = request.AttachmentIds.Select(id => id.ToGuid()).Where(id => id != Guid.Empty).ToList(),
        MentionedUserIds = request.MentionedUserIds.Select(id => id.ToGuid()).Where(id => id != Guid.Empty).ToList(),
        Latitude = request.Latitude == 0 ? null : request.Latitude,
        Longitude = request.Longitude == 0 ? null : request.Longitude,
        ClientMessageId = request.ClientMessageId
    };

    public static CallSignalDto ToDto(this CallSignal signal) => new(
        signal.CallId.ToGuid(),
        signal.ChatId.ToGuid(),
        signal.FromUserId.ToGuid(),
        signal.ToUserId.ToGuidOrNull(),
        signal.Type,
        signal.SignalKind,
        signal.Payload);
}

/// <summary>Builds the single realtime envelope the gRPC stream carries.</summary>
public static class GrpcEventFactory
{
    public static RealtimeEvent Message(string kind, MessageDto message) =>
        new() { Kind = kind, Message = message.ToGrpc() };

    public static RealtimeEvent Deleted(Guid messageId) =>
        new() { Kind = "message_deleted", DeletedMessageId = messageId.Str() };

    public static RealtimeEvent Receipt(Guid chatId, Guid messageId, Guid userId, int state) =>
        new()
        {
            Kind = "receipt",
            Receipt = new ReceiptEvent
            {
                ChatId = chatId.Str(),
                MessageId = messageId.Str(),
                UserId = userId.Str(),
                State = state
            }
        };

    public static RealtimeEvent Typing(TypingNotification notification) =>
        new()
        {
            Kind = "typing",
            Typing = new TypingEvent
            {
                ChatId = notification.ChatId.Str(),
                UserId = notification.UserId.Str(),
                UserName = notification.UserName,
                IsTyping = notification.IsTyping
            }
        };

    public static RealtimeEvent Presence(PresenceNotification notification) =>
        new()
        {
            Kind = "presence",
            Presence = new PresenceEvent
            {
                UserId = notification.UserId.Str(),
                Presence = notification.Presence,
                LastSeenAt = notification.LastSeenAt.ToTimestamp()
            }
        };

    public static RealtimeEvent Call(CallSignalDto signal) =>
        new()
        {
            Kind = "call",
            Call = new CallSignal
            {
                CallId = signal.CallId.Str(),
                ChatId = signal.ChatId.Str(),
                FromUserId = signal.FromUserId.Str(),
                ToUserId = signal.ToUserId.Str(),
                Type = signal.Type,
                SignalKind = signal.SignalKind,
                Payload = signal.Payload.OrEmpty()
            }
        };

    public static RealtimeEvent Chat(ChatDto chat) =>
        new() { Kind = "chat_updated", Chat = chat.ToGrpc() };
}
