using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IAdminService
{
    Task<DashboardStatsDto> GetDashboardAsync(CancellationToken ct = default);
    Task<PagedResult<UserDto>> GetUsersAsync(string? search, int page, int pageSize, CancellationToken ct = default);
    Task<ApiResult> SetUserActiveAsync(Guid adminId, Guid userId, bool active, CancellationToken ct = default);
    Task<ApiResult> SetUserRoleAsync(Guid adminId, Guid userId, UserRole role, CancellationToken ct = default);
    Task<PagedResult<ChatDto>> GetChatsAsync(ChatType? type, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<GroupInsightsDto?> GetGroupInsightsAsync(Guid chatId, CancellationToken ct = default);
    Task<PagedResult<UserReport>> GetReportsAsync(ReportState? state, int page, int pageSize, CancellationToken ct = default);
    Task<ApiResult> ResolveReportAsync(Guid adminId, Guid reportId, ReportState state, string? note, CancellationToken ct = default);
}

public class AdminService(
    TelepatiDbContext db,
    ICacheService cache,
    IStorageService storage,
    ICallService calls,
    IActivityLogger activity) : IAdminService
{
    public async Task<DashboardStatsDto> GetDashboardAsync(CancellationToken ct = default)
    {
        // The dashboard polls often; a short cache keeps it live without hammering the database.
        return await cache.GetOrCreateAsync("admin:dashboard", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var dayAgo = now.AddDays(-1);

            var messageTrend = await BuildTrendAsync(
                db.Messages.AsNoTracking().Where(m => m.CreatedAt >= now.AddDays(-13)).Select(m => m.CreatedAt), ct);

            var userTrend = await BuildTrendAsync(
                db.Users.AsNoTracking().Where(u => u.CreatedAt >= now.AddDays(-13)).Select(u => u.CreatedAt), ct);

            return new DashboardStatsDto
            {
                TotalUsers = await db.Users.CountAsync(ct),
                ActiveUsers24h = await db.Users.CountAsync(u => u.LastSeenAt >= dayAgo, ct),
                OnlineUsers = await db.Users.CountAsync(u => u.Presence == UserPresence.Online, ct),
                TotalChats = await db.Chats.CountAsync(ct),
                TotalGroups = await db.Chats.CountAsync(c => c.Type == ChatType.Group, ct),
                TotalChannels = await db.Chats.CountAsync(c => c.Type == ChatType.Channel, ct),
                TotalMessages = await db.Messages.LongCountAsync(ct),
                Messages24h = await db.Messages.LongCountAsync(m => m.CreatedAt >= dayAgo, ct),
                StorageBytes = await storage.GetTotalSizeAsync(ct),
                OpenReports = await db.UserReports.CountAsync(r => r.State == ReportState.Open, ct),
                ActiveCalls = await calls.GetActiveCallCountAsync(ct),
                BotInvocations24h = await db.ActivityLogs.CountAsync(l => l.Kind == ActivityKind.BotInvoked && l.CreatedAt >= dayAgo, ct),
                MessageTrend = messageTrend,
                UserTrend = userTrend
            };
        }, TimeSpan.FromSeconds(15), ct);
    }

    public async Task<PagedResult<UserDto>> GetUsersAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.Username.Contains(term) || u.DisplayName.ToLower().Contains(term) || u.Email.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<UserDto>(users.Select(u => u.ToDto()).ToList(), total, page, pageSize);
    }

    public async Task<ApiResult> SetUserActiveAsync(Guid adminId, Guid userId, bool active, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");

        user.IsActive = active;
        if (!active)
        {
            // Suspension must take effect immediately, not at the next token expiry.
            await db.UserSessions.Where(s => s.UserId == userId && !s.IsRevoked)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);
        }

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.User(userId), ct);
        await activity.LogAsync(ActivityKind.SettingChanged, adminId,
            $"{(active ? "Mengaktifkan" : "Menonaktifkan")} user {user.Username}.", entityType: nameof(User), entityId: userId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetUserRoleAsync(Guid adminId, Guid userId, UserRole role, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");

        // Never let the last super admin be demoted, or the system becomes unadministrable.
        if (user.Role == UserRole.SuperAdmin && role != UserRole.SuperAdmin)
        {
            var superAdmins = await db.Users.CountAsync(u => u.Role == UserRole.SuperAdmin, ct);
            if (superAdmins <= 1) return ApiResult.Fail("Minimal harus ada satu Super Admin.");
        }

        user.Role = role;
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.User(userId), ct);
        await activity.LogAsync(ActivityKind.SettingChanged, adminId, $"Role {user.Username} → {role}.", entityType: nameof(User), entityId: userId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<PagedResult<ChatDto>> GetChatsAsync(ChatType? type, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Chats.AsNoTracking().AsQueryable();

        if (type is not null) query = query.Where(c => c.Type == type);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(c => c.Title!.ToLower().Contains(term) || c.Handle!.Contains(term));
        }

        var total = await query.CountAsync(ct);
        var chats = await query
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ChatDto>(chats.Select(c => c.ToDto(null, c.Title)).ToList(), total, page, pageSize);
    }

    public async Task<GroupInsightsDto?> GetGroupInsightsAsync(Guid chatId, CancellationToken ct = default)
    {
        var chat = await db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return null;

        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        var contributors = await db.Messages.AsNoTracking()
            .Where(m => m.ChatId == chatId)
            .GroupBy(m => m.SenderId)
            .Select(g => new { UserId = g.Key, Count = g.LongCount() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .Join(db.Users.AsNoTracking(), x => x.UserId, u => u.Id,
                (x, u) => new MemberActivityDto(u.Id, u.DisplayName, x.Count))
            .ToListAsync(ct);

        var trend = await BuildTrendAsync(
            db.Messages.AsNoTracking().Where(m => m.ChatId == chatId && m.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-13)).Select(m => m.CreatedAt), ct);

        return new GroupInsightsDto(
            chat.Id,
            chat.Title ?? "(tanpa judul)",
            chat.MemberCount,
            await db.Messages.LongCountAsync(m => m.ChatId == chatId, ct),
            await db.Messages.LongCountAsync(m => m.ChatId == chatId && m.CreatedAt >= weekAgo, ct),
            contributors,
            trend);
    }

    public async Task<PagedResult<UserReport>> GetReportsAsync(ReportState? state, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.UserReports.AsNoTracking()
            .Include(r => r.Reporter)
            .Include(r => r.ReportedUser)
            .AsQueryable();

        if (state is not null) query = query.Where(r => r.State == state);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<UserReport>(items, total, page, pageSize);
    }

    public async Task<ApiResult> ResolveReportAsync(Guid adminId, Guid reportId, ReportState state, string? note, CancellationToken ct = default)
    {
        var report = await db.UserReports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null) return ApiResult.Fail("Laporan tidak ditemukan.");

        report.State = state;
        report.ResolutionNote = note;
        report.ResolvedById = adminId;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    /// <summary>Buckets timestamps into the last 14 days, filling empty days with zero.</summary>
    private static async Task<IReadOnlyList<TimeSeriesPoint>> BuildTrendAsync(IQueryable<DateTimeOffset> source, CancellationToken ct)
    {
        var timestamps = await source.ToListAsync(ct);
        var today = DateTimeOffset.UtcNow.Date;

        return Enumerable.Range(0, 14)
            .Select(offset =>
            {
                var day = today.AddDays(-13 + offset);
                var count = timestamps.Count(t => t.UtcDateTime.Date == day);
                return new TimeSeriesPoint(new DateTimeOffset(day, TimeSpan.Zero), count, day.ToString("dd MMM"));
            })
            .ToList();
    }
}
