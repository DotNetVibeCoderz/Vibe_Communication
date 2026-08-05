using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IUserService
{
    Task<UserDto?> GetAsync(Guid userId, CancellationToken ct = default);
    Task<UserDto?> GetByUsernameAsync(string username, CancellationToken ct = default);
    Task<ApiResult<UserDto>> UpdateProfileAsync(Guid userId, string? displayName, string? about, string? avatarUrl, string? preferredLanguage, string? preferredTheme, CancellationToken ct = default);
    Task<ApiResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default);
    Task<ApiResult> SetPresenceAsync(Guid userId, UserPresence presence, CancellationToken ct = default);
    Task<ApiResult> UpdateLocationAsync(Guid userId, double latitude, double longitude, bool shareForDiscovery, CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> SearchAsync(Guid requesterId, ContactSearchRequest request, CancellationToken ct = default);
}

public class UserService(
    TelepatiDbContext db,
    ICacheService cache,
    ISettingsService settings,
    IRealtimeNotifier notifier) : IUserService
{
    public async Task<UserDto?> GetAsync(Guid userId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(CacheKeys.User(userId), async () =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            return user?.ToDto();
        }, TimeSpan.FromMinutes(5), ct);

    public async Task<UserDto?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var normalized = username.Trim().TrimStart('@').ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == normalized, ct);
        return user?.ToDto();
    }

    public async Task<ApiResult<UserDto>> UpdateProfileAsync(Guid userId, string? displayName, string? about,
        string? avatarUrl, string? preferredLanguage, string? preferredTheme, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult<UserDto>.Fail("User tidak ditemukan.");

        if (displayName is not null) user.DisplayName = displayName;
        if (about is not null) user.About = about;
        if (avatarUrl is not null) user.AvatarUrl = avatarUrl;
        if (preferredLanguage is not null) user.PreferredLanguage = preferredLanguage;
        if (preferredTheme is not null) user.PreferredTheme = preferredTheme;

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.User(userId), ct);
        return ApiResult<UserDto>.Ok(user.ToDto());
    }

    public async Task<ApiResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash)) return ApiResult.Fail("Password lama salah.");
        if (newPassword.Length < 8) return ApiResult.Fail("Password baru minimal 8 karakter.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        await db.SaveChangesAsync(ct);

        // Every other device must re-authenticate once the password changes.
        await db.UserSessions.Where(s => s.UserId == userId && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);

        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetPresenceAsync(Guid userId, UserPresence presence, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");

        user.Presence = presence;
        user.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.User(userId), ct);

        // Presence is only interesting to people who share a conversation with this user.
        var audience = await db.ChatMembers.AsNoTracking()
            .Where(m => db.ChatMembers.Any(x => x.ChatId == m.ChatId && x.UserId == userId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct);

        // Invisible users report as offline to everyone else.
        var broadcast = presence == UserPresence.Invisible ? UserPresence.Offline : presence;
        await notifier.PresenceAsync(new PresenceNotification(userId, (int)broadcast, user.LastSeenAt), audience, ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> UpdateLocationAsync(Guid userId, double latitude, double longitude, bool shareForDiscovery, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");

        user.LastLatitude = latitude;
        user.LastLongitude = longitude;
        user.ShareLocationForDiscovery = shareForDiscovery;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<IReadOnlyList<UserDto>> SearchAsync(Guid requesterId, ContactSearchRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        var take = Math.Clamp(request.Take, 1, 100);

        // People who blocked the requester (or were blocked by them) never appear in results.
        var hidden = await db.BlockedUsers.AsNoTracking()
            .Where(b => b.OwnerId == requesterId || b.BlockedUserId == requesterId)
            .Select(b => b.OwnerId == requesterId ? b.BlockedUserId : b.OwnerId)
            .ToListAsync(ct);

        if (request.Mode.Equals("nearby", StringComparison.OrdinalIgnoreCase))
        {
            return await SearchNearbyAsync(requesterId, request, options.Features, hidden, take, ct);
        }

        var term = (request.Query ?? string.Empty).Trim().ToLowerInvariant();
        if (term.Length < 2) return [];

        var query = db.Users.AsNoTracking()
            .Where(u => u.Id != requesterId && u.IsActive && !hidden.Contains(u.Id));

        query = request.Mode.ToLowerInvariant() switch
        {
            "email" => query.Where(u => u.Email == term),
            "phone" => query.Where(u => u.PhoneNumber != null && u.PhoneNumber.Replace(" ", "") == term.Replace(" ", "")),
            "username" => query.Where(u => u.Username.Contains(term.TrimStart('@'))),
            _ => query.Where(u => u.Username.Contains(term) || u.DisplayName.ToLower().Contains(term) || u.Email == term)
        };

        var users = await query.OrderBy(u => u.DisplayName).Take(take).ToListAsync(ct);
        return users.Select(u => u.ToDto()).ToList();
    }

    /// <summary>
    /// Nearby discovery only sees users who opted in from their client settings. The database
    /// filter is a cheap bounding box; the exact great-circle distance is applied in memory to
    /// the small candidate set that survives it.
    /// </summary>
    private async Task<IReadOnlyList<UserDto>> SearchNearbyAsync(
        Guid requesterId, ContactSearchRequest request, Shared.Configuration.FeatureOptions features,
        List<Guid> hidden, int take, CancellationToken ct)
    {
        if (!features.EnableNearbySearch) return [];
        if (request.Latitude is null || request.Longitude is null) return [];

        var radius = Math.Clamp(request.RadiusKm, features.MinNearbyRadiusKm, features.MaxNearbyRadiusKm);
        var lat = request.Latitude.Value;
        var lon = request.Longitude.Value;

        var latDelta = radius / 111.0;
        var lonDelta = radius / (111.0 * Math.Max(0.01, Math.Cos(lat * Math.PI / 180.0)));

        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.Id != requesterId
                        && u.IsActive
                        && u.ShareLocationForDiscovery
                        && !hidden.Contains(u.Id)
                        && u.LastLatitude != null && u.LastLongitude != null
                        && u.LastLatitude >= lat - latDelta && u.LastLatitude <= lat + latDelta
                        && u.LastLongitude >= lon - lonDelta && u.LastLongitude <= lon + lonDelta)
            .Take(take * 4)
            .ToListAsync(ct);

        return candidates
            .Select(u => new { User = u, Distance = Haversine(lat, lon, u.LastLatitude!.Value, u.LastLongitude!.Value) })
            .Where(x => x.Distance <= radius)
            .OrderBy(x => x.Distance)
            .Take(take)
            .Select(x => x.User.ToDto(Math.Round(x.Distance, 2)))
            .ToList();
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
