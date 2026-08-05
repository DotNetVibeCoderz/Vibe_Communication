using System.ComponentModel.DataAnnotations;

namespace Telepati.Domain;

/// <summary>
/// A source of skills the admin trusts. Only repositories listed here can be browsed or
/// installed from — a skill is instructions plus executable scripts, so "where did this come
/// from" is a security question, not a convenience one.
/// </summary>
public class SkillRepository : BaseEntity
{
    [MaxLength(128)] public string Name { get; set; } = string.Empty;

    /// <summary>GitHub repository in <c>owner/repo</c> form.</summary>
    [MaxLength(256)] public string Slug { get; set; } = string.Empty;

    [MaxLength(512)] public string? Description { get; set; }
    [MaxLength(512)] public string HomepageUrl { get; set; } = string.Empty;

    /// <summary>Branch or tag to read from.</summary>
    [MaxLength(64)] public string Reference { get; set; } = "main";

    /// <summary>Sub-folder that holds the skills; empty means the repository root.</summary>
    [MaxLength(256)] public string Path { get; set; } = string.Empty;

    /// <summary>Shipped with Telepati and vetted; a built-in source cannot be deleted.</summary>
    public bool IsOfficial { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? LastIndexedAt { get; set; }
    public int DiscoveredSkillCount { get; set; }
    [MaxLength(1024)] public string? LastIndexError { get; set; }
}

/// <summary>
/// An installed skill: a folder holding <c>SKILL.md</c> plus, optionally, scripts the bot may
/// run and reference documents it may read. The instructions are cached on the row so the
/// common path (listing what the bot can do) never touches the disk.
/// </summary>
public class SkillDefinition : BaseEntity
{
    [MaxLength(128)] public string Name { get; set; } = string.Empty;

    /// <summary>Folder-safe identifier, unique across installed skills.</summary>
    [MaxLength(128)] public string Slug { get; set; } = string.Empty;

    [MaxLength(1024)] public string Description { get; set; } = string.Empty;
    [MaxLength(64)] public string? Version { get; set; }
    [MaxLength(128)] public string? Author { get; set; }
    [MaxLength(256)] public string? License { get; set; }

    public Guid? SkillRepositoryId { get; set; }
    public SkillRepository? Repository { get; set; }

    /// <summary>Where it came from, kept so an admin can audit or re-install it.</summary>
    [MaxLength(512)] public string? SourceUrl { get; set; }
    [MaxLength(256)] public string? SourcePath { get; set; }

    /// <summary>Folder under the bot workspace that holds the installed files.</summary>
    [MaxLength(512)] public string InstalledPath { get; set; } = string.Empty;

    /// <summary>Body of SKILL.md — the instructions handed to the model when the skill loads.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Relative paths of bundled reference documents, one per line.</summary>
    public string? ReferenceFiles { get; set; }

    /// <summary>Relative paths of bundled scripts, one per line.</summary>
    public string? ScriptFiles { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether the bot may execute this skill's bundled scripts. Off by default: installing a
    /// skill and granting it execution are deliberately two separate decisions.
    /// </summary>
    public bool AllowScriptExecution { get; set; }

    public bool IsBuiltIn { get; set; }
    public long SizeBytes { get; set; }
    public int UseCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public enum McpTransport
{
    /// <summary>Launched as a child process and spoken to over stdin/stdout.</summary>
    Stdio = 0,
    /// <summary>A remote endpoint over HTTP (streamable or SSE).</summary>
    Http = 1
}

public enum McpCategory
{
    General = 0,
    Coding = 1,
    Design = 2,
    ThreeD = 3,
    Presentation = 4,
    Document = 5,
    Data = 6,
    Web = 7,
    Productivity = 8
}

/// <summary>
/// An MCP server the bot may call tools on. Stdio servers are launched as child processes, so
/// the command and arguments are treated as configuration an admin owns — never something a
/// model or an end user can influence.
/// </summary>
public class McpServerDefinition : BaseEntity
{
    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    [MaxLength(128)] public string Slug { get; set; } = string.Empty;
    [MaxLength(1024)] public string Description { get; set; } = string.Empty;

    public McpCategory Category { get; set; } = McpCategory.General;
    public McpTransport Transport { get; set; } = McpTransport.Stdio;

    /// <summary>Executable for a stdio server, e.g. <c>npx</c> or <c>uvx</c>.</summary>
    [MaxLength(256)] public string? Command { get; set; }

    /// <summary>Arguments, one per line, so values containing spaces survive intact.</summary>
    public string? Arguments { get; set; }

    /// <summary>Endpoint for an HTTP server.</summary>
    [MaxLength(512)] public string? Url { get; set; }

    /// <summary>Environment variables as <c>KEY=value</c>, one per line.</summary>
    public string? EnvironmentVariables { get; set; }

    [MaxLength(512)] public string? HomepageUrl { get; set; }
    [MaxLength(256)] public string? PackageName { get; set; }

    /// <summary>Set when the server cannot work until an admin supplies a key.</summary>
    public bool RequiresApiKey { get; set; }
    [MaxLength(256)] public string? ApiKeyEnvironmentName { get; set; }

    public bool IsEnabled { get; set; }
    public bool IsBuiltIn { get; set; }

    /// <summary>Result of the last connection test, so the gallery can show what is reachable.</summary>
    public bool? LastCheckSucceeded { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    [MaxLength(1024)] public string? LastCheckMessage { get; set; }
    public int DiscoveredToolCount { get; set; }

    /// <summary>Tool names reported at the last successful check, one per line.</summary>
    public string? DiscoveredTools { get; set; }
}
