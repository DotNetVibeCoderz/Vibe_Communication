using Avalonia;

namespace Telepati.Desktop;

internal static class Program
{
    /// <summary>The loopback address of the in-process Blazor host, read by the main window.</summary>
    public static string BlazorAddress { get; private set; } = string.Empty;

    private static BlazorHost? _host;

    [STAThread]
    public static int Main(string[] args)
    {
        // The UI must exist before the window opens, so the Blazor host is started
        // synchronously here rather than racing the Avalonia lifetime.
        _host = new BlazorHost();
        BlazorAddress = _host.StartAsync().GetAwaiter().GetResult();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
