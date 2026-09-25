using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace VoipNet.Gallery;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot")
        {
            return CaptureScreenshots(args[1]);
        }

        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Runs several demos headlessly and saves what the window shows, for the documentation.</summary>
    private static int CaptureScreenshots(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

        var viewModel = new GalleryViewModel();
        var window = new MainWindow { DataContext = viewModel, Width = 1400, Height = 880 };
        window.Show();

        (string Title, int ActionIndex, double SecondsBeforeShot, string File)[] shots =
        [
            ("Overview", -1, 0.3, "gallery-overview.png"),
            ("Place a call", 0, 3.5, "gallery-call.png"),
            ("Conference", 0, 3.5, "gallery-conference.png"),
            ("Video", 0, 12, "gallery-video.png"),
            ("Language models", 1, 25, "gallery-ai-models.png"),
            ("Voice agent", 0, 90, "gallery-voice-agent.png"),
            ("IVR builder", 0, 17, "gallery-ivr.png"),
            ("Queues and agents", 0, 8, "gallery-call-center.png"),
            ("Diagnostics", 0, 4.5, "gallery-diagnostics.png"),
        ];

        foreach (var shot in shots)
        {
            var item = viewModel.Navigation.First(n => n.Label == shot.Title);
            viewModel.SelectedItem = item;
            Pump(0.3, viewModel);
            if (shot.ActionIndex >= 0)
            {
                item.Page!.Actions[shot.ActionIndex].Command.Execute(null);
            }

            Pump(shot.SecondsBeforeShot, viewModel, until: () => item.Page is { IsBusy: false });

            // Real endpoints stay out of published images: mask it only while the frame is captured.
            var realEndpoint = Pages.AiSettings.Shared.Endpoint;
            if (Pages.AiSettings.Shared.IsConfigured)
            {
                Pages.AiSettings.Shared.Endpoint = "https://your-resource.openai.azure.com/";
                Pump(0.2, viewModel);
            }

            using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // PNG default encoder
            frame?.Save(Path.Combine(folder, shot.File));
#pragma warning restore CS0618
            Console.WriteLine($"saved {shot.File}");
            Pages.AiSettings.Shared.Endpoint = realEndpoint;
        }

        foreach (var page in viewModel.Pages)
        {
            var reset = page.ResetAsync();
            while (!reset.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }
        }

        return 0;
    }

    /// <summary>Drives the UI for at least half a second and until <paramref name="until"/> holds or time runs out.</summary>
    private static void Pump(double seconds, GalleryViewModel viewModel, Func<bool>? until = null)
    {
        var start = DateTime.UtcNow;
        var end = start.AddSeconds(seconds);
        do
        {
            viewModel.Tick();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(20);
        }
        while (DateTime.UtcNow < end && !(until is not null && DateTime.UtcNow - start > TimeSpan.FromSeconds(Math.Min(seconds, 3)) && until()));
    }
}
