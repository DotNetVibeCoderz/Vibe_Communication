using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.Audio;

namespace VoipNet.Enterprise.Recording;

/// <summary>Details of a stored recording.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="CallId">Engine call identifier.</param>
/// <param name="RemoteUri">The other party.</param>
/// <param name="Outgoing">Whether this endpoint placed the call.</param>
/// <param name="Path">Audio file.</param>
/// <param name="Format">Container.</param>
/// <param name="StartedAt">When recording started.</param>
/// <param name="Duration">Recorded length.</param>
/// <param name="Tags">Free-form labels such as queue or agent.</param>
public sealed record RecordingInfo(
    string Id,
    ulong CallId,
    string RemoteUri,
    bool Outgoing,
    string Path,
    RecordingFormat Format,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Settings for <see cref="RecordingService"/>.</summary>
public sealed class RecordingOptions
{
    /// <summary>Where recordings are stored. Files are grouped by date.</summary>
    public string Directory { get; set; } = "recordings";

    /// <summary>Container to write.</summary>
    public RecordingFormat Format { get; set; } = RecordingFormat.Mp3;

    /// <summary>Keep caller and agent on separate channels, which helps quality review and transcription.</summary>
    public RecordingLayout Layout { get; set; } = RecordingLayout.Stereo;

    /// <summary>Start recording every connected call automatically.</summary>
    public bool RecordAllCalls { get; set; } = true;

    /// <summary>Delete recordings older than this. Zero keeps them forever.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.Zero;
}

/// <summary>
/// Records calls and keeps an index of the recordings next to the audio files, so a dashboard can
/// list and play them without a database.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly VoipClient _client;
    private readonly RecordingOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, (CallRecorder Recorder, RecordingInfo Info)> _active = new();

    /// <summary>Creates the service and, if configured, starts recording new calls.</summary>
    /// <param name="client">Client whose calls are recorded.</param>
    /// <param name="options">Recording settings.</param>
    /// <param name="logger">Optional logger.</param>
    public RecordingService(VoipClient client, RecordingOptions? options = null, ILogger<RecordingService>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new RecordingOptions();
        _logger = logger ?? NullLogger<RecordingService>.Instance;
        System.IO.Directory.CreateDirectory(_options.Directory);
        _client.CallStateChanged += OnCallStateChanged;
    }

    /// <summary>Raised when a recording has been finalised.</summary>
    public event EventHandler<RecordingInfo>? RecordingSaved;

    /// <summary>Calls currently being recorded.</summary>
    public IReadOnlyCollection<RecordingInfo> Active => _active.Values.Select(v => v.Info).ToArray();

    /// <summary>Starts recording a call, if it is not already being recorded.</summary>
    /// <param name="call">The call.</param>
    /// <param name="tags">Labels stored with the recording.</param>
    public RecordingInfo Start(VoipCall call, IReadOnlyDictionary<string, string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (_active.TryGetValue(call.Id, out var existing))
        {
            return existing.Info;
        }

        var started = DateTimeOffset.UtcNow;
        var folder = Path.Combine(_options.Directory, started.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        System.IO.Directory.CreateDirectory(folder);
        var id = $"{started:HHmmss}-{call.Id}-{Guid.NewGuid().ToString("N")[..6]}";
        var extension = _options.Format == RecordingFormat.Mp3 ? ".mp3" : ".wav";
        var recorder = CallRecorder.Start(call, Path.Combine(folder, id + extension), _options.Format, _options.Layout, _logger);
        var info = new RecordingInfo(id, call.Id, call.RemoteUri, call.IsOutgoing, recorder.Path, recorder.Format, started, TimeSpan.Zero, tags ?? new Dictionary<string, string>());
        _active[call.Id] = (recorder, info);
        return info;
    }

    /// <summary>Stops recording a call and writes its index entry.</summary>
    /// <param name="call">The call.</param>
    public RecordingInfo? Stop(VoipCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return Stop(call.Id);
    }

    private RecordingInfo? Stop(ulong callId)
    {
        if (!_active.TryRemove(callId, out var entry))
        {
            return null;
        }

        entry.Recorder.Dispose();
        var info = entry.Info with { Duration = entry.Recorder.Duration, Path = entry.Recorder.Path, Format = entry.Recorder.Format };
        try
        {
            File.WriteAllText(Path.ChangeExtension(info.Path, ".json"), JsonSerializer.Serialize(info, Json));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Cannot write the index for recording {Id}", info.Id);
        }

        RecordingSaved?.Invoke(this, info);
        return info;
    }

    /// <summary>Lists stored recordings, newest first.</summary>
    /// <param name="take">Maximum number of entries.</param>
    public IReadOnlyList<RecordingInfo> List(int take = 200)
    {
        if (!System.IO.Directory.Exists(_options.Directory))
        {
            return [];
        }

        return System.IO.Directory
            .EnumerateFiles(_options.Directory, "*.json", SearchOption.AllDirectories)
            .Select(file =>
            {
                try
                {
                    return JsonSerializer.Deserialize<RecordingInfo>(File.ReadAllText(file), Json);
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    return null;
                }
            })
            .OfType<RecordingInfo>()
            .OrderByDescending(r => r.StartedAt)
            .Take(take)
            .ToList();
    }

    /// <summary>Deletes recordings older than the retention period.</summary>
    /// <returns>Number of recordings removed.</returns>
    public int ApplyRetention()
    {
        if (_options.Retention <= TimeSpan.Zero)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow - _options.Retention;
        var removed = 0;
        foreach (var info in List(int.MaxValue).Where(r => r.StartedAt < cutoff))
        {
            try
            {
                File.Delete(info.Path);
                File.Delete(Path.ChangeExtension(info.Path, ".json"));
                removed++;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Cannot delete recording {Id}", info.Id);
            }
        }

        return removed;
    }

    private void OnCallStateChanged(object? sender, CallStateEventArgs e)
    {
        if (e.State == CallState.Connected && _options.RecordAllCalls)
        {
            Start(e.Call);
        }
        else if (e.State == CallState.Terminated)
        {
            Stop(e.Call);
        }
    }

    /// <summary>Stops every active recording and detaches from the client.</summary>
    public void Dispose()
    {
        _client.CallStateChanged -= OnCallStateChanged;
        foreach (var callId in _active.Keys.ToArray())
        {
            Stop(callId);
        }
    }
}
