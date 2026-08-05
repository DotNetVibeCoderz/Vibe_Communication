using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

// ---------------------------------------------------------------------------
// Status (24-hour stories)
// ---------------------------------------------------------------------------

public interface IStatusService
{
    Task<IReadOnlyList<StatusPostDto>> GetFeedAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<StatusPostDto>> GetMineAsync(Guid userId, CancellationToken ct = default);
    Task<ApiResult<StatusPostDto>> CreateAsync(Guid userId, CreateStatusRequest request, CancellationToken ct = default);
    Task<ApiResult> ViewAsync(Guid userId, Guid statusId, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(Guid userId, Guid statusId, CancellationToken ct = default);
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

public class StatusService(
    TelepatiDbContext db,
    IStorageService storage,
    ISettingsService settings) : IStatusService
{
    public async Task<IReadOnlyList<StatusPostDto>> GetFeedAsync(Guid userId, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        if (!options.Features.EnableStatus) return [];

        // The feed shows contacts only, so a status is never visible to strangers.
        var contactIds = await db.Contacts.AsNoTracking()
            .Where(c => c.OwnerId == userId)
            .Select(c => c.ContactUserId)
            .ToListAsync(ct);

        var blocked = await db.BlockedUsers.AsNoTracking()
            .Where(b => b.OwnerId == userId || b.BlockedUserId == userId)
            .Select(b => b.OwnerId == userId ? b.BlockedUserId : b.OwnerId)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var posts = await db.StatusPosts.AsNoTracking()
            .Where(s => contactIds.Contains(s.UserId) && !blocked.Contains(s.UserId) && s.ExpiresAt > now)
            .Include(s => s.User)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        var seen = await db.StatusViews.AsNoTracking()
            .Where(v => v.ViewerId == userId)
            .Select(v => v.StatusPostId)
            .ToListAsync(ct);

        return await ProjectAsync(posts, seen, ct);
    }

    public async Task<IReadOnlyList<StatusPostDto>> GetMineAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var posts = await db.StatusPosts.AsNoTracking()
            .Where(s => s.UserId == userId && s.ExpiresAt > now)
            .Include(s => s.User)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);

        return await ProjectAsync(posts, [], ct);
    }

    public async Task<ApiResult<StatusPostDto>> CreateAsync(Guid userId, CreateStatusRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        if (!options.Features.EnableStatus) return ApiResult<StatusPostDto>.Fail("Fitur status dimatikan.");

        string? storageKey = null;
        if (request.AttachmentId is not null)
        {
            var attachment = await db.MessageAttachments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == request.AttachmentId, ct);
            if (attachment is null) return ApiResult<StatusPostDto>.Fail("Media tidak ditemukan.");
            storageKey = attachment.StorageKey;
        }

        var post = new StatusPost
        {
            UserId = userId,
            MediaType = (StatusMediaType)request.MediaType,
            Caption = request.Caption,
            StorageKey = storageKey,
            BackgroundColor = request.BackgroundColor,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(options.Limits.StatusRetentionHours)
        };

        db.StatusPosts.Add(post);
        await db.SaveChangesAsync(ct);

        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        post.User = user;

