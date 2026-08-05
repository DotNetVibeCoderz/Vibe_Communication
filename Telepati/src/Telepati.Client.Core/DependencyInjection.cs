using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telepati.Shared.Configuration;

namespace Telepati.Client.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the client stack for a messenger app. All three transports are constructed;
    /// <see cref="ITelepatiClient"/> resolves to whichever one <c>TelepatiClient:Transport</c>
    /// names, which is why a user can switch it from the settings page and reconnect.
    /// </summary>
    public static IServiceCollection AddTelepatiClient(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ClientOptions.SectionName).Get<ClientOptions>() ?? new ClientOptions();
        return services.AddTelepatiClient(options);
    }

    public static IServiceCollection AddTelepatiClient(this IServiceCollection services, ClientOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<TokenStore>();

        services.AddHttpClient<RestTelepatiClient>(client =>
        {
            client.BaseAddress = new Uri(options.ServerUrl);
            client.Timeout = TimeSpan.FromSeconds(60);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // Development runs on a self-signed certificate; production hosts a real one.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

        services.AddSingleton<SignalRTelepatiClient>();
        services.AddSingleton<GrpcTelepatiClient>();

        services.AddSingleton<ITelepatiClient>(sp => TransportSelector.Resolve(sp, options.Transport));
        services.AddSingleton<TransportSwitcher>();

        return services;
    }
}

internal static class TransportSelector
{
    public static ITelepatiClient Resolve(IServiceProvider services, string transport) =>
        transport.ToLowerInvariant() switch
        {
            "grpc" => services.GetRequiredService<GrpcTelepatiClient>(),
            "rest" => services.GetRequiredService<RestTelepatiClient>(),
            _ => services.GetRequiredService<SignalRTelepatiClient>()
        };
}

/// <summary>
/// Changes the active transport at runtime. The settings page calls this; it tears the current
/// connection down and brings the new one up so the switch takes effect without a restart.
/// </summary>
public class TransportSwitcher(IServiceProvider services, ClientOptions options, ILogger<TransportSwitcher> logger)
{
    public event Action<ITelepatiClient>? TransportChanged;

    public ITelepatiClient Current { get; private set; } = TransportSelector.Resolve(services, options.Transport);

    public IReadOnlyList<string> Available => ["SignalR", "Grpc", "Rest"];

    public async Task<ITelepatiClient> SwitchAsync(string transport, CancellationToken ct = default)
    {
        if (string.Equals(transport, options.Transport, StringComparison.OrdinalIgnoreCase)) return Current;

        logger.LogInformation("Switching transport {From} → {To}", options.Transport, transport);

        await Current.DisconnectAsync(ct);

        options.Transport = transport;
        Current = TransportSelector.Resolve(services, transport);
        await Current.ConnectAsync(ct);

        TransportChanged?.Invoke(Current);
        return Current;
    }
}
