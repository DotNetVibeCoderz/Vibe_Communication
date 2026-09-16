using Avalonia;

namespace VoipNet.Softphone;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `--screenshot <folder>` renders the real UI headlessly for the documentation.
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            return Screenshots.Capture(args[1]);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
