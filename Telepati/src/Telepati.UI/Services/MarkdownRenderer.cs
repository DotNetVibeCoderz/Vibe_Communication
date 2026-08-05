using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Telepati.UI.Services;

/// <summary>
/// Turns a bot reply into HTML for the thread. Kang Bacot answers in Markdown, so tables, code
/// blocks and media links all have to survive the trip — but the output lands in a page as raw
/// HTML, which makes sanitising the result part of rendering, not an optional extra.
/// </summary>
public class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml() // Raw HTML in a model's output is never trusted.
        .Build();

    private static readonly Regex ScriptLike = new(
        @"<\s*(script|iframe|object|embed|form)\b[^>]*>.*?<\s*/\s*\1\s*>|<\s*(script|iframe|object|embed|form)\b[^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EventHandler = new(
        @"\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JavascriptUri = new(
        @"(href|src)\s*=\s*([""'])\s*javascript:[^""']*\2",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BareMedia = new(
        @"^(https?://\S+\.(?:png|jpe?g|gif|webp|svg|mp4|webm|mp3|wav|ogg|m4a))$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TableTag = new(@"<table>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MediaToken = new(
        @"@@TP(VIDEO|AUDIO):(https?://[^\s@]+)@@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MarkupString Render(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new MarkupString(string.Empty);

        // A bare media URL on its own line is far more useful rendered than linked. Images go
        // through Markdown; video and audio have no Markdown syntax, so they travel as a plain
        // token — raw HTML would be escaped, since the pipeline deliberately disables it.
        var prepared = BareMedia.Replace(markdown, match =>
        {
            var url = match.Groups[1].Value;

            return Path.GetExtension(url).ToLowerInvariant() switch
            {
                ".mp4" or ".webm" => $"@@TPVIDEO:{url}@@",
                ".mp3" or ".wav" or ".ogg" or ".m4a" => $"@@TPAUDIO:{url}@@",
                _ => $"![]({url})"
            };
        });

        var html = Markdown.ToHtml(prepared, Pipeline);

        // Sanitise first, then expand the tokens — the elements below are ours, not the model's.
        html = Sanitize(html);

        html = MediaToken.Replace(html, match =>
        {
            var url = System.Net.WebUtility.HtmlEncode(match.Groups[2].Value);
            return match.Groups[1].Value == "VIDEO"
                ? $"<video controls src=\"{url}\"></video>"
                : $"<audio controls src=\"{url}\"></audio>";
        });

        // Wide tables scroll inside the bubble instead of stretching the thread.
        html = TableTag.Replace(html, "<div class=\"tp-md__tablewrap\"><table>")
                       .Replace("</table>", "</table></div>");

        return new MarkupString(html);
    }

    /// <summary>Plain-text preview for the conversation list, with markup stripped.</summary>
    public static string ToPreview(string? markdown, int maxLength = 90)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var text = Markdown.ToPlainText(markdown, Pipeline)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();

        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }

    private static string Sanitize(string html)
    {
        html = ScriptLike.Replace(html, string.Empty);
        html = EventHandler.Replace(html, string.Empty);
        html = JavascriptUri.Replace(html, string.Empty);
        return html;
    }
}
