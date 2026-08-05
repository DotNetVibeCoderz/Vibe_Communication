using Telepati.Shared.Configuration;

namespace Telepati.Bot.Plugins;

/// <summary>
/// The sandbox root every bot file operation and script execution is confined to. All paths the
/// model supplies pass through <see cref="Resolve"/>, which is the single containment boundary:
/// if it cannot be expressed inside the workspace, the call fails rather than reaching the disk.
/// </summary>
public class Workspace
{
    public Workspace(BotOptions options)
    {
        Root = Path.IsPathRooted(options.WorkspacePath)
            ? options.WorkspacePath
            : Path.Combine(AppContext.BaseDirectory, options.WorkspacePath);

        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Per-session subfolder, so two conversations never overwrite each other's files.</summary>
    public string ResolveSessionFolder(Guid sessionId)
    {
        var path = Path.Combine(Root, "sessions", sessionId.ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) relativePath = ".";

        // Reject absolute paths outright — the model has no business naming drive letters.
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException("Path absolut tidak diizinkan; gunakan path relatif di dalam workspace.");
        }

        var combined = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!combined.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path '{relativePath}' keluar dari workspace.");
        }

        return combined;
    }
}