        var projected = await ProjectAsync([post], [], ct);
        return ApiResult<StatusPostDto>.Ok(projected[0]);
    }

    public async Task<ApiResult> ViewAsync(Guid userId, Guid statusId, CancellationToken ct = default)
    {
        var post = await db.StatusPosts.FirstOrDefaultAsync(s => s.Id == statusId, ct);
        if (post is null) return ApiResult.Fail("Status tidak ditemukan.");
        if (post.UserId == userId) return ApiResult.Ok();

        if (await db.StatusViews.AnyAsync(v => v.StatusPostId == statusId && v.ViewerId == userId, ct))
            return ApiResult.Ok();

        db.StatusViews.Add(new StatusView { StatusPostId = statusId, ViewerId = userId });
        post.ViewCount++;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DeleteAsync(Guid userId, Guid statusId, CancellationToken ct = default)
    {
        var post = await db.StatusPosts.FirstOrDefaultAsync(s => s.Id == statusId && s.UserId == userId, ct);
        if (post is null) return ApiResult.Fail("Status tidak ditemukan.");

        post.IsDeleted = true;
        post.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return db.StatusPosts.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }

    private async Task<IReadOnlyList<StatusPostDto>> ProjectAsync(IEnumerable<StatusPost> posts, List<Guid> seen, CancellationToken ct)
    {
        var result = new List<StatusPostDto>();
        foreach (var post in posts)
        {
            result.Add(new StatusPostDto(
                post.Id,
                post.UserId,
                post.User?.DisplayName ?? "Unknown",
                post.User?.AvatarUrl,
                (int)post.MediaType,
                post.Caption,
                post.StorageKey is null ? null : await storage.GetUrlAsync(post.StorageKey, ct),
                post.BackgroundColor,
                post.CreatedAt,
                post.ExpiresAt,
                post.ViewCount,
                seen.Contains(post.Id)));
        }
        return result;
    }
}

// ---------------------------------------------------------------------------
// Calls (WebRTC signalling)
// ---------------------------------------------------------------------------

