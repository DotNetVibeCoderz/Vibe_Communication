using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IAttachmentService
{
    Task<ApiResult<UploadResultDto>> UploadAsync(Guid userId, Stream content, string fileName, string contentType,
        string folder = StorageFolders.Attachments, CancellationToken ct = default);
    Task<(Stream Content, string ContentType, string FileName)?> DownloadAsync(Guid attachmentId, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(Guid userId, Guid attachmentId, CancellationToken ct = default);
    /// <summary>Removes rows whose upload was never attached to a message.</summary>
    Task<int> PurgeOrphansAsync(TimeSpan olderThan, CancellationToken ct = default);
}

public class AttachmentService(
    TelepatiDbContext db,
    IStorageService storage,
    ISettingsService settings,
    IActivityLogger activity) : IAttachmentService
{
    public async Task<ApiResult<UploadResultDto>> UploadAsync(Guid userId, Stream content, string fileName,
        string contentType, string folder = StorageFolders.Attachments, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);

        if (content.CanSeek && content.Length > options.Limits.MaxUploadBytes)
        {
            return ApiResult<UploadResultDto>.Fail($"Ukuran file melebihi {options.Limits.MaxUploadBytes / (1024 * 1024)} MB.");
        }

        var key = await storage.UploadAsync(content, fileName, contentType, folder, ct);

        // The row is created without a MessageId; SendAsync claims it when the message is sent.
        var attachment = new MessageAttachment
        {
            MessageId = null,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = content.CanSeek ? content.Length : 0,
            StorageKey = key
        };

        db.MessageAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.FileUploaded, userId, fileName, entityType: nameof(MessageAttachment), entityId: attachment.Id, ct: ct);

        var url = await storage.GetUrlAsync(key, ct);
        return ApiResult<UploadResultDto>.Ok(new UploadResultDto(
            attachment.Id, fileName, contentType, attachment.SizeBytes, url, null));
    }

    public async Task<(Stream Content, string ContentType, string FileName)?> DownloadAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.MessageAttachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null) return null;

        var stream = await storage.DownloadAsync(attachment.StorageKey, ct);
        return stream is null ? null : (stream, attachment.ContentType, attachment.FileName);
    }

    public async Task<ApiResult> DeleteAsync(Guid userId, Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.MessageAttachments
            .Include(a => a.Message)
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct);

        if (attachment is null) return ApiResult.Fail("Lampiran tidak ditemukan.");
        if (attachment.Message is not null && attachment.Message.SenderId != userId)
            return ApiResult.Fail("Hak akses tidak cukup.");

        await storage.DeleteAsync(attachment.StorageKey, ct);
        if (attachment.ThumbnailKey is not null) await storage.DeleteAsync(attachment.ThumbnailKey, ct);

        db.MessageAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<int> PurgeOrphansAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;

        var orphans = await db.MessageAttachments
            .Where(a => a.MessageId == null && a.CreatedAt < cutoff)
            .ToListAsync(ct);

        foreach (var orphan in orphans) await storage.DeleteAsync(orphan.StorageKey, ct);

        db.MessageAttachments.RemoveRange(orphans);
        await db.SaveChangesAsync(ct);
        return orphans.Count;
    }
}
