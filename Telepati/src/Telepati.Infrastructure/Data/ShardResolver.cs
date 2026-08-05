using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Telepati.Shared.Configuration;

namespace Telepati.Infrastructure.Data;

/// <summary>
/// Maps a conversation (or user) onto one of the configured shards.
/// Selection is a stable hash rather than a lookup table, so no coordination service is needed
/// and every node computes the same answer. Because the key is the chat id by default, all
/// messages of a conversation live together and a chat read never touches more than one shard.
/// </summary>
public interface IShardResolver
{
    bool IsEnabled { get; }
    int ShardCount { get; }
    int GetShardIndex(Guid key);
    string GetConnectionString(Guid key);
    TelepatiDbContext CreateContext(Guid key);
    IReadOnlyList<TelepatiDbContext> CreateAllContexts();
}

public class ShardResolver(DatabaseOptions options) : IShardResolver
{
    private readonly ShardingOptions _sharding = options.Sharding;

    public bool IsEnabled => _sharding.Enabled && _sharding.Shards.Count > 0;

    public int ShardCount => IsEnabled ? _sharding.Shards.Count : 1;

    public int GetShardIndex(Guid key)
    {
        if (!IsEnabled) return 0;

        // A GUID's own hash code is not stable across processes, so hash the canonical text.
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(key.ToString("N")));
        var value = BitConverter.ToUInt32(bytes, 0);
        return (int)(value % (uint)_sharding.Shards.Count);
    }

    public string GetConnectionString(Guid key) =>
        IsEnabled ? _sharding.Shards[GetShardIndex(key)] : options.ConnectionString;

    public TelepatiDbContext CreateContext(Guid key) => Build(GetConnectionString(key));

    public IReadOnlyList<TelepatiDbContext> CreateAllContexts() =>
        IsEnabled
            ? _sharding.Shards.Select(Build).ToList()
            : [Build(options.ConnectionString)];

    private TelepatiDbContext Build(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<TelepatiDbContext>();
        DbProviderConfigurator.Configure(builder, options, connectionString);
        return new TelepatiDbContext(builder.Options);
    }
}
