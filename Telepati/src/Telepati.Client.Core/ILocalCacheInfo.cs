namespace Telepati.Client.Core;

/// <summary>
/// What the settings page needs to know about an on-device cache, without depending on the
/// storage implementation. Desktop and mobile register a real one; the web app registers none,
/// and the settings page simply omits the section.
/// </summary>
public interface ILocalCacheInfo
{
    string ProviderName { get; }
    bool IsEnabled { get; }

    Task<LocalCacheSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<int> PruneAsync(CancellationToken ct = default);
}

public record LocalCacheSnapshot(string Provider, string Location, int ChatCount, int MessageCount, int ContactCount, long SizeBytes);
