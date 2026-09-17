namespace VoipNet.Tests;

/// <summary>
/// Keys for tests against real services, read at run time from the file named by VOIPNET_TEST_KEYS
/// (default: a testkey.txt in a parent folder) and never stored in the repository. Sections are separated
/// by blank lines; a section starts with a header line and holds "name: value" lines.
/// </summary>
internal static class TestKeys
{
    private static readonly Lazy<IReadOnlyDictionary<string, Dictionary<string, string>>> Keys = new(Load);

    private static IReadOnlyDictionary<string, Dictionary<string, string>> Load()
    {
        var path = Environment.GetEnvironmentVariable("VOIPNET_TEST_KEYS") ?? FindDefault();
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path))
        {
            return sections;
        }

        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                current = null;
                continue;
            }

            var colon = line.IndexOf(':');
            if (current is null && colon > 0 && !line.Contains("//", StringComparison.Ordinal))
            {
                // A lone "name: value" line is a section of its own.
                var name = line[..colon].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = line[(colon + 1)..].Trim() };
                sections[name] = current;
                continue;
            }

            // "https://..." values contain a colon too, so a header is a line without "key: value" shape.
            if (current is null || colon < 0)
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line] = current;
                continue;
            }

            current[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return sections;
    }

    private static string? FindDefault()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "testkey.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string Squash(string s) => s.Replace(" ", string.Empty);

    /// <summary>The section whose header equals <paramref name="name"/> (ignoring spaces and case), or else the first that contains it.</summary>
    public static Dictionary<string, string>? Section(string name) =>
        Keys.Value.FirstOrDefault(p => Squash(p.Key).Equals(Squash(name), StringComparison.OrdinalIgnoreCase)).Value
        ?? Keys.Value.FirstOrDefault(p => Squash(p.Key).Contains(Squash(name), StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The first value in a section whose name contains <paramref name="contains"/>.</summary>
    public static string? Value(Dictionary<string, string> section, string contains) =>
        section.FirstOrDefault(p => p.Key.Contains(contains, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>A value whose name contains <paramref name="contains"/>, in any section.</summary>
    public static string? Find(string contains) =>
        Keys.Value.Values.SelectMany(s => s).FirstOrDefault(p => Squash(p.Key).Contains(Squash(contains), StringComparison.OrdinalIgnoreCase)).Value;
}
