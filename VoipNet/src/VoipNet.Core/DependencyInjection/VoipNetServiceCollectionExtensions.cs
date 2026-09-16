using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VoipNet.DependencyInjection;

/// <summary>Registers Voip.NET services in a dependency injection container.</summary>
public static class VoipNetServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton <see cref="VoipClient"/> configured with <paramref name="configure"/>.
    /// Start it with <see cref="VoipClient.StartAsync"/>, or add <see cref="AddVoipClientHostedService"/>
    /// to start and stop it with the host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the client options.</param>
    public static IServiceCollection AddVoipClient(this IServiceCollection services, Action<VoipClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddSingleton<VoipClient>();
        return services;
    }

    /// <summary>Starts the registered <see cref="VoipClient"/> with the host and disposes it on shutdown.</summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddVoipClientHostedService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<VoipClientHostedService>();
        return services;
    }
}
