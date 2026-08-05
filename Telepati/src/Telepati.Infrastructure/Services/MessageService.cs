using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IMessageService
{
    Task<PagedResult<MessageDto>> GetMessagesAsync(Guid userId, Guid chatId, int page, int pageSize, Guid? beforeMessageId, CancellationToken ct = default);
    Task<ApiResult<MessageDto>> SendAsync(Guid senderId, SendMessageRequest request, CancellationToken ct = default);
    Task<ApiResult<MessageDto>> EditAsync(Guid userId, EditMessageRequest request, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(Guid userId, Guid messageId, CancellationToken ct = default);
    Task<ApiResult> PinAsync(Guid userId, Guid messageId, bool pinned, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetPinnedAsync(Guid userId, Guid chatId, CancellationToken ct = default);
    Task<ApiResult> ReactAsync(Guid userId, ReactionRequest request, CancellationToken ct = default);
    Task<ApiResult<MessageDto>> ForwardAsync(Guid userId, Guid messageId, Guid targetChatId, CancellationToken ct = default);
    Task<ApiResult> MarkReadAsync(Guid userId, ReadReceiptRequest request, CancellationToken ct = default);
    Task<ApiResult> SetTypingAsync(Guid userId, Guid chatId, bool isTyping, CancellationToken ct = default);
    Task<PagedResult<MessageDto>> SearchAsync(Guid userId, string query, Guid? chatId, int page, int pageSize, CancellationToken ct = default);
    Task<DeliveryReportDto?> GetDeliveryReportAsync(Guid userId, Guid messageId, CancellationToken ct = default);
    Task<IReadOnlyList<MessageDto>> GetMentionsAsync(Guid userId, int take, CancellationToken ct = default);
}

public class MessageService(
    TelepatiDbContext db,
    ICacheService cache,
    IStorageService storage,
    ISettingsService settings,
    IChatService chats,
    IActivityLogger activity,
    IRealtimeNotifier notifier) : IMessageService
{
    public async Task<PagedResult<MessageDto>> GetMessagesAsync(Guid userId, Guid chatId, int page, int pageSize, Guid? beforeMessageId, CancellationToken ct = default)
    {
        if (!await chats.IsMemberAsync(chatId, userId, ct))
            return new PagedResult<MessageDto>([], 0, page, pageSize);

        var query = db.Messages.AsNoTracking().Where(m => m.ChatId == chatId);

        // Cursor paging keeps a long scrollback stable while new messages keep arriving.
        if (beforeMessageId is not null)
        {
            var anchor = await db.Messages.AsNoTracking()
                .Where(m => m.Id == beforeMessageId)
                .Select(m => m.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (anchor != default) query = query.Where(m => m.CreatedAt < anchor);
        }

        var total = await db.Messages.AsNoTracking().CountAsync(m => m.ChatId == chatId, ct);

        var messages = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip(beforeMessageId is null ? (page - 1) * pageSize : 0)
            .Take(pageSize)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .Include(m => m.ReplyToMessage)
            .AsSplitQuery()
            .ToListAsync(ct);

        var dtos = new List<MessageDto>(messages.Count);
        foreach (var message in messages.OrderBy(m => m.CreatedAt))
        {
            dtos.Add(await message.ToDtoAsync(storage, ct: ct));
        }

        return new PagedResult<MessageDto>(dtos, total, page, pageSize);
    }

    public async Task<ApiResult<MessageDto>> SendAsync(Guid senderId, SendMessageRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);

        var membership = await db.ChatMembers
            .Include(m => m.Chat)
            .FirstOrDefaultAsync(m => m.ChatId == request.ChatId && m.UserId == senderId && m.LeftAt == null, ct);

        if (membership?.Chat is null) return ApiResult<MessageDto>.Fail("Bukan anggota chat ini.");

        var chat = membership.Chat;
        if (chat.OnlyAdminsCanPost && membership.Role < ChatMemberRole.Moderator)
            return ApiResult<MessageDto>.Fail("Hanya admin yang boleh mengirim pesan di sini.");

        if (request.Content is { Length: > 0 } && request.Content.Length > options.Limits.MaxMessageLength)
            return ApiResult<MessageDto>.Fail($"Pesan melebihi {options.Limits.MaxMessageLength} karakter.");

        // A blocked recipient must not receive anything, in either direction.
        if (chat.Type == ChatType.Direct)
        {
            var otherId = await db.ChatMembers.AsNoTracking()
                .Where(m => m.ChatId == chat.Id && m.UserId != senderId)
                .Select(m => m.UserId).FirstOrDefaultAsync(ct);

            if (otherId != Guid.Empty && await IsBlockedEitherWayAsync(senderId, otherId, ct))
                return ApiResult<MessageDto>.Fail("Pesan tidak bisa dikirim ke pengguna ini.");
        }

        var message = new Message
        {
            ChatId = chat.Id,
            SenderId = senderId,
            Type = (MessageType)request.Type,
            ReplyToMessageId = request.ReplyToMessageId,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            DeliveryState = MessageDeliveryState.Sent
        };

        // In an encrypted chat the server stores only what the client encrypted; it never
        // holds the plaintext, so search and previews are unavailable there by design.
        if (chat.IsEncrypted) message.EncryptedContent = request.Content;
        else message.Content = request.Content;

        if (request.ForwardedFromMessageId is not null)
        {
            var source = await db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.ForwardedFromMessageId, ct);
            if (source is not null)
            {
                message.ForwardedFromMessageId = source.Id;
                message.ForwardedFromUserId = source.SenderId;
            }
        }

        db.Messages.Add(message);

        foreach (var attachmentId in request.AttachmentIds)
        {
            var attachment = await db.MessageAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
            if (attachment is not null) attachment.MessageId = message.Id;
        }

        foreach (var mentionedId in request.MentionedUserIds.Distinct())
        {
            db.MessageMentions.Add(new MessageMention { MessageId = message.Id, MentionedUserId = mentionedId });
        }

        var memberIds = await chats.GetMemberIdsAsync(chat.Id, ct);

        // One receipt row per recipient is what makes delivery reports possible later.
        foreach (var memberId in memberIds.Where(id => id != senderId))
        {
            db.MessageReceipts.Add(new MessageReceipt { MessageId = message.Id, UserId = memberId });
        }

        chat.LastMessageId = message.Id;
        chat.LastMessageAt = message.CreatedAt;

        await db.SaveChangesAsync(ct);

        // Bumped in SQL after the insert commits, so the counter can never point at a message
        // that failed to save. MarkReadAsync clears it the same way.
        await db.ChatMembers
            .Where(m => m.ChatId == chat.Id && m.UserId != senderId && m.LeftAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.UnreadCount, m => m.UnreadCount + 1), ct);

        foreach (var memberId in memberIds) await cache.RemoveAsync(CacheKeys.ChatList(memberId), ct);

        var dto = await LoadDtoAsync(message.Id, ct);
        if (dto is null) return ApiResult<MessageDto>.Fail("Gagal memuat pesan yang baru dikirim.");

        await notifier.MessageSentAsync(dto, memberIds, ct);
        await activity.LogAsync(ActivityKind.MessageSent, senderId, null, entityType: nameof(Message), entityId: message.Id, ct: ct);

        return ApiResult<MessageDto>.Ok(dto);
    }

    public async Task<ApiResult<MessageDto>> EditAsync(Guid userId, EditMessageRequest request, CancellationToken ct = default)
    {
        var message = await db.Messages.Include(m => m.Chat).FirstOrDefaultAsync(m => m.Id == request.MessageId, ct);
        if (message is null) return ApiResult<MessageDto>.Fail("Pesan tidak ditemukan.");
        if (message.SenderId != userId) return ApiResult<MessageDto>.Fail("Hanya pengirim yang bisa mengubah pesan.");

        if (message.Chat?.IsEncrypted == true) message.EncryptedContent = request.Content;
        else message.Content = request.Content;

        message.IsEdited = true;
        message.EditedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var dto = await LoadDtoAsync(message.Id, ct);
        if (dto is null) return ApiResult<MessageDto>.Fail("Gagal memuat pesan.");

        await notifier.MessageEditedAsync(dto, await chats.GetMemberIdsAsync(message.ChatId, ct), ct);
        return ApiResult<MessageDto>.Ok(dto);
    }

    public async Task<ApiResult> DeleteAsync(Guid userId, Guid messageId, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return ApiResult.Fail("Pesan tidak ditemukan.");

        // Senders can always delete their own; chat moderators can delete anyone's.
        if (message.SenderId != userId)
        {
            var membership = await db.ChatMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ChatId == message.ChatId && m.UserId == userId, ct);
            if (membership is null || membership.Role < ChatMemberRole.Moderator)
                return ApiResult.Fail("Hak akses tidak cukup.");
        }

        message.IsDeleted = true;
        message.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await notifier.MessageDeletedAsync(message.ChatId, messageId, await chats.GetMemberIdsAsync(message.ChatId, ct), ct);
        await activity.LogAsync(ActivityKind.MessageDeleted, userId, null, entityType: nameof(Message), entityId: messageId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> PinAsync(Guid userId, Guid messageId, bool pinned, CancellationToken ct = default)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null) return ApiResult.Fail("Pesan tidak ditemukan.");

        var membership = await db.ChatMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ChatId == message.ChatId && m.UserId == userId, ct);
        if (membership is null) return ApiResult.Fail("Bukan anggota chat ini.");

        var chat = await db.Chats.AsNoTracking().FirstAsync(c => c.Id == message.ChatId, ct);
        if (chat.Type != ChatType.Direct && membership.Role < ChatMemberRole.Moderator)
            return ApiResult.Fail("Hanya admin/moderator yang bisa menyematkan pesan.");

        if (pinned)
        {
            var options = await settings.GetOptionsAsync(ct);
            var pinnedCount = await db.Messages.CountAsync(m => m.ChatId == message.ChatId && m.IsPinned, ct);
            if (pinnedCount >= options.Limits.MaxPinnedMessages)
                return ApiResult.Fail($"Maksimal {options.Limits.MaxPinnedMessages} pesan tersemat.");
        }

        message.IsPinned = pinned;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<IReadOnlyList<MessageDto>> GetPinnedAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        if (!await chats.IsMemberAsync(chatId, userId, ct)) return [];

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.IsPinned)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .AsSplitQuery()
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

        var result = new List<MessageDto>(messages.Count);
        foreach (var message in messages) result.Add(await message.ToDtoAsync(storage, ct: ct));
        return result;
    }

    public async Task<ApiResult> ReactAsync(Guid userId, ReactionRequest request, CancellationToken ct = default)
    {
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.MessageId, ct);
        if (message is null) return ApiResult.Fail("Pesan tidak ditemukan.");
        if (!await chats.IsMemberAsync(message.ChatId, userId, ct)) return ApiResult.Fail("Bukan anggota chat ini.");

        var existing = await db.MessageReactions
            .FirstOrDefaultAsync(r => r.MessageId == request.MessageId && r.UserId == userId && r.Emoji == request.Emoji, ct);

        // Reacting with the same emoji twice is a toggle, matching how every messenger behaves.
        if (existing is not null) db.MessageReactions.Remove(existing);
        else db.MessageReactions.Add(new MessageReaction { MessageId = request.MessageId, UserId = userId, Emoji = request.Emoji });

        await db.SaveChangesAsync(ct);

        var dto = await LoadDtoAsync(request.MessageId, ct);
        if (dto is not null)
        {
            await notifier.MessageEditedAsync(dto, await chats.GetMemberIdsAsync(message.ChatId, ct), ct);
        }
        return ApiResult.Ok();
    }

    public async Task<ApiResult<MessageDto>> ForwardAsync(Guid userId, Guid messageId, Guid targetChatId, CancellationToken ct = default)
    {
        var source = await db.Messages.AsNoTracking()
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (source is null) return ApiResult<MessageDto>.Fail("Pesan tidak ditemukan.");
        if (!await chats.IsMemberAsync(source.ChatId, userId, ct)) return ApiResult<MessageDto>.Fail("Bukan anggota chat asal.");

        var request = new SendMessageRequest
        {
            ChatId = targetChatId,
            Type = (int)source.Type,
            Content = source.Content,
            ForwardedFromMessageId = source.Id
        };

        return await SendAsync(userId, request, ct);
    }

    public async Task<ApiResult> MarkReadAsync(Guid userId, ReadReceiptRequest request, CancellationToken ct = default)
    {
        if (!await db.ChatMembers.AsNoTracking()
                .AnyAsync(m => m.ChatId == request.ChatId && m.UserId == userId, ct))
        {
            return ApiResult.Fail("Bukan anggota chat ini.");
        }

        var readAt = DateTimeOffset.UtcNow;

        // The unread counter is written with ExecuteUpdate, matching how SendAsync increments
        // it. Mixing the two — set-based there, tracked entity here — lets a stale tracked
        // instance overwrite a counter the same context already bumped in SQL.
        await db.ChatMembers
            .Where(m => m.ChatId == request.ChatId && m.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.LastReadMessageId, request.LastReadMessageId)
                .SetProperty(m => m.LastReadAt, readAt)
                .SetProperty(m => m.UnreadCount, 0), ct);

        var anchor = await db.Messages.AsNoTracking()
            .Where(m => m.Id == request.LastReadMessageId)
            .Select(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Marking one message read implicitly reads everything above it in the same chat.
        await db.MessageReceipts
            .Where(r => r.UserId == userId
                        && r.State != MessageDeliveryState.Read
                        && db.Messages.Any(m => m.Id == r.MessageId && m.ChatId == request.ChatId && m.CreatedAt <= anchor))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, MessageDeliveryState.Read)
                .SetProperty(r => r.ReadAt, readAt), ct);

        await cache.RemoveAsync(CacheKeys.ChatList(userId), ct);

        await notifier.ReceiptAsync(request.ChatId, request.LastReadMessageId, userId,
            (int)MessageDeliveryState.Read, await chats.GetMemberIdsAsync(request.ChatId, ct), ct);

        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetTypingAsync(Guid userId, Guid chatId, bool isTyping, CancellationToken ct = default)
    {
        if (!await chats.IsMemberAsync(chatId, userId, ct)) return ApiResult.Fail("Bukan anggota chat ini.");

        var name = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "Seseorang";

        var recipients = (await chats.GetMemberIdsAsync(chatId, ct)).Where(id => id != userId).ToList();
        await notifier.TypingAsync(new TypingNotification(chatId, userId, name, isTyping), recipients, ct);
        return ApiResult.Ok();
    }

    public async Task<PagedResult<MessageDto>> SearchAsync(Guid userId, string query, Guid? chatId, int page, int pageSize, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new PagedResult<MessageDto>([], 0, page, pageSize);

        var term = query.Trim().ToLowerInvariant();
        var memberChatIds = await db.ChatMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.LeftAt == null)
            .Select(m => m.ChatId)
            .ToListAsync(ct);

        // Encrypted chats hold ciphertext only, so they are excluded rather than returning noise.
        var searchable = db.Messages.AsNoTracking()
            .Where(m => memberChatIds.Contains(m.ChatId) && m.Content != null && m.Content.ToLower().Contains(term));

        if (chatId is not null) searchable = searchable.Where(m => m.ChatId == chatId);

        var total = await searchable.CountAsync(ct);
        var messages = await searchable
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .AsSplitQuery()
            .ToListAsync(ct);

        var dtos = new List<MessageDto>(messages.Count);
        foreach (var message in messages) dtos.Add(await message.ToDtoAsync(storage, ct: ct));
        return new PagedResult<MessageDto>(dtos, total, page, pageSize);
    }

    public async Task<DeliveryReportDto?> GetDeliveryReportAsync(Guid userId, Guid messageId, CancellationToken ct = default)
    {
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.SenderId != userId) return null;

        var receipts = await db.MessageReceipts.AsNoTracking()
            .Where(r => r.MessageId == messageId)
            .Join(db.Users.AsNoTracking(), r => r.UserId, u => u.Id, (r, u) => new { r, u.DisplayName })
            .ToListAsync(ct);

        var details = receipts
            .Select(x => new DeliveryDetailDto(x.r.UserId, x.DisplayName, (int)x.r.State, x.r.DeliveredAt, x.r.ReadAt, x.r.FailureReason))
            .ToList();

        return new DeliveryReportDto(
            messageId,
            receipts.Count(r => r.r.State == MessageDeliveryState.Sent),
            receipts.Count(r => r.r.State == MessageDeliveryState.Delivered),
            receipts.Count(r => r.r.State == MessageDeliveryState.Read),
            receipts.Count(r => r.r.State == MessageDeliveryState.Failed),
            details);
    }

    public async Task<IReadOnlyList<MessageDto>> GetMentionsAsync(Guid userId, int take, CancellationToken ct = default)
    {
        var messages = await db.MessageMentions.AsNoTracking()
            .Where(m => m.MentionedUserId == userId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => m.Message!)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .AsSplitQuery()
            .ToListAsync(ct);

        var result = new List<MessageDto>(messages.Count);
        foreach (var message in messages) result.Add(await message.ToDtoAsync(storage, ct: ct));
        return result;
    }

    // -- helpers --------------------------------------------------------------

    private async Task<MessageDto?> LoadDtoAsync(Guid messageId, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking()
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .Include(m => m.ReplyToMessage)
            .AsSplitQuery()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        return message is null ? null : await message.ToDtoAsync(storage, ct: ct);
    }

    private Task<bool> IsBlockedEitherWayAsync(Guid a, Guid b, CancellationToken ct) =>
        db.BlockedUsers.AsNoTracking().AnyAsync(x =>
            (x.OwnerId == a && x.BlockedUserId == b) || (x.OwnerId == b && x.BlockedUserId == a), ct);
}
