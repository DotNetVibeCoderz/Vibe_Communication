using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

/// <summary>
/// Manages the MCP servers Kang Bacot may call tools on.
///
/// A stdio server is a child process launched with a command an admin configured, so nothing
/// here may ever be influenced by a model or an end user. The gallery ships a vetted catalogue;
/// anything else has to be added deliberately.
/// </summary>
public interface IMcpCatalogService
{
    Task<IReadOnlyList<McpServerDto>> GetAllAsync(McpCategory? category, string? query, CancellationToken ct = default);
    Task<ApiResult<McpServerDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<ApiResult<McpServerDto>> AddAsync(Guid adminId, McpServerDto server, CancellationToken ct = default);
    Task<ApiResult> UpdateAsync(Guid adminId, McpServerDto server, CancellationToken ct = default);
    Task<ApiResult> RemoveAsync(Guid adminId, Guid id, CancellationToken ct = default);
    Task<ApiResult> SetEnabledAsync(Guid adminId, Guid id, bool enabled, CancellationToken ct = default);

    /// <summary>Records the outcome of a connection test run by the bot layer.</summary>
    Task RecordCheckAsync(Guid id, bool succeeded, string? message, IReadOnlyList<string>? tools, CancellationToken ct = default);

    /// <summary>Everything the bot should wire up on its next kernel build.</summary>
    Task<IReadOnlyList<McpServerDto>> GetEnabledAsync(CancellationToken ct = default);
}

public class McpCatalogService(TelepatiDbContext db, IActivityLogger activity) : IMcpCatalogService
{
    public async Task<IReadOnlyList<McpServerDto>> GetAllAsync(McpCategory? category, string? query, CancellationToken ct = default)
    {
        var servers = db.McpServers.AsNoTracking().AsQueryable();

        if (category is not null) servers = servers.Where(s => s.Category == category);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            servers = servers.Where(s =>
                s.Name.ToLower().Contains(term) ||
                s.Description.ToLower().Contains(term) ||
                (s.PackageName != null && s.PackageName.ToLower().Contains(term)));
        }

