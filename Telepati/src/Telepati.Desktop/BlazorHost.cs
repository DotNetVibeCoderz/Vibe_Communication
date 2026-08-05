using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telepati.Client.Core;
using Telepati.Client.Data;
using Telepati.Desktop.Components;
using Telepati.Shared.Configuration;
using Telepati.UI;

namespace Telepati.Desktop;

/// <summary>
/// Runs the messenger UI as a Blazor Server app inside this process, bound to loopback only.
/// The Avalonia window points its WebView at the address this returns.
///
/// Port 0 lets the OS pick a free port, so two copies of the desktop app can run side by side
/// and a stale process can never block startup.
/// </summary>
public sealed class BlazorHost : IAsyncDisposable
{
    private WebApplication? _app;

    public string Address { get; private set; } = string.Empty;

    public async Task<string> StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // The desktop host never runs in Development, so the RCL assets that make up the
        // UI have to be loaded explicitly or the window renders an unstyled, dead page.
        builder.WebHost.UseStaticWebAssets();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        var clientOptions = builder.Configuration.GetSection(ClientOptions.SectionName).Get<ClientOptions>() ?? new ClientOptions();
        builder.Services.AddTelepatiClient(clientOptions);

        // Wraps the selected transport with the on-device cache, so a cold start paints
        // from disk and scrollback already read never hits the network again.
        builder.Services.AddTelepatiLocalStore(clientOptions);
        builder.Services.AddTelepatiUI();
        builder.Services.AddSingleton(
            builder.Configuration.GetSection(TelepatiOptions.SectionName).Get<TelepatiOptions>() ?? new TelepatiOptions());

        _app = builder.Build();

        _app.MapStaticAssets();
        _app.UseAntiforgery();
        _app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // Routable pages live in Telepati.UI. The Router's AdditionalAssemblies only covers
    // client-side navigation; endpoint discovery needs this as well, or every URL 404s.
    .AddAdditionalAssemblies(typeof(Telepati.UI.Pages.Messenger).Assembly);

        await _app.StartAsync(ct);

        // The bound port is only known after the server starts.
        var address = _app.Urls.FirstOrDefault() ?? "http://127.0.0.1:5000";
        Address = address;
        return address;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;

        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
