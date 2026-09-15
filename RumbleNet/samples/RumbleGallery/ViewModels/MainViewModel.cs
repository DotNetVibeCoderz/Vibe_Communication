using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rumble.Net;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromMinutes(2);

    private readonly IReadOnlyList<GallerySample> _all;
    private MockMumbleServer? _server;
    private CancellationTokenSource? _runCts;

    public MainViewModel()
    {
        _all = SampleCatalog.Discover();
        VisibleSamples = new ObservableCollection<GallerySample>(_all);
        SelectedSample = _all.FirstOrDefault();
        ServerStatus = $"Rumble.Net native {SafeVersion()} · demo server starts when you run a sample";
    }

    public ObservableCollection<GallerySample> VisibleSamples { get; }

    public ObservableCollection<OutputLine> Output { get; } = [];

    /// <summary>Set by the view to access the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Code))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CopyCodeCommand))]
    public partial GallerySample? SelectedSample { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunButtonText), nameof(OutputHeader))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string ServerStatus { get; set; }

    public string Code => SelectedSample?.Code ?? string.Empty;

    public string RunButtonText => IsRunning ? "Run again" : "Run sample";

    public string OutputHeader => IsRunning ? "OUTPUT · RUNNING" : "OUTPUT";

    public string SampleCountText => $"{VisibleSamples.Count} OF {_all.Count} SAMPLES";

    partial void OnSearchChanged(string value)
    {
        var terms = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        VisibleSamples.Clear();
        foreach (var sample in _all.Where(s => terms.All(t =>
                     s.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                     s.Category.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                     s.Summary.Contains(t, StringComparison.OrdinalIgnoreCase))))
        {
            VisibleSamples.Add(sample);
        }

        OnPropertyChanged(nameof(SampleCountText));
    }

    [RelayCommand(CanExecute = nameof(HasSample))]
    private async Task RunAsync()
    {
        if (SelectedSample is not { } sample)
        {
            return;
        }

        _runCts?.Cancel();
        var cts = new CancellationTokenSource(SampleTimeout);
        _runCts = cts;

        Output.Clear();
        IsRunning = true;
        Write($"▶ {sample.Title}", OutputKind.Muted);

        try
        {
            var server = EnsureServer();
            var context = new SampleContext(server, Write, cts.Token);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await Task.Run(() => sample.RunAsync(context), cts.Token);
            Write($"✓ Finished in {watch.Elapsed.TotalSeconds:0.00} s", OutputKind.Success);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Write("■ Stopped", OutputKind.Warning);
        }
        catch (Exception ex)
        {
            Write($"✗ {ex.GetType().Name}: {ex.Message}", OutputKind.Error);
        }
        finally
        {
            if (_runCts == cts)
            {
                IsRunning = false;
                _runCts = null;
            }

            cts.Dispose();
        }
    }

    private bool HasSample() => SelectedSample is not null;

    [RelayCommand]
    private void Stop() => _runCts?.Cancel();

    [RelayCommand]
    private void ClearOutput() => Output.Clear();

    [RelayCommand(CanExecute = nameof(HasSample))]
    private async Task CopyCodeAsync()
    {
        if (CopyToClipboard is { } copy && SelectedSample is { } sample)
        {
            await copy(sample.Code);
            Write($"Copied {sample.FileName} to the clipboard.", OutputKind.Muted);
        }
    }

    private MockMumbleServer EnsureServer()
    {
        if (_server is null)
        {
            _server = MockMumbleServer.Start();
            ServerStatus = $"Demo server {_server.Host}:{_server.Port} · EchoBot online in Lobby · native {SafeVersion()}";
        }

        return _server;
    }

    private void Write(string text, OutputKind kind)
    {
        var line = new OutputLine(DateTime.Now, text, kind);
        if (Dispatcher.UIThread.CheckAccess())
        {
            Output.Add(line);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Output.Add(line));
        }
    }

    private static string SafeVersion()
    {
        try
        {
            return RumbleNative.Version;
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _server?.Dispose();
    }
}
