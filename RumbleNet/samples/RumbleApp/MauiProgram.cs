using Microsoft.Extensions.Logging;
using RumbleApp.Services;

namespace RumbleApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

		builder.Services.AddMauiBlazorWebView();
		builder.Services.AddSingleton<ServerStore>();
		builder.Services.AddSingleton<SettingsStore>();
		builder.Services.AddSingleton<VoiceSession>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
		builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

		return builder.Build();
	}
}