        return (await servers.OrderByDescending(s => s.IsEnabled).ThenBy(s => s.Name).ToListAsync(ct))
            .Select(ToDto).ToList();
    }

    public async Task<ApiResult<McpServerDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var server = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return server is null ? ApiResult<McpServerDto>.Fail("Server MCP tidak ditemukan.") : ApiResult<McpServerDto>.Ok(ToDto(server));
    }

    public async Task<IReadOnlyList<McpServerDto>> GetEnabledAsync(CancellationToken ct = default) =>
        (await db.McpServers.AsNoTracking().Where(s => s.IsEnabled).OrderBy(s => s.Name).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<ApiResult<McpServerDto>> AddAsync(Guid adminId, McpServerDto server, CancellationToken ct = default)
    {
        var validation = Validate(server);
        if (!validation.Success) return ApiResult<McpServerDto>.Fail(validation.Error!);

        var slug = SkillCatalogService.Slugify(string.IsNullOrWhiteSpace(server.Slug) ? server.Name : server.Slug);
        if (await db.McpServers.AnyAsync(s => s.Slug == slug, ct))
            return ApiResult<McpServerDto>.Fail($"Server '{slug}' sudah ada.");

        var entity = new McpServerDefinition
        {
            Name = server.Name.Trim(),
            Slug = slug,
            Description = server.Description?.Trim() ?? string.Empty,
            Category = (McpCategory)server.Category,
            Transport = (McpTransport)server.Transport,
            Command = server.Command?.Trim(),
            Arguments = Normalize(server.Arguments),
            Url = server.Url?.Trim(),
            EnvironmentVariables = Normalize(server.EnvironmentVariables),
            HomepageUrl = server.HomepageUrl?.Trim(),
            PackageName = server.PackageName?.Trim(),
            RequiresApiKey = server.RequiresApiKey,
            ApiKeyEnvironmentName = server.ApiKeyEnvironmentName?.Trim(),
            // A newly added server starts off: an admin should test it before the bot uses it.
            IsEnabled = false
        };

        db.McpServers.Add(entity);
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId,
            $"Menambah server MCP '{entity.Name}'.", entityType: nameof(McpServerDefinition), entityId: entity.Id, ct: ct);

        return ApiResult<McpServerDto>.Ok(ToDto(entity));
    }

    public async Task<ApiResult> UpdateAsync(Guid adminId, McpServerDto server, CancellationToken ct = default)
    {
        var entity = await db.McpServers.FirstOrDefaultAsync(s => s.Id == server.Id, ct);
        if (entity is null) return ApiResult.Fail("Server MCP tidak ditemukan.");

        var validation = Validate(server);
        if (!validation.Success) return validation;

        // Built-in entries keep their identity and command; only the parameters an admin owns
        // — environment variables, category, homepage — are editable.
        if (!entity.IsBuiltIn)
        {
            entity.Name = server.Name.Trim();
            entity.Description = server.Description?.Trim() ?? string.Empty;
            entity.Transport = (McpTransport)server.Transport;
            entity.Command = server.Command?.Trim();
            entity.Arguments = Normalize(server.Arguments);
            entity.Url = server.Url?.Trim();
            entity.PackageName = server.PackageName?.Trim();
        }

        entity.Category = (McpCategory)server.Category;
        entity.EnvironmentVariables = MergeEnvironment(entity.EnvironmentVariables, server.EnvironmentVariables);
        entity.HomepageUrl = server.HomepageUrl?.Trim();
        entity.ApiKeyEnvironmentName = server.ApiKeyEnvironmentName?.Trim();

        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId,
            $"Mengubah konfigurasi MCP '{entity.Name}'.", entityType: nameof(McpServerDefinition), entityId: entity.Id, ct: ct);

        return ApiResult.Ok();
    }

    public async Task<ApiResult> RemoveAsync(Guid adminId, Guid id, CancellationToken ct = default)
    {
        var entity = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return ApiResult.Fail("Server MCP tidak ditemukan.");
        if (entity.IsBuiltIn) return ApiResult.Fail("Server bawaan tidak bisa dihapus, tetapi bisa dinonaktifkan.");

        db.McpServers.Remove(entity);
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId, $"Menghapus server MCP '{entity.Name}'.", ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetEnabledAsync(Guid adminId, Guid id, bool enabled, CancellationToken ct = default)
    {
        var entity = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return ApiResult.Fail("Server MCP tidak ditemukan.");

        // Turning on a server that still needs a key would just fail at the first tool call.
        if (enabled && entity.RequiresApiKey && !HasApiKey(entity))
        {
            return ApiResult.Fail($"Server ini butuh {entity.ApiKeyEnvironmentName ?? "sebuah API key"}. Isi dulu di parameter environment.");
        }

        entity.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId,
            $"{(enabled ? "Mengaktifkan" : "Menonaktifkan")} server MCP '{entity.Name}'.", ct: ct);

        return ApiResult.Ok();
    }

    public async Task RecordCheckAsync(Guid id, bool succeeded, string? message, IReadOnlyList<string>? tools, CancellationToken ct = default)
    {
        var entity = await db.McpServers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (entity is null) return;

        entity.LastCheckSucceeded = succeeded;
        entity.LastCheckedAt = DateTimeOffset.UtcNow;
        entity.LastCheckMessage = message;

        if (tools is not null)
        {
            entity.DiscoveredToolCount = tools.Count;
            entity.DiscoveredTools = tools.Count > 0 ? string.Join('\n', tools) : null;
        }

        await db.SaveChangesAsync(ct);
    }

    // -- helpers --------------------------------------------------------------

    private static bool HasApiKey(McpServerDefinition entity)
    {
        if (string.IsNullOrWhiteSpace(entity.ApiKeyEnvironmentName)) return true;
        if (string.IsNullOrWhiteSpace(entity.EnvironmentVariables)) return false;

        return entity.EnvironmentVariables
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line =>
            {
                var separator = line.IndexOf('=');
                if (separator <= 0) return false;

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                return key.Equals(entity.ApiKeyEnvironmentName, StringComparison.OrdinalIgnoreCase) && value.Length > 0;
            });
    }

    private static ApiResult Validate(McpServerDto server)
    {
        if (string.IsNullOrWhiteSpace(server.Name)) return ApiResult.Fail("Nama wajib diisi.");

        if ((McpTransport)server.Transport == McpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(server.Command))
                return ApiResult.Fail("Server stdio butuh perintah, misalnya npx atau uvx.");
        }
        else if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return ApiResult.Fail("Server HTTP butuh URL yang valid.");
        }

        return ApiResult.Ok();
    }

    /// <summary>
    /// Merges the submitted environment with what is stored, keeping the real value wherever the
    /// form sent back the mask. Without this, editing anything unrelated — the category, the
    /// homepage — would silently replace a working API key with bullet characters.
    /// </summary>
    private static string? MergeEnvironment(string? stored, IReadOnlyList<string>? submitted)
    {
        var existing = Split(stored).ToDictionary(
            line => line.Contains('=') ? line[..line.IndexOf('=')].Trim() : line,
            line => line,
            StringComparer.OrdinalIgnoreCase);

        var merged = new List<string>();

        foreach (var line in submitted ?? [])
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                merged.Add(trimmed);
                continue;
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();

            merged.Add(value == "••••••" && existing.TryGetValue(key, out var original) ? original : trimmed);
        }

        return merged.Count == 0 ? null : string.Join('\n', merged);
    }

    /// <summary>Normalises a multi-line field, dropping blank lines and stray carriage returns.</summary>
    private static string? Normalize(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0) return null;

        var cleaned = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return cleaned.Count == 0 ? null : string.Join('\n', cleaned);
    }

    private static McpServerDto ToDto(McpServerDefinition s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Slug = s.Slug,
        Description = s.Description,
        Category = (int)s.Category,
        Transport = (int)s.Transport,
        Command = s.Command,
        Arguments = Split(s.Arguments),
        Url = s.Url,
        // Secrets are never sent back to the browser; an empty box means "leave as-is".
        EnvironmentVariables = SplitMasked(s.EnvironmentVariables),
        HomepageUrl = s.HomepageUrl,
        PackageName = s.PackageName,
        RequiresApiKey = s.RequiresApiKey,
        ApiKeyEnvironmentName = s.ApiKeyEnvironmentName,
        IsEnabled = s.IsEnabled,
        IsBuiltIn = s.IsBuiltIn,
        IsConfigured = !s.RequiresApiKey || HasApiKey(s),
        LastCheckSucceeded = s.LastCheckSucceeded,
        LastCheckedAt = s.LastCheckedAt,
        LastCheckMessage = s.LastCheckMessage,
        DiscoveredToolCount = s.DiscoveredToolCount,
        DiscoveredTools = Split(s.DiscoveredTools)
    };

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Keeps the variable names visible but replaces the values with a placeholder.</summary>
    private static IReadOnlyList<string> SplitMasked(string? value)
    {
        return Split(value).Select(line =>
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) return line;

            var key = line[..separator];
            var secret = key.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                         || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);

            return secret && line.Length > separator + 1 ? $"{key}=••••••" : line;
        }).ToList();
    }
}
