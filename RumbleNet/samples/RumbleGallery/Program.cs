using Avalonia;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `RumbleGallery --run-all [filter]` runs every sample without UI (used in CI to keep samples working).
        if (args.Length > 0 && args[0] == "--run-all")
        {
            return RunAllAsync(args.Length > 1 ? args[1] : null).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static async Task<int> RunAllAsync(string? filter)
    {
        using var server = MockMumbleServer.Start();
        var failures = 0;
        foreach (var sample in SampleCatalog.Discover())
        {
            if (filter is not null && !sample.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Console.WriteLine($"=== {sample.Category} / {sample.Title}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var context = new SampleContext(server, (text, kind) => Console.WriteLine($"  [{kind}] {text}"), cts.Token);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await sample.RunAsync(context);
                Console.WriteLine($"  PASS ({watch.Elapsed.TotalSeconds:0.00} s)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(failures == 0 ? "All samples passed." : $"{failures} sample(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
