using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

/// <summary>
/// Browses and installs skills for Kang Bacot.
///
/// A skill is a folder holding <c>SKILL.md</c> plus optional <c>scripts/</c> and
/// <c>references/</c>. Because a skill can carry executable code, installation is restricted to
/// repositories an admin has explicitly listed — there is no free-form "install from any URL".
/// </summary>
public interface ISkillCatalogService
{
    Task<IReadOnlyList<SkillRepositoryDto>> GetRepositoriesAsync(CancellationToken ct = default);
    Task<ApiResult<SkillRepositoryDto>> AddRepositoryAsync(Guid adminId, string slug, string? name, string reference, string path, CancellationToken ct = default);
    Task<ApiResult> RemoveRepositoryAsync(Guid adminId, Guid repositoryId, CancellationToken ct = default);
    Task<ApiResult> SetRepositoryEnabledAsync(Guid repositoryId, bool enabled, CancellationToken ct = default);

    /// <summary>Searches the enabled repositories for skills matching a query.</summary>
    Task<ApiResult<IReadOnlyList<SkillSearchResultDto>>> SearchAsync(string? query, Guid? repositoryId, CancellationToken ct = default);

    Task<IReadOnlyList<SkillDto>> GetInstalledAsync(CancellationToken ct = default);
    Task<ApiResult<SkillDto>> InstallAsync(Guid adminId, Guid repositoryId, string skillPath, CancellationToken ct = default);
    Task<ApiResult> RemoveAsync(Guid adminId, Guid skillId, CancellationToken ct = default);
    Task<ApiResult> SetEnabledAsync(Guid skillId, bool enabled, CancellationToken ct = default);
    Task<ApiResult> SetScriptExecutionAsync(Guid skillId, bool allowed, CancellationToken ct = default);
    Task<ApiResult<SkillDto>> GetAsync(Guid skillId, CancellationToken ct = default);
}

