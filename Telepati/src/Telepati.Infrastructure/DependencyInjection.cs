using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Services;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Configuration;

namespace Telepati.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Wires the whole data/service layer from configuration. The provider switches for
    /// database, cache and storage all live here, so a host only ever calls this once and
    /// never learns which concrete implementation it got.
    /// </summary>
    public static IServiceCollection AddTelepatiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(TelepatiOptions.SectionName).Get<TelepatiOptions>() ?? new TelepatiOptions();

        services.AddSingleton(options);
        services.AddSingleton(options.Branding);
        services.AddSingleton(options.Database);
        services.AddSingleton(options.Cache);
        services.AddSingleton(options.Storage);
        services.AddSingleton(options.Security);
        services.AddSingleton(options.Features);
        services.AddSingleton(options.Bot);
        services.AddSingleton(options.Theme);
        services.AddSingleton(options.Limits);
        services.Configure<TelepatiOptions>(configuration.GetSection(TelepatiOptions.SectionName));

        services.AddDbContext<TelepatiDbContext>(builder =>
            DbProviderConfigurator.Configure(builder, options.Database));

        services.AddSingleton<IShardResolver>(_ => new ShardResolver(options.Database));
        services.AddHttpClient();

        AddCache(services, options.Cache);
        AddStorage(services, options.Storage);

        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IActivityLogger, ActivityLogger>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IStatusService, StatusService>();
        services.AddScoped<ICallService, CallService>();
        services.AddScoped<IBroadcastService, BroadcastService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IThemeService, ThemeService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<ISkillCatalogService, SkillCatalogService>();
        services.AddScoped<IMcpCatalogService, McpCatalogService>();
        services.AddScoped<GallerySeeder>();
        services.AddScoped<DataSeeder>();

        // Hosts that can actually push (the server) replace this with a real fan-out.
        services.AddSingleton<IRealtimeNotifier, NullRealtimeNotifier>();

        return services;
    }

    private static void AddCache(IServiceCollection services, CacheOptions options)
    {
        if (options.Provider.Equals("Redis", StringComparison.OrdinalIgnoreCase))
        {
            services.AddStackExchangeRedisCache(redis =>
            {
                redis.Configuration = options.ConnectionString;
                redis.InstanceName = options.InstanceName;
            });
            services.AddSingleton<ICacheService>(sp =>
                new RedisCacheService(sp.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(), options));
        }
        else
        {
            services.AddMemoryCache();
            services.AddSingleton<ICacheService>(sp =>
                new MemoryCacheService(sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), options));
        }
    }

    private static void AddStorage(IServiceCollection services, StorageOptions options)
    {
        services.AddSingleton<IStorageService>(_ => options.Provider.ToLowerInvariant() switch
        {
            "azureblob" => new AzureBlobStorageService(options),
            "s3" => new S3StorageService(options),
            "minio" => new MinioStorageService(options),
            _ => new FileSystemStorageService(options)
        });
    }

    /// <summary>Applies migrations (or creates the schema) and seeds, honouring configuration.</summary>
    public static async Task InitializeTelepatiDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<TelepatiOptions>();
        var db = scope.ServiceProvider.GetRequiredService<TelepatiDbContext>();

        if (options.Database.AutoMigrate)
        {
            // A project without generated migrations still needs a schema, hence the fallback.
            if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync(ct);
            else await db.Database.EnsureCreatedAsync(ct);
        }

        var seeder = scope.ServiceProvider.GetRequiredService<DataSeeder>();
        await seeder.SeedAsync(ct);

        // Skill sources and the MCP catalogue seed on every start, but always disabled — the
        // list is reference data, turning an entry on is an admin's decision.
        var gallery = scope.ServiceProvider.GetRequiredService<GallerySeeder>();
        await gallery.SeedAsync(ct);

        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SyncFromOptionsAsync(options, ct);
    }
}
