using Microsoft.EntityFrameworkCore;
using Telepati.Shared.Configuration;

namespace Telepati.Infrastructure.Data;

/// <summary>
/// The single place that knows how to point EF Core at any of the four supported engines.
/// Everything else in the codebase talks to <see cref="TelepatiDbContext"/> and stays unaware
/// of which one is live.
/// </summary>
public static class DbProviderConfigurator
{
    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder builder,
        DatabaseOptions options,
        string? connectionStringOverride = null)
    {
        var connectionString = connectionStringOverride ?? options.ConnectionString;
        var timeout = options.CommandTimeoutSeconds;

        switch (options.Provider.ToLowerInvariant())
        {
            case "sqlserver":
                builder.UseSqlServer(connectionString, o =>
                {
                    o.CommandTimeout(timeout);
                    o.EnableRetryOnFailure(3);
                });
                break;

            case "mysql":
            case "mariadb":
                builder.UseMySQL(connectionString, o => o.CommandTimeout(timeout));
                break;

            case "postgresql":
            case "postgres":
            case "npgsql":
                builder.UseNpgsql(connectionString, o =>
                {
                    o.CommandTimeout(timeout);
                    o.EnableRetryOnFailure(3);
                });
                break;

            default:
                builder.UseSqlite(connectionString, o => o.CommandTimeout(timeout));
                break;
        }

        if (options.EnableSensitiveDataLogging)
        {
            builder.EnableSensitiveDataLogging().EnableDetailedErrors();
        }

        // Tracking stays on by default. Read paths opt out with AsNoTracking() per query; making
        // NoTracking the global default silently turned every "load, mutate, SaveChanges" write
        // in the service layer into a no-op, which is far too easy to miss.
        return builder;
    }
}
