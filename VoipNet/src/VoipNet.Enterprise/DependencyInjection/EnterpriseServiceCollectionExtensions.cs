using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoipNet.Enterprise.CallCenter;
using VoipNet.Enterprise.Crm;
using VoipNet.Enterprise.Ivr;
using VoipNet.Enterprise.Recording;

namespace VoipNet.Enterprise.DependencyInjection;

/// <summary>Registers the contact centre building blocks.</summary>
public static class EnterpriseServiceCollectionExtensions
{
    /// <summary>Registers the IVR runner, call centre service and recording service.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureRecording">Optional recording settings.</param>
    public static IServiceCollection AddVoipNetEnterprise(this IServiceCollection services, Action<RecordingOptions>? configureRecording = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var recording = new RecordingOptions();
        configureRecording?.Invoke(recording);
        services.TryAddSingleton(recording);
        services.TryAddSingleton<IvrRunner>();
        services.TryAddSingleton<CallCenterService>();
        services.TryAddSingleton<RecordingService>();
        services.TryAddSingleton<ICrmConnector, InMemoryCrmConnector>();
        return services;
    }

    /// <summary>Uses HubSpot as the CRM.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the account.</param>
    public static IServiceCollection AddHubSpotCrm(this IServiceCollection services, Action<HubSpotOptions> configure) =>
        AddCrm(services, configure, (options, http) => new HubSpotCrmConnector(options, http), "hubspot-crm");

    /// <summary>Uses Salesforce as the CRM.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the org.</param>
    public static IServiceCollection AddSalesforceCrm(this IServiceCollection services, Action<SalesforceOptions> configure) =>
        AddCrm(services, configure, (options, http) => new SalesforceCrmConnector(options, http), "salesforce-crm");

    /// <summary>Uses Dynamics 365 as the CRM.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the organisation.</param>
    public static IServiceCollection AddDynamicsCrm(this IServiceCollection services, Action<DynamicsOptions> configure) =>
        AddCrm(services, configure, (options, http) => new DynamicsCrmConnector(options, http), "dynamics-crm");

    /// <summary>Uses Odoo as the CRM.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the server.</param>
    public static IServiceCollection AddOdooCrm(this IServiceCollection services, Action<OdooOptions> configure) =>
        AddCrm(services, configure, (options, http) => new OdooCrmConnector(options, http), "odoo-crm");

    /// <summary>Registers a connector with its own named HTTP client, replacing the in-memory default.</summary>
    private static IServiceCollection AddCrm<TOptions>(
        IServiceCollection services,
        Action<TOptions> configure,
        Func<TOptions, HttpClient, ICrmConnector> create,
        string clientName)
        where TOptions : new()
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new TOptions();
        configure(options);
        services.AddHttpClient(clientName);
        services.AddSingleton<ICrmConnector>(sp =>
            create(options, sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName)));
        return services;
    }
}
