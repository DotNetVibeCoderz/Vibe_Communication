using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Telepati.Client.Core;
using Telepati.Shared.Configuration;

namespace Telepati.Client.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Adds the on-device cache and wraps the active transport with it. Call **after**
    /// <c>AddTelepatiClient</c>: this replaces the <see cref="ITelepatiClient"/> registration
    /// with a decorator around whichever transport was selected, so the UI is unaffected.
    ///
    /// The web app does not call this — it persists in the browser instead.
    /// </summary>
    public static IServiceCollection AddTelepatiLocalStore(this IServiceCollection services, ClientOptions options)
    {
        services.AddSingleton<ILocalStore>(sp => options.LocalDatabase.Provider.ToLowerInvariant() switch
        {
            "litedb" => new LiteDbLocalStore(options, sp.GetRequiredService<ILogger<LiteDbLocalStore>>()),
            "none" => new NullLocalStore(),
            _ => new SqliteLocalStore(options, sp.GetRequiredService<ILogger<SqliteLocalStore>>())
        });

        // The transport registrations stay in place; only the interface points somewhere new.
        var transport = services.Single(d => d.ServiceType == typeof(ITelepatiClient));
        services.Remove(transport);

        services.AddSingleton<ITelepatiClient>(sp =>
        {
            var inner = TransportFactory.Resolve(sp, options.Transport);

            return options.LocalDatabase.Enabled
                ? new CachedTelepatiClient(inner, sp.GetRequiredService<ILocalStore>(), options,
                    sp.GetRequiredService<ILogger<CachedTelepatiClient>>())
                : inner;
        });

        services.TryAddSingleton<LocalStoreMaintenance>();
        services.TryAddSingleton<ILocalCacheInfo>(sp => sp.GetRequiredService<LocalStoreMaintenance>());
        return services;
    }
}

/// <summary>
/// Resolves a transport by name. It mirrors the selector inside <c>Telepati.Client.Core</c>,
/// which is internal — this project only needs the same three-way choice.
/// </summary>
internal static class TransportFactory
{
    public static ITelepatiClient Resolve(IServiceProvider services, string transport) =>
        transport.ToLowerInvariant() switch
        {
            "grpc" => services.GetRequiredService<GrpcTelepatiClient>(),
            "rest" => services.GetRequiredService<RestTelepatiClient>(),
            _ => services.GetRequiredService<SignalRTelepatiClient>()
        };
}

/// <summary>Surfaces cache size and lets the settings page clear it.</summary>
public class LocalStoreMaintenance(ILocalStore store, ClientOptions options) : ILocalCacheInfo
{
    public string ProviderName => store.ProviderName;
    public bool IsEnabled => options.LocalDatabase.Enabled && store is not NullLocalStore;

    public async Task<LocalCacheSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var stats = await store.GetStatsAsync(ct);
        return new LocalCacheSnapshot(stats.Provider, stats.Location, stats.ChatCount,
            stats.MessageCount, stats.ContactCount, stats.SizeBytes);
    }

    public Task ClearAsync(CancellationToken ct = default) => store.ClearAsync(ct);

    public Task<int> PruneAsync(CancellationToken ct = default) =>
        store.PruneAsync(options.LocalDatabase.KeepMessagesPerChat,
            TimeSpan.FromDays(options.LocalDatabase.RetentionDays), ct);
}
