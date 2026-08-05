using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Telepati.Infrastructure.Services;

/// <summary>
/// A parsed <c>SKILL.md</c>: YAML frontmatter for the metadata, Markdown body for the
/// instructions the model actually reads.
///
/// Skills in the wild are inconsistent — some omit frontmatter, some use a different key
/// spelling, some have none of it. Parsing never throws; a malformed file degrades to
/// "name from the folder, empty description", because one bad skill must not break a gallery.
/// </summary>
public sealed record SkillManifest(
    string Name,
    string Description,
    string? Version,
    string? Author,
    string? License,
    string Instructions)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static SkillManifest Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new SkillManifest(string.Empty, string.Empty, null, null, null, string.Empty);
        }

        var text = markdown.Replace("\r\n", "\n").TrimStart('﻿');

        // Frontmatter must be the very first thing in the file, fenced by --- lines.
        if (!text.StartsWith("---\n"))
        {
            return new SkillManifest(FirstHeading(text), FirstParagraph(text), null, null, null, text.Trim());
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return new SkillManifest(FirstHeading(text), FirstParagraph(text), null, null, null, text.Trim());
        }

        var frontmatter = text[4..end];
        var body = text[(end + 4)..].TrimStart('\n', '-').Trim();

        try
        {
            var parsed = Yaml.Deserialize<Frontmatter>(frontmatter) ?? new Frontmatter();

            return new SkillManifest(
                string.IsNullOrWhiteSpace(parsed.Name) ? FirstHeading(body) : parsed.Name.Trim(),
                (parsed.Description ?? FirstParagraph(body)).Trim(),
                parsed.Version,
                parsed.Author,
                parsed.License,
                body);
        }
        catch
        {
            // Malformed YAML is common in community skills; the body is still useful.
            return new SkillManifest(FirstHeading(body), FirstParagraph(body), null, null, null, body);
        }
    }

    private static string FirstHeading(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith('#'));
        return line?.TrimStart('#', ' ').Trim() ?? string.Empty;
    }

    private static string FirstParagraph(string text)
    {
        var paragraph = text.Split('\n')
            .SkipWhile(l => string.IsNullOrWhiteSpace(l) || l.TrimStart().StartsWith('#'))
            .TakeWhile(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var joined = string.Join(' ', paragraph).Trim();
        return joined.Length <= 400 ? joined : joined[..400] + "…";
    }

    private sealed class Frontmatter
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public string? Author { get; set; }
        public string? License { get; set; }
    }
}
