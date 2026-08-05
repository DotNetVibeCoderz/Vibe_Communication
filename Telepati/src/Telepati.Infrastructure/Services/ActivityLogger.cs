using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IActivityLogger
{
    Task LogAsync(ActivityKind kind, Guid? userId, string? description, string? ipAddress = null,
        string? userAgent = null, string? entityType = null, Guid? entityId = null, CancellationToken ct = default);

    Task<PagedResult<ActivityLogDto>> QueryAsync(Guid? userId, ActivityKind? kind, DateTimeOffset? from,
        DateTimeOffset? to, int page, int pageSize, CancellationToken ct = default);

    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

public class ActivityLogger(TelepatiDbContext db) : IActivityLogger
{
    public async Task LogAsync(ActivityKind kind, Guid? userId, string? description, string? ipAddress = null,
        string? userAgent = null, string? entityType = null, Guid? entityId = null, CancellationToken ct = default)
    {
        db.ActivityLogs.Add(new ActivityLog
        {
            Kind = kind,
            UserId = userId,
            Description = description,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            EntityType = entityType,
            EntityId = entityId
        });

        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<ActivityLogDto>> QueryAsync(Guid? userId, ActivityKind? kind, DateTimeOffset? from,
        DateTimeOffset? to, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.ActivityLogs.AsNoTracking().Include(l => l.User).AsQueryable();

        if (userId is not null) query = query.Where(l => l.UserId == userId);
        if (kind is not null) query = query.Where(l => l.Kind == kind);
        if (from is not null) query = query.Where(l => l.CreatedAt >= from);
        if (to is not null) query = query.Where(l => l.CreatedAt <= to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ActivityLogDto>(items.Select(l => l.ToDto()).ToList(), total, page, pageSize);
    }

    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default) =>
        db.ActivityLogs.Where(l => l.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
}
