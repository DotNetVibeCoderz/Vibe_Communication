using System.Text;
using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IBackupService
{
    /// <summary>Dumps the database to a portable .sql script and stores it.</summary>
    Task<BackupResultDto> CreateBackupAsync(Guid adminId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListBackupsAsync(CancellationToken ct = default);
    Task<Stream?> DownloadAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// Writes plain <c>INSERT</c> statements rather than a provider-native dump, so a backup taken
/// on SQLite in development can be replayed into SQL Server or PostgreSQL in production.
/// </summary>
public class BackupService(
    TelepatiDbContext db,
    IStorageService storage,
    IActivityLogger activity) : IBackupService
{
    public async Task<BackupResultDto> CreateBackupAsync(Guid adminId, CancellationToken ct = default)
    {
        try
        {
            var script = new StringBuilder();
            var stamp = DateTimeOffset.UtcNow;

            script.AppendLine("-- Telepati database backup");
            script.AppendLine($"-- Generated: {stamp:yyyy-MM-dd HH:mm:ss} UTC");
            script.AppendLine($"-- Source provider: {db.Database.ProviderName}");
            script.AppendLine("-- Restore order matters: parents precede children.");
            script.AppendLine();

            await DumpAsync(script, "Users", db.Users.AsNoTracking(), u => new object?[]
            {
                u.Id, u.Username, u.Email, u.PhoneNumber, u.DisplayName, u.AvatarUrl, u.About,
                u.PasswordHash, (int)u.Role, u.IsActive, u.IsBot, u.TwoFactorEnabled, u.CreatedAt
            }, ["Id", "Username", "Email", "PhoneNumber", "DisplayName", "AvatarUrl", "About",
                "PasswordHash", "Role", "IsActive", "IsBot", "TwoFactorEnabled", "CreatedAt"], ct);

            await DumpAsync(script, "Chats", db.Chats.AsNoTracking(), c => new object?[]
            {
                c.Id, (int)c.Type, c.Title, c.Description, c.Handle, c.CreatedById, c.IsPublic,
                c.IsEncrypted, c.OnlyAdminsCanPost, c.MemberCount, c.CreatedAt
            }, ["Id", "Type", "Title", "Description", "Handle", "CreatedById", "IsPublic",
                "IsEncrypted", "OnlyAdminsCanPost", "MemberCount", "CreatedAt"], ct);

            await DumpAsync(script, "ChatMembers", db.ChatMembers.AsNoTracking(), m => new object?[]
            {
                m.Id, m.ChatId, m.UserId, (int)m.Role, m.JoinedAt, m.LeftAt, m.UnreadCount, m.CreatedAt
            }, ["Id", "ChatId", "UserId", "Role", "JoinedAt", "LeftAt", "UnreadCount", "CreatedAt"], ct);

            await DumpAsync(script, "Messages", db.Messages.AsNoTracking(), m => new object?[]
            {
                m.Id, m.ChatId, m.SenderId, (int)m.Type, m.Content, m.EncryptedContent,
                m.ReplyToMessageId, m.IsPinned, m.IsEdited, (int)m.DeliveryState, m.IsBotMessage, m.CreatedAt
            }, ["Id", "ChatId", "SenderId", "Type", "Content", "EncryptedContent",
                "ReplyToMessageId", "IsPinned", "IsEdited", "DeliveryState", "IsBotMessage", "CreatedAt"], ct);

            await DumpAsync(script, "MessageAttachments", db.MessageAttachments.AsNoTracking(), a => new object?[]
            {
                a.Id, a.MessageId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.CreatedAt
            }, ["Id", "MessageId", "FileName", "ContentType", "SizeBytes", "StorageKey", "CreatedAt"], ct);

            await DumpAsync(script, "Contacts", db.Contacts.AsNoTracking(), c => new object?[]
            {
                c.Id, c.OwnerId, c.ContactUserId, c.Alias, c.IsFavorite, c.CreatedAt
            }, ["Id", "OwnerId", "ContactUserId", "Alias", "IsFavorite", "CreatedAt"], ct);

            await DumpAsync(script, "AppSettings", db.AppSettings.AsNoTracking(), s => new object?[]
            {
                s.Id, s.Key, s.Value, s.Category, s.ValueType, s.IsSecret, s.CreatedAt
            }, ["Id", "Key", "Value", "Category", "ValueType", "IsSecret", "CreatedAt"], ct);

            await DumpAsync(script, "Themes", db.Themes.AsNoTracking(), t => new object?[]
            {
                t.Id, t.Name, t.Description, t.PrimaryColor, t.SecondaryColor, t.AccentColor,
                t.BackgroundColor, t.SurfaceColor, t.TextColor, t.IconSet, t.IsDark, t.IsActive,
                t.IsSeasonal, t.ActiveFrom, t.ActiveTo, t.CreatedAt
            }, ["Id", "Name", "Description", "PrimaryColor", "SecondaryColor", "AccentColor",
                "BackgroundColor", "SurfaceColor", "TextColor", "IconSet", "IsDark", "IsActive",
                "IsSeasonal", "ActiveFrom", "ActiveTo", "CreatedAt"], ct);

            var fileName = $"telepati-backup-{stamp:yyyyMMdd-HHmmss}.sql";
            var bytes = Encoding.UTF8.GetBytes(script.ToString());

            using var stream = new MemoryStream(bytes);
            var key = await storage.UploadAsync(stream, fileName, "application/sql", StorageFolders.Backups, ct);
            var url = await storage.GetUrlAsync(key, ct);

            await activity.LogAsync(ActivityKind.BackupCreated, adminId, fileName, ct: ct);
            return new BackupResultDto(true, fileName, url, bytes.LongLength, null);
        }
        catch (Exception e)
        {
            return new BackupResultDto(false, null, null, 0, e.Message);
        }
    }

    public Task<IReadOnlyList<string>> ListBackupsAsync(CancellationToken ct = default) =>
        // Listing is provider-specific; the admin UI links the URL returned at creation time.
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<Stream?> DownloadAsync(string key, CancellationToken ct = default) => storage.DownloadAsync(key, ct);

    private static async Task DumpAsync<T>(
        StringBuilder script, string table, IQueryable<T> source,
        Func<T, object?[]> project, string[] columns, CancellationToken ct)
    {
        var rows = await source.ToListAsync(ct);
        if (rows.Count == 0) return;

        script.AppendLine($"-- {table} ({rows.Count} rows)");
        var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));

        foreach (var row in rows)
        {
            var values = string.Join(", ", project(row).Select(Literal));
            script.AppendLine($"INSERT INTO \"{table}\" ({columnList}) VALUES ({values});");
        }
        script.AppendLine();
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        // Doubling single quotes is the escape every supported engine understands.
        string s => $"'{s.Replace("'", "''")}'",
        Guid g => $"'{g}'",
        DateTimeOffset d => $"'{d.UtcDateTime:yyyy-MM-dd HH:mm:ss}'",
        DateTime d => $"'{d:yyyy-MM-dd HH:mm:ss}'",
        _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
    };
}
