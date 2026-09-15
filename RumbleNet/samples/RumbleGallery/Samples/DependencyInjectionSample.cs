using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rumble.Net;
using Rumble.Net.DependencyInjection;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Integration", "Dependency injection", "Register RumbleClient in a service container and consume it from your services.",
    Description = "AddRumbleClient registers a singleton client that picks up ILoggerFactory from the container — the same pattern works in ASP.NET Core, worker services and MAUI.",
    Order = 140)]
public static class DependencyInjectionSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ctx.CreateLoggerFactory(LogLevel.Warning));
        services.AddRumbleClient(options =>
        {
            options.Host = ctx.Server.Host;
            options.Port = ctx.Server.Port;
            options.Username = "InjectedClient";
        });
        services.AddSingleton<StatusService>();

        await using var provider = services.BuildServiceProvider();
        var status = provider.GetRequiredService<StatusService>();
        ctx.Success(await status.DescribeAsync(ctx.Token));
    }

    private sealed class StatusService(RumbleClient client)
    {
        public async Task<string> DescribeAsync(CancellationToken cancellationToken)
        {
            if (!client.IsConnected)
            {
                await client.ConnectAsync(cancellationToken);
            }

            return $"{client.Self!.Name} sees {client.Server.Channels.Count} channels and {client.Server.Users.Count} users";
        }
    }
}
