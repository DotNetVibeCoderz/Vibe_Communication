using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoipNet.Audio;
using VoipNet.Samples.Theme;

namespace VoipNet.Gallery.Infrastructure;

/// <summary>A button on a demo page.</summary>
/// <param name="Label">What the button does, in the user's words.</param>
/// <param name="Command">The action.</param>
/// <param name="Style">primary, go, danger or quiet.</param>
public sealed record DemoAction(string Label, IAsyncRelayCommand Command, string Style = "primary")
{
    public bool IsPrimary => Style == "primary";

    public bool IsGo => Style == "go";

    public bool IsDanger => Style == "danger";

    public bool IsQuiet => Style == "quiet";
}

/// <summary>A labelled figure shown on a demo page.</summary>
public sealed partial class DemoMetric(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Value { get; set; } = "—";
}

/// <summary>
/// One gallery page: a live demo of an SDK feature with its code. Pages run the real engine on the
/// loopback interface, so everything shown actually happens.
/// </summary>
public abstract partial class DemoPage : ObservableObject
{
    private readonly LevelHistory _levels = new(160);
    private readonly Dictionary<string, DemoMetric> _metricIndex = [];

    protected DemoPage(string section, string title, string summary)
    {
        Section = section;
        Title = title;
        Summary = summary;
    }

    public string Section { get; }

    public string Title { get; }

    public string Summary { get; }

    /// <summary>C# that does what the demo does.</summary>
    public abstract string Code { get; }

    public ObservableCollection<DemoAction> Actions { get; } = [];

    public ObservableCollection<DemoMetric> Metrics { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    public partial double[] Levels { get; set; } = [];

    [ObservableProperty]
    public partial bool ShowTrace { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasMetrics => Metrics.Count > 0;

    /// <summary>Extra input shown above the actions (prompt, endpoint…), when a page needs it.</summary>
    public virtual bool HasInput => false;

    protected void AddAction(string label, Func<Task> run, string style = "primary") =>
        Actions.Add(new DemoAction(label, new AsyncRelayCommand(() => Guard(run)), style));

    protected void SetMetric(string name, string value)
    {
        if (!_metricIndex.TryGetValue(name, out var metric))
        {
            metric = new DemoMetric(name);
            _metricIndex[name] = metric;
            Metrics.Add(metric);
            OnPropertyChanged(nameof(HasMetrics));
        }

        metric.Value = value;
    }

    protected void Write(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        while (Log.Count > 200)
        {
            Log.RemoveAt(0);
        }
    }

    /// <summary>Feeds the page's trace line from a call's audio.</summary>
    protected void TraceAudio(VoipCall call, AudioDirection direction = AudioDirection.Inbound)
    {
        ShowTrace = true;
        call.AudioReceived += (_, dir, _, samples) =>
        {
            if (dir == direction)
            {
                _levels.Push(Pcm.Rms(samples));
            }
        };
    }

    /// <summary>Called by the shell about twelve times a second while the page is visible.</summary>
    public virtual void Tick()
    {
        if (ShowTrace)
        {
            Levels = _levels.Snapshot();
        }
    }

    /// <summary>Releases anything the page started.</summary>
    public virtual Task ResetAsync() => Task.CompletedTask;

    private async Task Guard(Func<Task> run)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            Write($"✗ {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Fills a buffer with a short synthetic voice so demos have something to hear.</summary>
    protected static short[] Voice(int milliseconds, double pitch = 190)
    {
        var samples = new short[16 * milliseconds];
        for (var i = 0; i < samples.Length; i++)
        {
            var syllable = Math.Max(0, Math.Sin(Math.PI * i / 4200.0));
            samples[i] = (short)(7000 * syllable * Math.Sin(2 * Math.PI * (pitch + (35 * Math.Sin(i / 700.0))) * i / 16000));
        }

        return samples;
    }
}