public interface ICallService
{
    Task<ApiResult<CallSignalDto>> StartAsync(Guid userId, StartCallRequest request, CancellationToken ct = default);
    /// <summary>Relays one SDP offer/answer or ICE candidate to the other party.</summary>
    Task<ApiResult> RelaySignalAsync(Guid userId, CallSignalDto signal, CancellationToken ct = default);
    Task<ApiResult> AnswerAsync(Guid userId, Guid callId, bool accepted, CancellationToken ct = default);
    Task<ApiResult> EndAsync(Guid userId, Guid callId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetIceServersAsync(CancellationToken ct = default);
    Task<int> GetActiveCallCountAsync(CancellationToken ct = default);
}

/// <summary>
/// Media never passes through the server — peers connect directly over WebRTC. All this does
/// is relay the handshake and keep a call log for the admin dashboard.
/// </summary>
public class CallService(
    TelepatiDbContext db,
    ISettingsService settings,
    IChatService chats,
    IRealtimeNotifier notifier) : ICallService
{
    public async Task<ApiResult<CallSignalDto>> StartAsync(Guid userId, StartCallRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        var type = (CallType)request.Type;

        if (type == CallType.Voice && !options.Features.EnableVoiceCall)
            return ApiResult<CallSignalDto>.Fail("Panggilan suara dimatikan.");
        if (type == CallType.Video && !options.Features.EnableVideoCall)
            return ApiResult<CallSignalDto>.Fail("Panggilan video dimatikan.");
        if (!await chats.IsMemberAsync(request.ChatId, userId, ct))
            return ApiResult<CallSignalDto>.Fail("Bukan anggota chat ini.");

        var call = new CallSession
        {
            ChatId = request.ChatId,
            InitiatorId = userId,
            Type = type,
            State = CallState.Ringing,
            Participants = userId.ToString()
        };

        db.CallSessions.Add(call);
        await db.SaveChangesAsync(ct);

        var signal = new CallSignalDto(call.Id, request.ChatId, userId, null, request.Type, "ring", null);
        var recipients = (await chats.GetMemberIdsAsync(request.ChatId, ct)).Where(id => id != userId).ToList();
        await notifier.CallSignalAsync(signal, recipients, ct);

        return ApiResult<CallSignalDto>.Ok(signal);
    }

    public async Task<ApiResult> RelaySignalAsync(Guid userId, CallSignalDto signal, CancellationToken ct = default)
    {
        if (!await chats.IsMemberAsync(signal.ChatId, userId, ct)) return ApiResult.Fail("Bukan anggota chat ini.");

        var recipients = signal.ToUserId is not null
            ? [signal.ToUserId.Value]
            : (await chats.GetMemberIdsAsync(signal.ChatId, ct)).Where(id => id != userId).ToList();

        await notifier.CallSignalAsync(signal with { FromUserId = userId }, recipients, ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> AnswerAsync(Guid userId, Guid callId, bool accepted, CancellationToken ct = default)
    {
        var call = await db.CallSessions.FirstOrDefaultAsync(c => c.Id == callId, ct);
        if (call is null) return ApiResult.Fail("Panggilan tidak ditemukan.");
        if (call.State != CallState.Ringing) return ApiResult.Fail("Panggilan sudah tidak aktif.");

        if (accepted)
        {
            call.State = CallState.Accepted;
            call.AnsweredAt = DateTimeOffset.UtcNow;
            call.Participants = string.Join(',', call.Participants.Split(',', StringSplitOptions.RemoveEmptyEntries).Append(userId.ToString()).Distinct());
        }
        else
        {
            call.State = CallState.Rejected;
            call.EndedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        var signal = new CallSignalDto(call.Id, call.ChatId, userId, call.InitiatorId, (int)call.Type,
            accepted ? "accept" : "reject", null);
        await notifier.CallSignalAsync(signal, [call.InitiatorId], ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> EndAsync(Guid userId, Guid callId, CancellationToken ct = default)
    {
        var call = await db.CallSessions.FirstOrDefaultAsync(c => c.Id == callId, ct);
        if (call is null) return ApiResult.Fail("Panggilan tidak ditemukan.");

        var endedAt = DateTimeOffset.UtcNow;
        // A call that never got answered is logged as missed rather than merely ended.
        call.State = call.AnsweredAt is null ? CallState.Missed : CallState.Ended;
        call.EndedAt = endedAt;
        call.DurationSeconds = call.AnsweredAt is null ? 0 : (int)(endedAt - call.AnsweredAt.Value).TotalSeconds;

        await db.SaveChangesAsync(ct);

        var recipients = (await chats.GetMemberIdsAsync(call.ChatId, ct)).Where(id => id != userId).ToList();
        await notifier.CallSignalAsync(new CallSignalDto(call.Id, call.ChatId, userId, null, (int)call.Type, "hangup", null), recipients, ct);
        return ApiResult.Ok();
    }

    public async Task<IReadOnlyList<string>> GetIceServersAsync(CancellationToken ct = default) =>
        (await settings.GetOptionsAsync(ct)).Features.IceServers;

    public Task<int> GetActiveCallCountAsync(CancellationToken ct = default) =>
        db.CallSessions.AsNoTracking().CountAsync(c => c.State == CallState.Ringing || c.State == CallState.Accepted, ct);
}

// ---------------------------------------------------------------------------
// Broadcast
// ---------------------------------------------------------------------------

public interface IBroadcastService
{
    Task<ApiResult<Guid>> CreateAsync(Guid userId, BroadcastRequest request, CancellationToken ct = default);
    Task<ApiResult> SendAsync(Guid userId, Guid broadcastId, CancellationToken ct = default);
    Task<IReadOnlyList<Broadcast>> GetMineAsync(Guid userId, CancellationToken ct = default);
    Task<DeliveryReportDto?> GetReportAsync(Guid userId, Guid broadcastId, CancellationToken ct = default);
}

/// <summary>
/// A broadcast is not a group: each recipient receives it in their own direct chat and never
/// sees who else got it.
/// </summary>
public class BroadcastService(
    TelepatiDbContext db,
    ISettingsService settings,
    IChatService chats,
    IMessageService messages) : IBroadcastService
{
    public async Task<ApiResult<Guid>> CreateAsync(Guid userId, BroadcastRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        if (!options.Features.EnableBroadcast) return ApiResult<Guid>.Fail("Fitur broadcast dimatikan.");

        var recipients = request.RecipientIds.Distinct().Where(id => id != userId).ToList();
        if (recipients.Count == 0) return ApiResult<Guid>.Fail("Pilih minimal satu penerima.");
        if (recipients.Count > options.Limits.MaxBroadcastRecipients)
            return ApiResult<Guid>.Fail($"Maksimal {options.Limits.MaxBroadcastRecipients} penerima.");

        var broadcast = new Broadcast
        {
            SenderId = userId,
            Name = request.Name,
            Content = request.Content,
            ScheduledAt = request.ScheduledAt,
            RecipientCount = recipients.Count,
            State = BroadcastState.Draft
        };

        db.Broadcasts.Add(broadcast);
        foreach (var recipientId in recipients)
        {
            db.BroadcastRecipients.Add(new BroadcastRecipient { BroadcastId = broadcast.Id, UserId = recipientId });
        }

        await db.SaveChangesAsync(ct);
        return ApiResult<Guid>.Ok(broadcast.Id);
    }

    public async Task<ApiResult> SendAsync(Guid userId, Guid broadcastId, CancellationToken ct = default)
    {
        var broadcast = await db.Broadcasts
            .Include(b => b.Recipients)
            .FirstOrDefaultAsync(b => b.Id == broadcastId && b.SenderId == userId, ct);

        if (broadcast is null) return ApiResult.Fail("Broadcast tidak ditemukan.");
        if (broadcast.State == BroadcastState.Completed) return ApiResult.Fail("Broadcast sudah terkirim.");

        broadcast.State = BroadcastState.Sending;
        await db.SaveChangesAsync(ct);

        foreach (var recipient in broadcast.Recipients)
        {
            try
            {
                var chat = await chats.GetOrCreateDirectChatAsync(userId, recipient.UserId, ct);
                var result = await messages.SendAsync(userId, new SendMessageRequest
                {
                    ChatId = chat.Id,
                    Type = (int)MessageType.Text,
                    Content = broadcast.Content
                }, ct);

                if (result.Success)
                {
                    recipient.State = MessageDeliveryState.Sent;
                    broadcast.SentCount++;
                }
                else
                {
                    recipient.State = MessageDeliveryState.Failed;
                    recipient.FailureReason = result.Error;
                    broadcast.FailedCount++;
                }
            }
            catch (Exception e)
            {
                // One bad recipient must not abort the rest of the run.
                recipient.State = MessageDeliveryState.Failed;
                recipient.FailureReason = e.Message;
                broadcast.FailedCount++;
            }
        }

        broadcast.State = broadcast.FailedCount == broadcast.RecipientCount ? BroadcastState.Failed : BroadcastState.Completed;
        broadcast.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<IReadOnlyList<Broadcast>> GetMineAsync(Guid userId, CancellationToken ct = default) =>
        await db.Broadcasts.AsNoTracking()
            .Where(b => b.SenderId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

    public async Task<DeliveryReportDto?> GetReportAsync(Guid userId, Guid broadcastId, CancellationToken ct = default)
    {
        var broadcast = await db.Broadcasts.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == broadcastId && b.SenderId == userId, ct);
        if (broadcast is null) return null;

        var recipients = await db.BroadcastRecipients.AsNoTracking()
            .Where(r => r.BroadcastId == broadcastId)
            .Join(db.Users.AsNoTracking(), r => r.UserId, u => u.Id, (r, u) => new { r, u.DisplayName })
            .ToListAsync(ct);

        var details = recipients
            .Select(x => new DeliveryDetailDto(x.r.UserId, x.DisplayName, (int)x.r.State, null, null, x.r.FailureReason))
            .ToList();

        return new DeliveryReportDto(
            broadcastId,
            recipients.Count(r => r.r.State == MessageDeliveryState.Sent),
            recipients.Count(r => r.r.State == MessageDeliveryState.Delivered),
            recipients.Count(r => r.r.State == MessageDeliveryState.Read),
            recipients.Count(r => r.r.State == MessageDeliveryState.Failed),
            details);
    }
}
