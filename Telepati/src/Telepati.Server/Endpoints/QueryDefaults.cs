namespace Telepati.Server.Endpoints;

/// <summary>
/// Paging arguments are optional on every list endpoint: omitting <c>page</c> or <c>pageSize</c>
/// must fall back to a sane default, not fail the request with a 400.
/// </summary>
public static class QueryDefaults
{
    public static int Page(int? page) => page is null or <= 0 ? 1 : page.Value;

    public static int Size(int? pageSize, int fallback) =>
        pageSize is null or <= 0 ? fallback : Math.Min(pageSize.Value, 200);
}
