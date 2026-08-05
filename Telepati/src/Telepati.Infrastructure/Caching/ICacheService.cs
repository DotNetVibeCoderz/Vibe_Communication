using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Telepati.Shared.Configuration;

namespace Telepati.Infrastructure.Caching;

/// <summary>
/// Cache surface used by the whole app. Both implementations honour the same semantics so
/// switching <c>Telepati:Cache:Provider</c> between Memory (dev) and Redis (production)
/// changes nothing else.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default);
    Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
    /// <summary>Drops every key under a logical prefix, e.g. all cached pages of one chat.</summary>
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
}

public static class CacheKeys
{
    public static string User(Guid id) => $"user:{id}";
    public static string UserByName(string username) => $"user:name:{username.ToLowerInvariant()}";
    public static string ChatList(Guid userId) => $"chats:{userId}";
    public static string Chat(Guid chatId) => $"chat:{chatId}";
    public static string ChatMembers(Guid chatId) => $"chat:{chatId}:members";
    public static string Presence(Guid userId) => $"presence:{userId}";
    public static string Settings() => "settings:all";
    public static string ActiveTheme() => "theme:active";
    public static string UnreadCount(Guid userId) => $"unread:{userId}";
}

public class MemoryCacheService(IMemoryCache cache, CacheOptions options) : ICacheService
{
    // IMemoryCache exposes no key enumeration, so prefix invalidation needs its own index.
    private readonly HashSet<string> _keys = [];
    private readonly Lock _gate = new();

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) =>
        Task.FromResult(cache.TryGetValue(key, out var value) ? (T?)value : default);

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        cache.Set(key, value, ttl ?? TimeSpan.FromSeconds(options.DefaultTtlSeconds));
        lock (_gate) _keys.Add(key);
        return Task.CompletedTask;
    }

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        if (cache.TryGetValue(key, out var cached) && cached is T typed) return typed;

        var value = await factory();
        await SetAsync(key, value, ttl, ct);
        return value;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        cache.Remove(key);
        lock (_gate) _keys.Remove(key);
        return Task.CompletedTask;
    }

    public Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        List<string> matches;
        lock (_gate) matches = _keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        foreach (var key in matches)
        {
            cache.Remove(key);
            lock (_gate) _keys.Remove(key);
        }
        return Task.CompletedTask;
    }
}

public class RedisCacheService(IDistributedCache cache, CacheOptions options) : ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var payload = await cache.GetStringAsync(Prefixed(key), ct);
        return payload is null ? default : JsonSerializer.Deserialize<T>(payload, Json);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default) =>
        cache.SetStringAsync(
            Prefixed(key),
            JsonSerializer.Serialize(value, Json),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromSeconds(options.DefaultTtlSeconds)
            },
            ct);

    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null) return cached;

        var value = await factory();
        await SetAsync(key, value, ttl, ct);
        return value;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default) => cache.RemoveAsync(Prefixed(key), ct);

    /// <summary>
    /// IDistributedCache has no scan API. Rather than take a hard StackExchange.Redis dependency
    /// here, prefixed groups are tracked by an index key that lists its own members.
    /// </summary>
    public async Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var indexKey = $"{prefix}__index";
        var members = await GetAsync<List<string>>(indexKey, ct);
        if (members is null) return;

        foreach (var member in members) await cache.RemoveAsync(Prefixed(member), ct);
        await cache.RemoveAsync(Prefixed(indexKey), ct);
    }

    private string Prefixed(string key) => options.InstanceName + key;
}
