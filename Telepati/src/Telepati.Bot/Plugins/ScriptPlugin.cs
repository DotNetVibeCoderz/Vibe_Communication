using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Telepati.Shared.Configuration;

namespace Telepati.Bot.Plugins;

/// <summary>
/// Lets the bot solve tasks that need real computation — generating a document, plotting a
/// chart, analysing a dataset, scaffolding an app — by running a script.
///
/// Three limits keep this bounded: execution must be enabled in configuration, the executor must
/// appear in the configured allow-list, and the process runs with its working directory pinned
/// to the session's workspace folder under a hard timeout.
/// </summary>
public partial class ScriptPlugin(Workspace workspace, BotOptions options, ILogger<ScriptPlugin> logger)
{
    private const int MaxOutputChars = 8000;

    [KernelFunction, Description(
        "Jalankan skrip untuk menyelesaikan tugas yang butuh komputasi nyata: analisa data, membuat chart, " +
        "mengolah dokumen, atau membuat aplikasi. Semua dijalankan di dalam workspace. " +
        "Executor yang tersedia: powershell, python, node, dotnet, cmd.")]
    public async Task<string> RunScriptAsync(
        [Description("Executor: powershell | python | node | dotnet | cmd")] string executor,
        [Description("Isi skrip atau argumen perintah")] string script,
        [Description("Sub-folder kerja di dalam workspace")] string? workingFolder = null,
        CancellationToken ct = default)
    {
        if (!options.EnableCodeExecution)
            return "Eksekusi skrip dimatikan oleh admin.";

        var normalized = executor.Trim().ToLowerInvariant();
        if (!options.AllowedExecutors.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return $"Executor '{executor}' tidak diizinkan. Tersedia: {string.Join(", ", options.AllowedExecutors)}.";

        string workingDirectory;
        try
        {
            workingDirectory = workspace.Resolve(workingFolder ?? ".");
            Directory.CreateDirectory(workingDirectory);
        }
        catch (UnauthorizedAccessException e)
        {
            return e.Message;
        }

        string? scriptFile = null;
        try
        {
            var (fileName, arguments) = await BuildCommandAsync(normalized, script, workingDirectory, ct);
            if (fileName is null) return $"Executor '{executor}' tidak dikenali.";
            scriptFile = ScriptFileFor(normalized, script, workingDirectory);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
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
                // A runaway script must not hold a worker thread or the workspace hostage.
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return $"Skrip dihentikan karena melewati batas {options.ExecutionTimeoutSeconds} detik.";
            }

            var result = new StringBuilder();
            result.AppendLine($"Exit code: {process.ExitCode}");
            if (stdout.Length > 0) result.AppendLine("--- OUTPUT ---").AppendLine(Trim(stdout.ToString()));
            if (stderr.Length > 0) result.AppendLine("--- ERROR ---").AppendLine(Trim(stderr.ToString()));

            return result.ToString();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Bot script execution failed for executor {Executor}", normalized);
            return $"Gagal menjalankan skrip: {e.Message}";
        }
        finally
        {
            if (scriptFile is not null && File.Exists(scriptFile))
            {
                try { File.Delete(scriptFile); } catch { /* best effort cleanup */ }
            }
        }
    }

    [KernelFunction, Description("Pasang library yang dibutuhkan skrip, contoh: pip install pandas matplotlib.")]
    public async Task<string> InstallPackageAsync(
        [Description("Manajer paket: pip | npm | dotnet")] string manager,
        [Description("Nama paket, boleh beberapa dipisah spasi")] string packages,
        CancellationToken ct = default)
    {
        if (!options.EnableCodeExecution) return "Eksekusi skrip dimatikan oleh admin.";
        if (!options.AllowPackageInstall) return "Instalasi paket dimatikan oleh admin.";

        var command = manager.Trim().ToLowerInvariant() switch
        {
            "pip" => ("python", $"-m pip install --quiet {packages}"),
            "npm" => ("npm", $"install {packages}"),
            "dotnet" => ("dotnet", $"add package {packages}"),
            _ => (null, null)
        };

        if (command.Item1 is null) return $"Manajer paket '{manager}' tidak dikenali.";

        return await RunProcessAsync(command.Item1, command.Item2!, workspace.Root, ct);
    }

