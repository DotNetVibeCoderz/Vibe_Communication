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
}
