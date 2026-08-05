using Telepati.Shared.Configuration;

namespace Telepati.UI.Services;

/// <summary>
/// Turns a media URL from the API into one the browser can actually fetch.
///
/// The FileSystem storage provider returns server-relative paths like <c>/files/status/…</c>,
/// which resolve against whichever host renders them. That is the server for its own pages, but
/// the messenger runs on a different port — and the desktop and mobile apps are not on a web
/// origin at all. Left alone, every image, video and attachment silently 404s.
///
/// Cloud providers already return absolute signed URLs, which pass through untouched.
/// </summary>
public class MediaResolver(ClientOptions options)
{
    public string? Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        // Absolute URLs (cloud storage, external links) and data URIs are already fetchable.
        if (Uri.TryCreate(url, UriKind.Absolute, out _)) return url;

        return $"{options.ServerUrl.TrimEnd('/')}/{url.TrimStart('/')}";
    }
}