    [KernelFunction, Description("Periksa executor mana saja yang benar-benar terpasang di server.")]
    public async Task<string> CheckAvailableExecutorsAsync(CancellationToken ct = default)
    {
        var report = new StringBuilder();
        foreach (var executor in options.AllowedExecutors)
        {
            var (fileName, arguments) = executor.ToLowerInvariant() switch
            {
                "python" => ("python", "--version"),
                "dotnet" => ("dotnet", "--version"),
                "powershell" => (PowerShellExecutable, "-Command \"$PSVersionTable.PSVersion.ToString()\""),
                "cmd" => ("cmd", "/c ver"),
                _ => (executor, "--version")
            };

            var output = await RunProcessAsync(fileName, arguments, workspace.Root, ct);
            report.AppendLine($"{executor}: {output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "tidak tersedia"}");
        }
        return report.ToString();
    }

    // -- helpers --------------------------------------------------------------

    private static string PowerShellExecutable =>
        OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";

    private static string? ScriptFileFor(string executor, string script, string workingDirectory) => executor switch
    {
        "powershell" => Path.Combine(workingDirectory, "__bacot.ps1"),
        "python" => Path.Combine(workingDirectory, "__bacot.py"),
        "cmd" => Path.Combine(workingDirectory, "__bacot.bat"),
        "node" => Path.Combine(workingDirectory, IsEsModule(script) ? "__bacot.mjs" : "__bacot.cjs"),
        _ => null
    };

    /// <summary>
    /// Whether a script should run as an ES module. Only the two forms that cannot work under
    /// CommonJS are looked for; anything else runs as CommonJS, where <c>require</c> is available.
    /// </summary>
    private static bool IsEsModule(string script) =>
        EsModuleSyntax().IsMatch(script);

    [GeneratedRegex(@"^\s*(import\s|export\s)", RegexOptions.Multiline)]
    private static partial Regex EsModuleSyntax();

    /// <summary>
    /// Scripts are written to a file rather than passed inline: multi-line code and quoting
    /// survive intact, which command-line arguments cannot guarantee across shells.
    /// </summary>
    private static async Task<(string? FileName, string Arguments)> BuildCommandAsync(
        string executor, string script, string workingDirectory, CancellationToken ct)
    {
        switch (executor)
        {
            case "powershell":
            {
                var file = Path.Combine(workingDirectory, "__bacot.ps1");
                await File.WriteAllTextAsync(file, script, ct);
                return (PowerShellExecutable, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"");
            }
            case "python":
            {
                var file = Path.Combine(workingDirectory, "__bacot.py");
                await File.WriteAllTextAsync(file, script, ct);
                return ("python", $"\"{file}\"");
            }
            case "cmd":
            {
                var file = Path.Combine(workingDirectory, "__bacot.bat");
                await File.WriteAllTextAsync(file, script, ct);
                return ("cmd", $"/c \"{file}\"");
            }
            case "node":
            {
                // The extension declares the module type. Left as plain .js, an ESM script makes
                // Node warn on stderr that it had to reparse — and that warning would surface to
                // the model as an ERROR block on a script that actually succeeded.
                var file = Path.Combine(workingDirectory, IsEsModule(script) ? "__bacot.mjs" : "__bacot.cjs");
                await File.WriteAllTextAsync(file, script, ct);
                return ("node", $"\"{file}\"");
            }
            case "dotnet":
                // dotnet takes a subcommand (run, build, new …) rather than a script body.
                return ("dotnet", script);
            default:
                return (null, string.Empty);
        }
    }

    private async Task<string> RunProcessAsync(string fileName, string arguments, string workingDirectory, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            return Trim(string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
        catch (Exception e)
        {
            return $"tidak tersedia ({e.Message})";
        }
    }

    private static string Trim(string value) =>
        value.Length <= MaxOutputChars ? value : value[..MaxOutputChars] + "\n… (output dipotong)";
}
