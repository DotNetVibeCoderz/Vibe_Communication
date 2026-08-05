using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Configuration;

namespace Telepati.Bot.Plugins;

/// <summary>
/// Gives Kang Bacot access to the installed skills.
///
/// A skill is more than a document. The instructions in <c>SKILL.md</c> are only the entry
/// point; a skill may also carry reference material to read on demand and scripts to run. This
/// plugin exposes all three, which is what makes a skill able to actually do something rather
/// than just describe it.
///
/// Progressive disclosure is deliberate: the system prompt carries only names and one-line
/// descriptions, <c>LoadSkill</c> pulls in the full instructions, and references are read only
/// when the instructions point at them. Loading everything up front would exhaust the context
/// window before the conversation started.
/// </summary>
public class SkillsPlugin(
    TelepatiDbContext db,
    BotOptions options,
    ILogger<SkillsPlugin> logger)
{
    private const int MaxReferenceChars = 20000;
    private const int MaxScriptOutputChars = 8000;

    [KernelFunction, Description(
        "Daftar skill yang tersedia beserta ringkasannya. Panggil ini dulu kalau tugas terasa " +
        "butuh keahlian khusus seperti membuat dokumen, presentasi, atau analisa tertentu.")]
    public async Task<string> ListSkillsAsync(CancellationToken ct = default)
    {
        var skills = await db.Skills.AsNoTracking()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Slug, s.Name, s.Description, s.ScriptFiles, s.AllowScriptExecution })
            .ToListAsync(ct);

        if (skills.Count == 0) return "Belum ada skill yang terpasang.";

        var builder = new StringBuilder("Skill yang tersedia:\n");
        foreach (var skill in skills)
        {
            builder.Append($"- **{skill.Slug}** — {skill.Name}: {skill.Description}");
            if (!string.IsNullOrWhiteSpace(skill.ScriptFiles))
            {
                builder.Append(skill.AllowScriptExecution ? " (punya skrip yang bisa dijalankan)" : " (punya skrip, tapi eksekusi dimatikan admin)");
            }
            builder.AppendLine();
        }

        builder.AppendLine("\nPakai Skills.LoadSkill dengan slug di atas untuk membaca instruksi lengkapnya.");
        return builder.ToString();
    }

    [KernelFunction, Description(
        "Baca instruksi lengkap sebuah skill. Lakukan ini sebelum mengerjakan tugas yang cocok " +
        "dengan skill tersebut, lalu ikuti langkah-langkahnya.")]
    public async Task<string> LoadSkillAsync(
        [Description("Slug skill dari Skills.ListSkills")] string slug,
        CancellationToken ct = default)
    {
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Slug == slug && s.IsEnabled, ct);
        if (skill is null) return $"Skill '{slug}' tidak ditemukan atau sedang dinonaktifkan.";

        // Usage is counted so the admin gallery can show which skills actually earn their place.
        skill.UseCount++;
        skill.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var builder = new StringBuilder();
        builder.AppendLine($"# Skill: {skill.Name}");
        if (!string.IsNullOrWhiteSpace(skill.Version)) builder.AppendLine($"Versi {skill.Version}");
        builder.AppendLine();
        builder.AppendLine(skill.Instructions);

        var references = Lines(skill.ReferenceFiles);
        if (references.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Referensi yang tersedia");
            builder.AppendLine("Baca dengan Skills.ReadReference bila instruksi di atas menyebutnya:");
            foreach (var reference in references) builder.AppendLine($"- {reference}");
        }

        var scripts = Lines(skill.ScriptFiles);
        if (scripts.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Skrip yang disertakan");

            if (skill.AllowScriptExecution)
            {
                builder.AppendLine("Jalankan dengan Skills.RunSkillScript:");
                foreach (var script in scripts) builder.AppendLine($"- {script}");
            }
            else
            {
                builder.AppendLine("Skill ini membawa skrip, tetapi admin belum mengizinkan eksekusinya. " +
                                   "Kerjakan tugasnya dengan cara lain, atau sarankan user meminta admin mengaktifkannya.");
            }
        }

        return builder.ToString();
    }

    [KernelFunction, Description(
        "Baca berkas referensi yang dibawa sebuah skill — misalnya panduan rinci, contoh, atau " +
        "spesifikasi yang disebut di dalam instruksinya.")]
    public async Task<string> ReadReferenceAsync(
        [Description("Slug skill")] string slug,
        [Description("Path referensi relatif, contoh: references/panduan.md")] string path,
        CancellationToken ct = default)
    {
        var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == slug && s.IsEnabled, ct);
        if (skill is null) return $"Skill '{slug}' tidak ditemukan.";

        try
        {
            var file = ResolveInsideSkill(skill.InstalledPath, path);
            if (!File.Exists(file)) return $"Berkas '{path}' tidak ada di skill ini.";

            var content = await File.ReadAllTextAsync(file, ct);
            return content.Length <= MaxReferenceChars
                ? content
                : content[..MaxReferenceChars] + $"\n… (dipotong, total {content.Length:N0} karakter)";
        }
        catch (UnauthorizedAccessException e)
        {
            return e.Message;
        }
        catch (Exception e)
        {
            return $"Gagal membaca referensi: {e.Message}";
        }
    }

    [KernelFunction, Description(
        "Lihat daftar berkas yang dibawa sebuah skill, termasuk aset dan template.")]
    public async Task<string> ListSkillFilesAsync(
        [Description("Slug skill")] string slug,
        CancellationToken ct = default)
    {
        var skill = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == slug && s.IsEnabled, ct);
        if (skill is null) return $"Skill '{slug}' tidak ditemukan.";
        if (!Directory.Exists(skill.InstalledPath)) return "Folder skill tidak ditemukan di disk.";

        var files = Directory.EnumerateFiles(skill.InstalledPath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(skill.InstalledPath, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .Take(200)
            .ToList();

        return files.Count == 0 ? "Skill ini tidak membawa berkas apa pun." : string.Join('\n', files);
    }

    [KernelFunction, Description(
        "Jalankan skrip yang dibawa sebuah skill. Ini yang membuat skill bisa benar-benar " +
        "mengerjakan tugas — mengolah data, membuat dokumen, menghasilkan gambar — bukan sekadar " +
        "memberi instruksi.")]
    public async Task<string> RunSkillScriptAsync(
        [Description("Slug skill")] string slug,
        [Description("Path skrip relatif, contoh: scripts/buat_laporan.py")] string scriptPath,
        [Description("Argumen baris perintah, boleh dikosongkan")] string? arguments = null,
        CancellationToken ct = default)
    {
        if (!options.EnableCodeExecution) return "Eksekusi skrip dimatikan oleh admin.";

        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Slug == slug && s.IsEnabled, ct);
        if (skill is null) return $"Skill '{slug}' tidak ditemukan.";

        // Two separate gates: the global switch above, and this per-skill permission. Installing
        // a skill never implies letting it run code.
        if (!skill.AllowScriptExecution)
            return $"Skill '{slug}' belum diizinkan menjalankan skrip. Minta admin mengaktifkannya di Skills Gallery.";

        string script;
        try
        {
            script = ResolveInsideSkill(skill.InstalledPath, scriptPath);
        }
        catch (UnauthorizedAccessException e)
        {
            return e.Message;
        }

        if (!File.Exists(script)) return $"Skrip '{scriptPath}' tidak ada di skill ini.";

        var (executable, launchArguments) = BuildCommand(script, arguments);
        if (executable is null) return $"Jenis skrip '{Path.GetExtension(script)}' tidak didukung.";

        if (!options.AllowedExecutors.Contains(ExecutorName(script), StringComparer.OrdinalIgnoreCase))
            return $"Executor untuk '{Path.GetExtension(script)}' tidak ada di daftar izin admin.";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = launchArguments,
                    // The skill's own folder is the working directory, so relative paths inside
                    // the script resolve against its bundled assets.
                    WorkingDirectory = skill.InstalledPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return $"Skrip dihentikan karena melewati batas {options.ExecutionTimeoutSeconds} detik.";
            }

            skill.UseCount++;
            skill.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var result = new StringBuilder($"Exit code: {process.ExitCode}\n");
            if (stdout.Length > 0) result.Append("--- OUTPUT ---\n").Append(Trim(stdout.ToString()));
            if (stderr.Length > 0) result.Append("\n--- ERROR ---\n").Append(Trim(stderr.ToString()));

            return result.ToString();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Skill script failed: {Skill}/{Script}", slug, scriptPath);
            return $"Gagal menjalankan skrip: {e.Message}";
        }
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>
    /// Resolves a path the model supplied against the skill's folder and refuses anything that
    /// escapes it. Same containment rule as <see cref="Workspace"/> — a skill may only reach
    /// its own files.
    /// </summary>
    private static string ResolveInsideSkill(string skillRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new UnauthorizedAccessException("Path absolut tidak diizinkan; gunakan path relatif di dalam skill.");

        var root = Path.GetFullPath(skillRoot);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Path '{relativePath}' keluar dari folder skill.");

        return combined;
    }

    private static string ExecutorName(string script) => Path.GetExtension(script).ToLowerInvariant() switch
    {
        ".py" => "python",
        ".ps1" => "powershell",
        ".sh" => "bash",
        ".js" or ".mjs" => "node",
        ".cs" or ".csx" => "dotnet",
        ".bat" or ".cmd" => "cmd",
        _ => "unknown"
    };

    private static (string? Executable, string Arguments) BuildCommand(string script, string? arguments)
    {
        var quoted = $"\"{script}\"";
        var tail = string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments;

        return Path.GetExtension(script).ToLowerInvariant() switch
        {
            ".py" => ("python", quoted + tail),
            ".ps1" => (OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
                       $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {quoted}{tail}"),
            ".sh" => ("bash", quoted + tail),
            ".js" or ".mjs" => ("node", quoted + tail),
            ".bat" or ".cmd" => ("cmd", $"/c {quoted}{tail}"),
            _ => (null, string.Empty)
        };
    }

    private static List<string> Lines(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Trim(string value) =>
        value.Length <= MaxScriptOutputChars ? value : value[..MaxScriptOutputChars] + "\n… (output dipotong)";
}
