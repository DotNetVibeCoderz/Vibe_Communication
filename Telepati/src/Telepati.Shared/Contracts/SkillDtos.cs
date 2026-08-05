namespace Telepati.Shared.Contracts;

public record SkillRepositoryDto(
    Guid Id,
    string Name,
    string Slug,
    string? Description,
    string HomepageUrl,
    string Reference,
    string Path,
    bool IsOfficial,
    bool IsEnabled,
    DateTimeOffset? LastIndexedAt,
    int DiscoveredSkillCount,
    string? LastIndexError);

/// <summary>A skill found in a repository but not yet installed.</summary>
public record SkillSearchResultDto(
    string Name,
    string Description,
    string Path,
    Guid RepositoryId,
    string RepositorySlug,
    string SourceUrl,
    bool IsInstalled);

public record SkillDto(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    string? Version,
    string? Author,
    string? License,
    string? RepositorySlug,
    string? SourceUrl,
    bool IsEnabled,
    bool AllowScriptExecution,
    bool IsBuiltIn,
    long SizeBytes,
    int UseCount,
    DateTimeOffset? LastUsedAt,
    IReadOnlyList<string> ReferenceFiles,
    IReadOnlyList<string> ScriptFiles,
    string Instructions);

public record McpServerDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Slug { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>Matches <c>McpCategory</c>.</summary>
    public int Category { get; init; }

    /// <summary>0 = stdio (child process), 1 = HTTP.</summary>
    public int Transport { get; init; }

    public string? Command { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? Url { get; init; }

    /// <summary><c>KEY=value</c> pairs. Secret values are masked on the way out.</summary>
    public IReadOnlyList<string> EnvironmentVariables { get; init; } = [];

    public string? HomepageUrl { get; init; }
    public string? PackageName { get; init; }

    public bool RequiresApiKey { get; init; }
    public string? ApiKeyEnvironmentName { get; init; }

    public bool IsEnabled { get; init; }
    public bool IsBuiltIn { get; init; }

    /// <summary>False when the server still needs an API key before it can be turned on.</summary>
    public bool IsConfigured { get; init; }

    public bool? LastCheckSucceeded { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string? LastCheckMessage { get; init; }
    public int DiscoveredToolCount { get; init; }
    public IReadOnlyList<string> DiscoveredTools { get; init; } = [];
}

public record McpTestResultDto(bool Success, string Message, IReadOnlyList<string> Tools, long ElapsedMs);
