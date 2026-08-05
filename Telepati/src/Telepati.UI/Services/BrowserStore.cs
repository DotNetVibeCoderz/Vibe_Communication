using Microsoft.JSInterop;
using Telepati.Client.Core;
using Telepati.Shared.Contracts;

namespace Telepati.UI.Services;

/// <summary>
/// The web app's equivalent of the desktop and mobile local database, backed by IndexedDB.
///
/// On Blazor Server every call here is a round trip over the circuit, so this is used where it
/// actually pays: painting a thread immediately after a page reload or a reconnect, when the
/// alternative is an empty screen while the API answers. It is not used as a read-through cache
/// on the hot path — the server-side cache already covers that without leaving the server.
/// </summary>
public class BrowserStore(IJSRuntime js) : ILocalCacheInfo
{
    private bool? _available;

    public string ProviderName => "IndexedDB";
    public bool IsEnabled => _available ?? true;

    /// <summary>
    /// Prerendering and early startup have no DOM, so availability is probed once and every
    /// call degrades to a no-op rather than throwing at the caller.
    /// </summary>
    private async Task<bool> IsAvailableAsync()
    {
        if (_available is not null) return _available.Value;

        try
        {
            _available = await js.InvokeAsync<bool>("eval", "typeof window.telepatiStore !== 'undefined' && window.telepatiStore.available");
        }
        catch
        {
            _available = false;
        }

        return _available.Value;
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid chatId, int take, DateTimeOffset? before = null)
    {
        if (!await IsAvailableAsync()) return [];

        try
        {
            var beforeMs = before is null ? 0 : before.Value.ToUnixTimeMilliseconds();
            return await js.InvokeAsync<List<MessageDto>>("telepatiStore.getMessages", chatId, take, beforeMs);
        }
        catch
        {
            return [];
        }
    }

    public async Task SaveMessagesAsync(IEnumerable<MessageDto> messages)
    {
        if (!await IsAvailableAsync()) return;

        try { await js.InvokeVoidAsync("telepatiStore.saveMessages", messages); }
        catch { /* a full or blocked store must never break sending */ }
    }

    public async Task DeleteMessageAsync(Guid messageId)
    {
        if (!await IsAvailableAsync()) return;

        try { await js.InvokeVoidAsync("telepatiStore.deleteMessage", messageId); }
        catch { /* best effort */ }
    }

    public async Task<IReadOnlyList<ChatDto>> GetChatsAsync()
    {
        if (!await IsAvailableAsync()) return [];

        try { return await js.InvokeAsync<List<ChatDto>>("telepatiStore.getChats"); }
        catch { return []; }
    }

    public async Task SaveChatsAsync(IEnumerable<ChatDto> chats)
    {
        if (!await IsAvailableAsync()) return;

        try { await js.InvokeVoidAsync("telepatiStore.saveChats", chats); }
        catch { /* best effort */ }
    }

    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync()) return 0;

        try { return await js.InvokeAsync<int>("telepatiStore.prune", ct, 30); }
        catch { return 0; }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync()) return;

        try { await js.InvokeVoidAsync("telepatiStore.clear", ct); }
        catch { /* best effort */ }
    }

    public async Task<LocalCacheSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync()) return new LocalCacheSnapshot(ProviderName, "browser", 0, 0, 0, 0);

        try
        {
            var stats = await js.InvokeAsync<BrowserStats>("telepatiStore.stats", ct);
            return new LocalCacheSnapshot(ProviderName, "browser (IndexedDB)", stats.Chats, stats.Messages, 0, stats.Bytes);
        }
        catch
        {
            return new LocalCacheSnapshot(ProviderName, "browser", 0, 0, 0, 0);
        }
    }

    private sealed record BrowserStats(int Messages, int Chats, long Bytes);
}
