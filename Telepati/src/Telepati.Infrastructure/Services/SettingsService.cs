using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

/// <summary>
/// Reads and writes the live configuration. appsettings.json supplies the defaults; rows in the
/// <c>AppSettings</c> table override them. That layering is what lets the admin app change any
/// knob without touching the file or restarting the process.
/// </summary>
public interface ISettingsService
{
    Task<TelepatiOptions> GetOptionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SettingDto>> GetAllAsync(CancellationToken ct = default);
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);
    Task SetValueAsync(string key, string? value, string category = "General", string? description = null, CancellationToken ct = default);
    Task SetManyAsync(IEnumerable<SettingDto> settings, CancellationToken ct = default);
    Task ResetAsync(string key, CancellationToken ct = default);
    /// <summary>Writes every property of the bound options tree back as individual rows.</summary>
    Task SyncFromOptionsAsync(TelepatiOptions options, CancellationToken ct = default);
}

public class SettingsService(TelepatiDbContext db, ICacheService cache, TelepatiOptions fileOptions) : ISettingsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public async Task<TelepatiOptions> GetOptionsAsync(CancellationToken ct = default)
    {
        var overrides = await cache.GetOrCreateAsync(
            CacheKeys.Settings(),
            async () => await db.AppSettings.AsNoTracking()
                .ToDictionaryAsync(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase, ct),
            CacheTtl, ct);

        if (overrides.Count == 0) return fileOptions;

        var merged = Clone(fileOptions);
        foreach (var (key, value) in overrides)
        {
            ApplyOverride(merged, key, value);
        }
        return merged;
    }

    public async Task<IReadOnlyList<SettingDto>> GetAllAsync(CancellationToken ct = default)
    {
        var stored = await db.AppSettings.AsNoTracking().ToListAsync(ct);
        var storedByKey = stored.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

        // The catalogue is derived from the options tree so a newly added property shows up in
        // the admin UI immediately, with or without a row behind it.
        var result = new List<SettingDto>();
        foreach (var descriptor in Describe(fileOptions, TelepatiOptions.SectionName))
        {
            storedByKey.TryGetValue(descriptor.Key, out var row);
            result.Add(descriptor with { Value = row?.Value ?? descriptor.Value });
        }
        return result;
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken ct = default)
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return row?.Value ?? Describe(fileOptions, TelepatiOptions.SectionName)
            .FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    public async Task SetValueAsync(string key, string? value, string category = "General", string? description = null, CancellationToken ct = default)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = key,
                Value = value,
                Category = category,
                Description = description,
                IsSecret = IsSecretKey(key)
            });
        }
        else
        {
            row.Value = value;
            row.Category = category;
            if (description is not null) row.Description = description;
        }

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.Settings(), ct);
    }

    public async Task SetManyAsync(IEnumerable<SettingDto> settings, CancellationToken ct = default)
    {
        foreach (var setting in settings)
        {
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == setting.Key, ct);
            if (row is null)
            {
                db.AppSettings.Add(new AppSetting
                {
                    Key = setting.Key,
                    Value = setting.Value,
                    Category = setting.Category,
                    Description = setting.Description,
                    ValueType = setting.ValueType,
                    IsSecret = setting.IsSecret || IsSecretKey(setting.Key)
                });
            }
            else
            {
                row.Value = setting.Value;
            }
        }

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.Settings(), ct);
    }

    public async Task ResetAsync(string key, CancellationToken ct = default)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return;

        db.AppSettings.Remove(row);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.Settings(), ct);
    }

    public async Task SyncFromOptionsAsync(TelepatiOptions options, CancellationToken ct = default)
    {
        var existing = await db.AppSettings.Select(s => s.Key).ToListAsync(ct);
        var known = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in Describe(options, TelepatiOptions.SectionName))
        {
            if (known.Contains(descriptor.Key)) continue;

            db.AppSettings.Add(new AppSetting
            {
                Key = descriptor.Key,
                Value = descriptor.Value,
                Category = descriptor.Category,
                Description = descriptor.Description,
                ValueType = descriptor.ValueType,
                IsSecret = descriptor.IsSecret
            });
        }

        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.Settings(), ct);
    }

    // -- reflection helpers ---------------------------------------------------

    private static bool IsSecretKey(string key) =>
        key.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase);

    /// <summary>Flattens the options tree into <c>Telepati:Section:Property</c> rows.</summary>
    private static IEnumerable<SettingDto> Describe(object instance, string prefix, string? category = null)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;

            var key = $"{prefix}:{property.Name}";
            var value = property.GetValue(instance);
            var type = property.PropertyType;

            if (IsComplex(type) && value is not null)
            {
                foreach (var nested in Describe(value, key, category ?? property.Name))
                {
                    yield return nested;
                }
                continue;
            }

            var rendered = value switch
            {
                null => null,
                IEnumerable<string> list => string.Join(",", list),
                bool b => b.ToString().ToLowerInvariant(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            };

            yield return new SettingDto(
                key,
                rendered,
                category ?? "General",
                null,
                type.Name,
                IsSecretKey(key));
        }
    }

    private static bool IsComplex(Type type) =>
        type is { IsClass: true, IsPrimitive: false } &&
        type != typeof(string) &&
        !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

    private static void ApplyOverride(TelepatiOptions target, string key, string? value)
    {
        var parts = key.Split(':');
        if (parts.Length < 2 || !string.Equals(parts[0], TelepatiOptions.SectionName, StringComparison.OrdinalIgnoreCase)) return;

        object current = target;
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var property = current.GetType().GetProperty(parts[i], BindingFlags.Public | BindingFlags.Instance);
            var next = property?.GetValue(current);
            if (next is null) return;
            current = next;
        }

        var leaf = current.GetType().GetProperty(parts[^1], BindingFlags.Public | BindingFlags.Instance);
        if (leaf is null || !leaf.CanWrite) return;

        try
        {
            leaf.SetValue(current, Convert(value, leaf.PropertyType));
        }
        catch
        {
            // A malformed override must never take the app down; the file default stands.
        }
    }

    private static object? Convert(string? value, Type type)
    {
        if (value is null) return null;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string)) return value;
        if (underlying == typeof(bool)) return bool.Parse(value);
        if (underlying == typeof(int)) return int.Parse(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(long)) return long.Parse(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(double)) return double.Parse(value, CultureInfo.InvariantCulture);
        if (underlying == typeof(float)) return float.Parse(value, CultureInfo.InvariantCulture);
        if (underlying.IsEnum) return Enum.Parse(underlying, value, ignoreCase: true);
        if (underlying == typeof(List<string>)) return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return System.Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    private static TelepatiOptions Clone(TelepatiOptions source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<TelepatiOptions>(json) ?? new TelepatiOptions();
    }
}
