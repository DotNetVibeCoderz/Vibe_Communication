using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telepati.Client.Core;
using Telepati.Client.Data;
using Telepati.Shared.Configuration;
using Telepati.UI;

namespace Telepati.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts => fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular"));

        builder.Services.AddMauiBlazorWebView();

        // A MAUI app has no content root to read appsettings from, so configuration ships
        // as an embedded resource and is loaded from the assembly instead.
        var configuration = LoadConfiguration();
        builder.Configuration.AddConfiguration(configuration);

        // Mobile defaults to gRPC: binary framing is meaningfully cheaper on a metered
        // connection than SignalR's JSON frames. The user can change it in Settings.
        var clientOptions = configuration.GetSection(ClientOptions.SectionName).Get<ClientOptions>() ?? new ClientOptions();
        builder.Services.AddTelepatiClient(clientOptions);

        // On a phone the cache matters most: it removes the network from every cold start
        // and from all scrollback the user has already read.
        builder.Services.AddTelepatiLocalStore(clientOptions);
        builder.Services.AddTelepatiUI();

        builder.Services.AddSingleton(
            configuration.GetSection(TelepatiOptions.SectionName).Get<TelepatiOptions>() ?? new TelepatiOptions());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }

    private static IConfiguration LoadConfiguration()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Telepati.Mobile.appsettings.json");

        var builder = new ConfigurationBuilder();
        if (stream is not null) builder.AddJsonStream(stream);

        return builder.Build();
    }
}
