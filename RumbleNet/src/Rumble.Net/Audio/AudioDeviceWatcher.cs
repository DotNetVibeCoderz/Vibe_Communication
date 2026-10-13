using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rumble.Net.Models;

namespace Rumble.Net.Audio;

/// <summary>
/// Reports when audio devices appear or disappear, so an app can refresh its device pickers or
/// restart audio after a headset is unplugged.
/// </summary>
/// <remarks>
/// The platform backends do not surface device-change events through cpal, so this polls the
/// device list on an interval (2 seconds by default). Polling is cheap, but changes are seen with
/// up to one interval of delay.
/// </remarks>
public sealed class AudioDeviceWatcher : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private IReadOnlyList<AudioDevice> _devices = [];

    /// <summary>Starts watching.</summary>
    /// <param name="interval">Poll interval; the default is 2 seconds, the minimum 250 ms.</param>
    /// <param name="logger">Optional logger for enumeration failures.</param>
    public AudioDeviceWatcher(TimeSpan? interval = null, ILogger? logger = null)
    {
        _interval = interval is { } value && value > TimeSpan.FromMilliseconds(250)
            ? value
            : TimeSpan.FromSeconds(2);
        _logger = logger ?? NullLogger.Instance;
        _devices = Enumerate();
        _loop = Task.Run(() => WatchAsync(_cts.Token));
    }

    /// <summary>The devices seen at the last poll.</summary>
    public IReadOnlyList<AudioDevice> Devices => _devices;

    /// <summary>Raised on a thread-pool thread when the device list changes.</summary>
    public event EventHandler<AudioDevicesChangedEventArgs>? DevicesChanged;

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var current = Enumerate();
                var previous = _devices;
                if (SameDevices(previous, current))
                {
                    continue;
                }

                _devices = current;
                var added = current.Where(d => !previous.Any(p => p.Id == d.Id)).ToList();
                var removed = previous.Where(p => !current.Any(d => d.Id == p.Id)).ToList();
                try
                {
                    DevicesChanged?.Invoke(this, new AudioDevicesChangedEventArgs(current, added, removed));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An audio device change handler threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private IReadOnlyList<AudioDevice> Enumerate()
    {
        try
        {
            return RumbleNative.GetAudioDevices();
        }
        catch (RumbleException ex)
        {
            // A machine with no audio stack at all (CI containers, servers) keeps reporting none.
            _logger.LogDebug(ex, "Audio devices could not be enumerated");
            return [];
        }
    }

    private static bool SameDevices(IReadOnlyList<AudioDevice> a, IReadOnlyList<AudioDevice> b) =>
        a.Count == b.Count && a.Zip(b).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.IsDefaultInput == pair.Second.IsDefaultInput &&
            pair.First.IsDefaultOutput == pair.Second.IsDefaultOutput);

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop only ever throws cancellation.
        }

        _cts.Dispose();
    }
}

/// <summary>Describes an audio device change.</summary>
/// <param name="Devices">All devices after the change.</param>
/// <param name="Added">Devices that appeared.</param>
/// <param name="Removed">Devices that disappeared.</param>
public sealed record AudioDevicesChangedEventArgs(
    IReadOnlyList<AudioDevice> Devices,
    IReadOnlyList<AudioDevice> Added,
    IReadOnlyList<AudioDevice> Removed);
