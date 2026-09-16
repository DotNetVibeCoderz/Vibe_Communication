using Microsoft.Extensions.Hosting;

namespace VoipNet.DependencyInjection;

/// <summary>Starts a <see cref="VoipClient"/> when the host starts and stops it on shutdown.</summary>
/// <param name="client">The client to manage.</param>
public sealed class VoipClientHostedService(VoipClient client) : IHostedService
{
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) => client.StartAsync(cancellationToken);

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.DisposeAsync().ConfigureAwait(false);
    }
}