public class SkillCatalogService(
    TelepatiDbContext db,
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    IActivityLogger activity,
    ILogger<SkillCatalogService> logger) : ISkillCatalogService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A skill folder must stay small — it is instructions and helpers, not a data set.
    private const long MaxSkillBytes = 12L * 1024 * 1024;
    private const int MaxSkillFiles = 400;

    // -- repositories ---------------------------------------------------------

    public async Task<IReadOnlyList<SkillRepositoryDto>> GetRepositoriesAsync(CancellationToken ct = default) =>
        (await db.SkillRepositories.AsNoTracking().OrderByDescending(r => r.IsOfficial).ThenBy(r => r.Name).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<ApiResult<SkillRepositoryDto>> AddRepositoryAsync(
        Guid adminId, string slug, string? name, string reference, string path, CancellationToken ct = default)
    {
        slug = slug.Trim().Trim('/');

        // Accept a full GitHub URL and reduce it to owner/repo, since that is what people paste.
        if (slug.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = slug.Split("github.com", StringSplitOptions.RemoveEmptyEntries);
            slug = parts[^1].Trim('/', ':');
        }

        var segments = slug.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return ApiResult<SkillRepositoryDto>.Fail("Format repo harus owner/repo.");

        slug = $"{segments[0]}/{segments[1]}";

        if (await db.SkillRepositories.AnyAsync(r => r.Slug == slug, ct))
            return ApiResult<SkillRepositoryDto>.Fail("Repo ini sudah terdaftar.");

        var repository = new SkillRepository
        {
            Slug = slug,
            Name = string.IsNullOrWhiteSpace(name) ? segments[1] : name,
            HomepageUrl = $"https://github.com/{slug}",
            Reference = string.IsNullOrWhiteSpace(reference) ? "main" : reference.Trim(),
            Path = path.Trim().Trim('/')
        };

        db.SkillRepositories.Add(repository);
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId,
            $"Menambah repo skill '{slug}'.", entityType: nameof(SkillRepository), entityId: repository.Id, ct: ct);

        return ApiResult<SkillRepositoryDto>.Ok(ToDto(repository));
    }

    public async Task<ApiResult> RemoveRepositoryAsync(Guid adminId, Guid repositoryId, CancellationToken ct = default)
    {
        var repository = await db.SkillRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null) return ApiResult.Fail("Repo tidak ditemukan.");
        if (repository.IsOfficial) return ApiResult.Fail("Repo bawaan tidak bisa dihapus, tetapi bisa dinonaktifkan.");

        db.SkillRepositories.Remove(repository);
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId, $"Menghapus repo skill '{repository.Slug}'.", ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetRepositoryEnabledAsync(Guid repositoryId, bool enabled, CancellationToken ct = default)
    {
        var repository = await db.SkillRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null) return ApiResult.Fail("Repo tidak ditemukan.");

        repository.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    // -- search ---------------------------------------------------------------

    public async Task<ApiResult<IReadOnlyList<SkillSearchResultDto>>> SearchAsync(
        string? query, Guid? repositoryId, CancellationToken ct = default)
    {
        var repositories = await db.SkillRepositories
            .Where(r => r.IsEnabled && (repositoryId == null || r.Id == repositoryId))
            .ToListAsync(ct);

        if (repositories.Count == 0)
            return ApiResult<IReadOnlyList<SkillSearchResultDto>>.Fail("Tidak ada repo skill yang aktif.");

        var installed = await db.Skills.AsNoTracking()
            .Select(s => s.Slug).ToListAsync(ct);

        var results = new List<SkillSearchResultDto>();
        var failures = new List<string>();

        foreach (var repository in repositories)
        {
            try
            {
                var found = await IndexRepositoryAsync(repository, ct);

                repository.LastIndexedAt = DateTimeOffset.UtcNow;
                repository.DiscoveredSkillCount = found.Count;
                repository.LastIndexError = null;

                results.AddRange(found.Select(f => f with
                {
                    IsInstalled = installed.Contains(Slugify(f.Name))
                }));
            }
            catch (Exception e)
            {
                // One unreachable repository must not blank the whole gallery.
                logger.LogWarning(e, "Could not index skill repository {Slug}", repository.Slug);
                repository.LastIndexError = e.Message;
                failures.Add(repository.Slug);
            }
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            results = results.Where(r =>
                r.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.Path.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        results = results.OrderBy(r => r.Name).ToList();

        return results.Count == 0 && failures.Count > 0
            ? ApiResult<IReadOnlyList<SkillSearchResultDto>>.Fail($"Gagal membaca repo: {string.Join(", ", failures)}")
            : ApiResult<IReadOnlyList<SkillSearchResultDto>>.Ok(results);
    }

    /// <summary>
    /// Walks a repository's tree once and pulls out every folder containing a SKILL.md. One
    /// recursive tree call is far cheaper than crawling the contents API folder by folder.
    /// </summary>
    private async Task<List<SkillSearchResultDto>> IndexRepositoryAsync(SkillRepository repository, CancellationToken ct)
    {
        var client = CreateClient();
        var url = $"https://api.github.com/repos/{repository.Slug}/git/trees/{repository.Reference}?recursive=1";

        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub menjawab {(int)response.StatusCode} untuk {repository.Slug}.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("tree", out var tree)) return [];

        var prefix = string.IsNullOrWhiteSpace(repository.Path) ? string.Empty : repository.Path.Trim('/') + "/";
        var results = new List<SkillSearchResultDto>();

        foreach (var node in tree.EnumerateArray())
        {
            var path = node.GetProperty("path").GetString() ?? string.Empty;

            if (!path.EndsWith("/SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                !path.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;

            if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
            var name = folder.Length == 0 ? repository.Name : folder[(folder.LastIndexOf('/') + 1)..];

            results.Add(new SkillSearchResultDto(
                name,
                string.Empty,
                folder,
                repository.Id,
                repository.Slug,
                $"https://github.com/{repository.Slug}/tree/{repository.Reference}/{folder}",
                false));
        }

        // The description lives in each SKILL.md's frontmatter, which the tree call does not
        // return. They are fetched together so the gallery is not a list of bare names.
        var descriptions = await Task.WhenAll(results.Take(80).Select(async result =>
        {
            try
            {
                var raw = $"https://raw.githubusercontent.com/{repository.Slug}/{repository.Reference}/" +
                          (result.Path.Length > 0 ? result.Path + "/" : string.Empty) + "SKILL.md";

                var markdown = await client.GetStringAsync(raw, ct);
                var parsed = SkillManifest.Parse(markdown);
                return (Path: result.Path, Name: parsed.Name, Description: parsed.Description);
            }
            catch
            {
                // A skill whose SKILL.md cannot be read still belongs in the list, just without
                // its description — hiding it would make the gallery quietly incomplete.
                return (Path: result.Path, Name: result.Name, Description: string.Empty);
            }
        }));

        var byPath = descriptions.ToDictionary(d => d.Path, d => d);

        return results.Select(r => byPath.TryGetValue(r.Path, out var d) && !string.IsNullOrWhiteSpace(d.Description)
            ? r with { Name = string.IsNullOrWhiteSpace(d.Name) ? r.Name : d.Name, Description = d.Description }
            : r).ToList();
    }

    // -- installed skills -----------------------------------------------------

    public async Task<IReadOnlyList<SkillDto>> GetInstalledAsync(CancellationToken ct = default) =>
        (await db.Skills.AsNoTracking().Include(s => s.Repository).OrderBy(s => s.Name).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<ApiResult<SkillDto>> GetAsync(Guid skillId, CancellationToken ct = default)
    {
        var skill = await db.Skills.AsNoTracking().Include(s => s.Repository).FirstOrDefaultAsync(s => s.Id == skillId, ct);
        return skill is null ? ApiResult<SkillDto>.Fail("Skill tidak ditemukan.") : ApiResult<SkillDto>.Ok(ToDto(skill));
    }

    public async Task<ApiResult<SkillDto>> InstallAsync(Guid adminId, Guid repositoryId, string skillPath, CancellationToken ct = default)
    {
        var repository = await db.SkillRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null) return ApiResult<SkillDto>.Fail("Repo tidak ditemukan.");
        if (!repository.IsEnabled) return ApiResult<SkillDto>.Fail("Repo ini sedang dinonaktifkan.");

        var options = await settings.GetOptionsAsync(ct);
        var client = CreateClient();

        try
        {
            // The whole repository arrives as one archive, then only the requested folder is
            // extracted. Pulling files one by one would be dozens of API calls per skill.
            var archiveUrl = $"https://codeload.github.com/{repository.Slug}/zip/refs/heads/{repository.Reference}";
            await using var stream = await client.GetStreamAsync(archiveUrl, ct);

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            buffer.Position = 0;

            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

            // GitHub archives nest everything under "<repo>-<ref>/".
            var root = archive.Entries.FirstOrDefault()?.FullName.Split('/')[0]
                       ?? throw new InvalidOperationException("Arsip repo kosong.");

            var folder = string.IsNullOrWhiteSpace(skillPath) ? root : $"{root}/{skillPath.Trim('/')}";
            var manifestEntry = archive.GetEntry($"{folder}/SKILL.md")
                                ?? throw new InvalidOperationException("SKILL.md tidak ditemukan di path itu.");

            var manifest = SkillManifest.Parse(await ReadEntryAsync(manifestEntry, ct));
            var slug = Slugify(string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileName(folder) : manifest.Name);

            if (await db.Skills.AnyAsync(s => s.Slug == slug, ct))
                return ApiResult<SkillDto>.Fail($"Skill '{slug}' sudah terpasang.");

            var target = Path.Combine(ResolveSkillsRoot(options.Bot), slug);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.CreateDirectory(target);

            var references = new List<string>();
            var scripts = new List<string>();
            long totalBytes = 0;
            var fileCount = 0;

            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith('/')) continue;

                var relative = entry.FullName[(folder.Length + 1)..];
                if (relative.Length == 0) continue;

                totalBytes += entry.Length;
                fileCount++;

                if (totalBytes > MaxSkillBytes || fileCount > MaxSkillFiles)
                {
                    Directory.Delete(target, recursive: true);
                    return ApiResult<SkillDto>.Fail("Skill terlalu besar; batasnya 12 MB / 400 berkas.");
                }

                var destination = Path.GetFullPath(Path.Combine(target, relative));

                // A zip entry can name "../" and escape the folder; refuse those outright.
                if (!destination.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(target, recursive: true);
                    return ApiResult<SkillDto>.Fail("Arsip berisi path yang keluar dari folder skill.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);

                if (relative.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase)) scripts.Add(relative);
                else if (relative.StartsWith("references/", StringComparison.OrdinalIgnoreCase)) references.Add(relative);
            }

            var skill = new SkillDefinition
            {
                Name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileName(folder) : manifest.Name,
                Slug = slug,
                Description = manifest.Description,
                Version = manifest.Version,
                Author = manifest.Author,
                License = manifest.License,
                SkillRepositoryId = repository.Id,
                SourceUrl = $"https://github.com/{repository.Slug}/tree/{repository.Reference}/{skillPath}",
                SourcePath = skillPath,
                InstalledPath = target,
                Instructions = manifest.Instructions,
                ReferenceFiles = references.Count > 0 ? string.Join('\n', references) : null,
                ScriptFiles = scripts.Count > 0 ? string.Join('\n', scripts) : null,
                SizeBytes = totalBytes,
                IsEnabled = true,
                // Installing a skill and letting it run code are two separate decisions.
                AllowScriptExecution = false
            };

            db.Skills.Add(skill);
            await db.SaveChangesAsync(ct);

            await activity.LogAsync(ActivityKind.SettingChanged, adminId,
                $"Memasang skill '{skill.Name}' dari {repository.Slug}.",
                entityType: nameof(SkillDefinition), entityId: skill.Id, ct: ct);

            return ApiResult<SkillDto>.Ok(ToDto(skill));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Skill installation failed for {Repo}/{Path}", repository.Slug, skillPath);
            return ApiResult<SkillDto>.Fail($"Gagal memasang: {e.Message}");
        }
    }

    public async Task<ApiResult> RemoveAsync(Guid adminId, Guid skillId, CancellationToken ct = default)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, ct);
        if (skill is null) return ApiResult.Fail("Skill tidak ditemukan.");
        if (skill.IsBuiltIn) return ApiResult.Fail("Skill bawaan tidak bisa dihapus, tetapi bisa dinonaktifkan.");

        try
        {
            if (Directory.Exists(skill.InstalledPath)) Directory.Delete(skill.InstalledPath, recursive: true);
        }
        catch (Exception e)
        {
            // The row must go even if the folder is locked, or the skill can never be reinstalled.
            logger.LogWarning(e, "Could not delete skill folder {Path}", skill.InstalledPath);
        }

        db.Skills.Remove(skill);
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.SettingChanged, adminId, $"Menghapus skill '{skill.Name}'.", ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetEnabledAsync(Guid skillId, bool enabled, CancellationToken ct = default)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, ct);
        if (skill is null) return ApiResult.Fail("Skill tidak ditemukan.");

        skill.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetScriptExecutionAsync(Guid skillId, bool allowed, CancellationToken ct = default)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == skillId, ct);
        if (skill is null) return ApiResult.Fail("Skill tidak ditemukan.");

        if (allowed && string.IsNullOrWhiteSpace(skill.ScriptFiles))
            return ApiResult.Fail("Skill ini tidak membawa skrip apa pun.");

        skill.AllowScriptExecution = allowed;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>Skills live beside the bot workspace so one sandbox root covers both.</summary>
    public static string ResolveSkillsRoot(BotOptions options)
    {
        var root = Path.IsPathRooted(options.WorkspacePath)
            ? options.WorkspacePath
            : Path.Combine(AppContext.BaseDirectory, options.WorkspacePath);

        var path = Path.Combine(root, "skills");
        Directory.CreateDirectory(path);
        return path;
    }

    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(nameof(SkillCatalogService));

        // GitHub rejects requests without a User-Agent outright.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Telepati-SkillCatalog/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    public static string Slugify(string value)
    {
        var cleaned = new string(value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

        while (cleaned.Contains("--")) cleaned = cleaned.Replace("--", "-");
        return cleaned.Trim('-');
    }

    private static SkillRepositoryDto ToDto(SkillRepository r) => new(
        r.Id, r.Name, r.Slug, r.Description, r.HomepageUrl, r.Reference, r.Path,
        r.IsOfficial, r.IsEnabled, r.LastIndexedAt, r.DiscoveredSkillCount, r.LastIndexError);

    private static SkillDto ToDto(SkillDefinition s) => new(
        s.Id, s.Name, s.Slug, s.Description, s.Version, s.Author, s.License,
        s.Repository?.Slug, s.SourceUrl, s.IsEnabled, s.AllowScriptExecution, s.IsBuiltIn,
        s.SizeBytes, s.UseCount, s.LastUsedAt,
        Split(s.ReferenceFiles), Split(s.ScriptFiles), s.Instructions);

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
