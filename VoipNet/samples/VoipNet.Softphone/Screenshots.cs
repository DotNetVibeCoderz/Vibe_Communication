using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using VoipNet.Softphone.ViewModels;

namespace VoipNet.Softphone;

/// <summary>Renders the softphone in representative states without a display, for the docs.</summary>
internal static class Screenshots
{
    public static int Capture(string folder)
    {
        Directory.CreateDirectory(folder);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext());

        var viewModel = new SoftphoneViewModel { UseAudioDevices = false };
        _refresh = () => viewModel.ActiveCall?.Refresh();
        var window = new MainWindow { DataContext = viewModel, Width = 1280, Height = 800 };
        window.Show();

        Pump(viewModel.StartAsync());
        Save(window, folder, "softphone-idle.png");

        viewModel.Recent.Add(new RecentCall("Budi Santoso", "sip:0812345@pbx.local", true, "04:12", "09:41", false));
        viewModel.Recent.Add(new RecentCall("2002", "sip:2002@pbx.local", false, "00:48", "09:12", false));
        viewModel.Recent.Add(new RecentCall("0215550199", "sip:0215550199@trunk", false, "486", "08:55", true));
        viewModel.DialTarget = "music";
        viewModel.DialCommand.Execute(null);
        Wait(() => viewModel.ActiveCall?.IsConnected == true, TimeSpan.FromSeconds(10));

        // No microphone in a headless run: speak a few syllables of synthetic voice instead.
        var voice = new short[16000 * 3];
        for (var i = 0; i < voice.Length; i++)
        {
            var syllable = Math.Max(0, Math.Sin(Math.PI * i / 5200.0));
            voice[i] = (short)(6000 * syllable * Math.Sin(2 * Math.PI * (180 + (40 * Math.Sin(i / 900.0))) * i / 16000));
        }

        viewModel.ActiveCall?.Call.SendAudio(voice, 16000);
        viewModel.Notes = "Customer asks to move the installation to Thursday.\nConfirmed new address: Jl. Merdeka 17, Bandung.\nPromised SMS confirmation today.";
        viewModel.Summary = "• Installation moved to Thursday at the customer's request.\n• New address confirmed: Jl. Merdeka 17, Bandung.\n• Customer expects an SMS confirmation.\n\nFollow-up: send the SMS today and update the work order.";
        Wait(() => false, TimeSpan.FromSeconds(3.5));
        Save(window, folder, "softphone-call.png");

        viewModel.IsSettingsOpen = true;
        viewModel.AiEndpoint = "https://your-resource.openai.azure.com/";
        Wait(() => false, TimeSpan.FromSeconds(0.6));
        Save(window, folder, "softphone-settings.png");

        viewModel.ActiveCall?.HangupCommand.Execute(null);
        Wait(() => viewModel.ActiveCall?.IsEnded == true, TimeSpan.FromSeconds(5));
        Pump(viewModel.DisposeAsync().AsTask());
        return 0;
    }

    private static void Pump(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }

        task.GetAwaiter().GetResult();
    }

    // Dispatcher timers do not fire on the headless platform, so the capture loop drives refreshes.
    private static Action? _refresh;

    private static void Wait(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !condition())
        {
            _refresh?.Invoke();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }
    }

    private static void Save(Avalonia.Controls.Window window, string folder, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618 // the PNG default encoder is what we want
        frame?.Save(Path.Combine(folder, name));
#pragma warning restore CS0618
        Console.WriteLine($"saved {name}");
    }
}
