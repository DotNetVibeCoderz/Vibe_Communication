using Rumble.Net.Models;

namespace Rumble.Net.Events;

/// <summary>A received text message with resolved sender and targets.</summary>
/// <param name="Sender">Sending user (null for server messages or users no longer present).</param>
/// <param name="Channels">Target channels (empty for private messages).</param>
/// <param name="Message">Message text (may contain HTML).</param>
/// <param name="IsPrivate">True if sent directly to users.</param>
/// <param name="Timestamp">Local receive time.</param>
/// <param name="Raw">The underlying event.</param>
public sealed record TextMessage(
    User? Sender,
    IReadOnlyList<Channel> Channels,
    string Message,
    bool IsPrivate,
    DateTimeOffset Timestamp,
    TextMessageEvent Raw)
{
    /// <summary>Message with HTML tags removed.</summary>
    public string PlainText => HtmlText.ToPlainText(Message);
}

/// <summary>A user moved between channels.</summary>
public sealed record UserMovedEventArgs(User User, Channel? From, Channel? To, User? Actor);

/// <summary>A user left the server.</summary>
public sealed record UserLeftEventArgs(User? User, uint Session, User? Actor, string? Reason, bool Banned);

/// <summary>A user started or stopped talking.</summary>
public sealed record UserTalkingEventArgs(User User, bool Talking);

/// <summary>A user's state changed.</summary>
public sealed record UserUpdatedEventArgs(User User, User? Previous, User? Actor);

/// <summary>Minimal HTML → text conversion for Mumble messages.</summary>
public static class HtmlText
{
    /// <summary>Strips tags and decodes common entities.</summary>
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrEmpty(html) || html.IndexOf('<') < 0 && html.IndexOf('&') < 0)
        {
            return html;
        }

        var sb = new System.Text.StringBuilder(html.Length);
        var inTag = false;
        foreach (var ch in html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                     .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                     .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase))
        {
            switch (ch)
            {
                case '<':
                    inTag = true;
                    break;
                case '>':
                    inTag = false;
                    break;
                default:
                    if (!inTag)
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        return System.Net.WebUtility.HtmlDecode(sb.ToString()).Trim();
    }
}
