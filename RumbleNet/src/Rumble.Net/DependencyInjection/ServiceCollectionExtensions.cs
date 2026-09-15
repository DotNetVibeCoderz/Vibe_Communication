using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Rumble.Net.DependencyInjection;

/// <summary>Dependency injection registration helpers.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="RumbleClient"/> configured by <paramref name="configure"/>.
    /// The <see cref="ILoggerFactory"/> from the container is used when available.
    /// </summary>
    public static IServiceCollection AddRumbleClient(this IServiceCollection services, Action<RumbleClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.TryAddSingleton(sp =>
        {
            var options = new RumbleClientOptions { LoggerFactory = sp.GetService<ILoggerFactory>() };
            configure(options);
            return options;
        });
        services.TryAddSingleton(sp => new RumbleClient(sp.GetRequiredService<RumbleClientOptions>()));
        return services;
    }
}
