using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IThemeService
{
    Task<IReadOnlyList<ThemeDto>> GetAllAsync(CancellationToken ct = default);
    /// <summary>Theme the clients should render right now, seasonal window included.</summary>
    Task<ThemeDto> GetActiveAsync(CancellationToken ct = default);
    Task<ApiResult<ThemeDto>> CreateAsync(ThemeDto theme, CancellationToken ct = default);
    Task<ApiResult> UpdateAsync(ThemeDto theme, CancellationToken ct = default);
    Task<ApiResult> ActivateAsync(Guid themeId, CancellationToken ct = default);
    Task<ApiResult> DeleteAsync(Guid themeId, CancellationToken ct = default);
}

public class ThemeService(TelepatiDbContext db, ICacheService cache, ISettingsService settings) : IThemeService
{
    public async Task<IReadOnlyList<ThemeDto>> GetAllAsync(CancellationToken ct = default) =>
        (await db.Themes.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct))
        .Select(t => t.ToDto()).ToList();

    public async Task<ThemeDto> GetActiveAsync(CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(CacheKeys.ActiveTheme(), async () =>
        {
            var options = await settings.GetOptionsAsync(ct);
            var now = DateTimeOffset.UtcNow;

            // A seasonal theme inside its date window outranks the manually activated one, so
            // Lebaran or New Year skins switch themselves on and off without an admin visit.
            if (options.Theme.EnableSeasonalThemes)
            {
                var seasonal = await db.Themes.AsNoTracking()
                    .Where(t => t.IsSeasonal && t.ActiveFrom <= now && t.ActiveTo >= now)
                    .OrderByDescending(t => t.ActiveFrom)
                    .FirstOrDefaultAsync(ct);

                if (seasonal is not null) return seasonal.ToDto();
            }

            var active = await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.IsActive && !t.IsSeasonal, ct)
                         ?? await db.Themes.AsNoTracking().FirstOrDefaultAsync(t => t.Name == options.Theme.DefaultThemeName, ct);

            return active?.ToDto() ?? DefaultTheme;
        }, TimeSpan.FromMinutes(5), ct);

    public async Task<ApiResult<ThemeDto>> CreateAsync(ThemeDto theme, CancellationToken ct = default)
    {
        if (await db.Themes.AnyAsync(t => t.Name == theme.Name, ct))
            return ApiResult<ThemeDto>.Fail("Nama tema sudah dipakai.");

        var entity = new ThemeDefinition
        {
            Name = theme.Name,
            Description = theme.Description,
            PrimaryColor = theme.PrimaryColor,
            SecondaryColor = theme.SecondaryColor,
            AccentColor = theme.AccentColor,
            BackgroundColor = theme.BackgroundColor,
            SurfaceColor = theme.SurfaceColor,
            TextColor = theme.TextColor,
            IconSet = theme.IconSet,
            IsDark = theme.IsDark,
            IsSeasonal = theme.IsSeasonal,
            ActiveFrom = theme.ActiveFrom,
            ActiveTo = theme.ActiveTo
        };

        db.Themes.Add(entity);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ActiveTheme(), ct);
        return ApiResult<ThemeDto>.Ok(entity.ToDto());
    }

    public async Task<ApiResult> UpdateAsync(ThemeDto theme, CancellationToken ct = default)
    {
        var entity = await db.Themes.FirstOrDefaultAsync(t => t.Id == theme.Id, ct);
        if (entity is null) return ApiResult.Fail("Tema tidak ditemukan.");

        entity.Name = theme.Name;
        entity.Description = theme.Description;
        entity.PrimaryColor = theme.PrimaryColor;
        entity.SecondaryColor = theme.SecondaryColor;
        entity.AccentColor = theme.AccentColor;
        entity.BackgroundColor = theme.BackgroundColor;
        entity.SurfaceColor = theme.SurfaceColor;
        entity.TextColor = theme.TextColor;
        entity.IconSet = theme.IconSet;
        entity.IsDark = theme.IsDark;
        entity.IsSeasonal = theme.IsSeasonal;
        entity.ActiveFrom = theme.ActiveFrom;
        entity.ActiveTo = theme.ActiveTo;

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ActiveTheme(), ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> ActivateAsync(Guid themeId, CancellationToken ct = default)
    {
        var theme = await db.Themes.FirstOrDefaultAsync(t => t.Id == themeId, ct);
        if (theme is null) return ApiResult.Fail("Tema tidak ditemukan.");

        await db.Themes.Where(t => t.IsActive).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsActive, false), ct);

        theme.IsActive = true;
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ActiveTheme(), ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DeleteAsync(Guid themeId, CancellationToken ct = default)
    {
        var theme = await db.Themes.FirstOrDefaultAsync(t => t.Id == themeId, ct);
        if (theme is null) return ApiResult.Fail("Tema tidak ditemukan.");
        if (theme.IsActive) return ApiResult.Fail("Tema aktif tidak bisa dihapus.");

        db.Themes.Remove(theme);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ActiveTheme(), ct);
        return ApiResult.Ok();
    }

    /// <summary>Fallback when the themes table is empty, so the UI always has colours to render.</summary>
    private static ThemeDto DefaultTheme => new(
        Guid.Empty, "Telepati Classic", "Tema bawaan Telepati",
        "#6C5CE7", "#00CEC9", "#FD79A8", "#FFFFFF", "#F5F6FA", "#2D3436",
        "💬", false, true, false, null, null);
}
