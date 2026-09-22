using System.Diagnostics;

namespace VoipNet.Cli;

/// <summary>How a load run is shaped: how many calls, how fast they arrive and how long they last.</summary>
public sealed record LoadPlan
{
    /// <summary>Who to call: SIP URI or extension.</summary>
    public required string Target { get; init; }

    /// <summary>Calls to place in total.</summary>
    public int Calls { get; init; } = 10;

    /// <summary>Calls allowed to be up at the same time.</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>New calls per second, which is the rate a switch is usually rated by.</summary>
    public double CallsPerSecond { get; init; } = 2;

    /// <summary>How long each call stays connected.</summary>
    public TimeSpan CallDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a call may take to connect before it counts as failed.</summary>
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Test tone to send while connected, in Hz; 0 sends silence.</summary>
    public int ToneHz { get; init; } = 440;
}

/// <summary>What one call did.</summary>
/// <param name="Connected">True when the call reached the connected state.</param>
/// <param name="SetupMs">Time from the INVITE to the answer.</param>
/// <param name="StatusCode">Final SIP status code, 0 when the call never got one.</param>
/// <param name="Reason">Why the call failed, empty when it did not.</param>
/// <param name="Mos">Estimated MOS when the call ended, 0 when there was no media.</param>
/// <param name="LossPercent">Packet loss when the call ended.</param>
public readonly record struct LoadCallResult(bool Connected, double SetupMs, int StatusCode, string Reason, double Mos, double LossPercent);

/// <summary>How far along a run is, for live output.</summary>
/// <param name="Placed">Calls placed so far.</param>
/// <param name="Active">Calls currently up.</param>
/// <param name="Connected">Calls that connected.</param>
/// <param name="Failed">Calls that failed.</param>
/// <param name="Elapsed">Time since the run started.</param>
public readonly record struct LoadProgress(int Placed, int Active, int Connected, int Failed, TimeSpan Elapsed);

/// <summary>The result of a whole run.</summary>
public sealed class LoadReport
{
    /// <summary>Every call, in the order they finished.</summary>
    public required IReadOnlyList<LoadCallResult> Calls { get; init; }

    /// <summary>How long the run took, from the first call placed to the last one ended.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Most calls that were up at the same time.</summary>
    public required int PeakConcurrency { get; init; }

    public int Attempted => Calls.Count;

    public int Connected => Calls.Count(c => c.Connected);

    public int Failed => Attempted - Connected;

    /// <summary>Calls placed per second over the whole run. Concurrency and call duration cap this, so
    /// it is what the far end actually saw rather than the rate that was asked for.</summary>
    public double CallsPerSecond => Elapsed.TotalSeconds > 0 ? Attempted / Elapsed.TotalSeconds : 0;

    /// <summary>Failures grouped by SIP status code, worst first.</summary>
    public IReadOnlyList<(int StatusCode, string Reason, int Count)> Failures =>
        Calls.Where(c => !c.Connected)
            .GroupBy(c => (c.StatusCode, c.Reason))
            .Select(g => (g.Key.StatusCode, g.Key.Reason, g.Count()))
            .OrderByDescending(g => g.Item3)
            .ToArray();

    /// <summary>Setup time at a percentile between 0 and 1, in milliseconds.</summary>
    public double SetupMs(double percentile)
    {
        var times = Calls.Where(c => c.Connected).Select(c => c.SetupMs).OrderBy(t => t).ToArray();
        if (times.Length == 0)
        {
            return 0;
        }

        var index = (int)Math.Clamp(Math.Round(percentile * (times.Length - 1)), 0, times.Length - 1);
        return times[index];
    }

    /// <summary>Average MOS of the calls that carried media.</summary>
    public double AverageMos
    {
        get
        {
            var scored = Calls.Where(c => c.Mos > 0).Select(c => c.Mos).ToArray();
            return scored.Length == 0 ? 0 : scored.Average();
        }
    }

    /// <summary>Average packet loss of the calls that connected.</summary>
    public double AverageLossPercent
    {
        get
        {
            var connected = Calls.Where(c => c.Connected).Select(c => c.LossPercent).ToArray();
            return connected.Length == 0 ? 0 : connected.Average();
        }
    }
}

/// <summary>
/// Places calls at a steady rate and reports what the far end did with them: how many connected, how
/// long they took to answer and what the media sounded like. One client places every call, which is
/// how a trunk sees a busy PBX.
/// </summary>
public static class LoadRunner
{
    /// <summary>Runs a load plan against an already started client.</summary>
    /// <param name="client">Client to place the calls with.</param>
    /// <param name="plan">Shape of the run.</param>
    /// <param name="onProgress">Called as calls start and finish, for live output.</param>
    /// <param name="cancellationToken">Stops placing calls and hangs up what is still connected.</param>
    public static async Task<LoadReport> RunAsync(
        VoipClient client,
        LoadPlan plan,
        Action<LoadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(plan);

        var results = new List<LoadCallResult>(Math.Max(plan.Calls, 4));
        var inFlight = new SemaphoreSlim(Math.Max(plan.Concurrency, 1));
        var running = new List<Task>();
        var started = Stopwatch.StartNew();
        var interval = plan.CallsPerSecond > 0 ? TimeSpan.FromSeconds(1 / plan.CallsPerSecond) : TimeSpan.Zero;
        var placed = 0;
        var active = 0;
        var peak = 0;
        var connected = 0;
        var failed = 0;

        void Report() => onProgress?.Invoke(new LoadProgress(
            Volatile.Read(ref placed),
            Volatile.Read(ref active),
            Volatile.Read(ref connected),
            Volatile.Read(ref failed),
            started.Elapsed));

        for (var i = 0; i < plan.Calls && !cancellationToken.IsCancellationRequested; i++)
        {
            await inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
            // Calls go out on the clock, not as fast as the semaphore lets them: a load test is about
            // the rate the far end is asked to handle.
            var due = interval * i;
            if (due > started.Elapsed)
            {
                await Task.Delay(due - started.Elapsed, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref placed);
            var now = Interlocked.Increment(ref active);
            peak = Math.Max(peak, now);
            Report();

            running.Add(Task.Run(async () =>
            {
                var result = await PlaceOneAsync(client, plan, cancellationToken).ConfigureAwait(false);
                lock (results)
                {
                    results.Add(result);
                }

                Interlocked.Decrement(ref active);
                if (result.Connected)
                {
                    Interlocked.Increment(ref connected);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }

                inFlight.Release();
                Report();
            }, CancellationToken.None));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
        started.Stop();
        Report();
        lock (results)
        {
            return new LoadReport { Calls = results.ToArray(), Elapsed = started.Elapsed, PeakConcurrency = peak };
        }
    }

    private static async Task<LoadCallResult> PlaceOneAsync(VoipClient client, LoadPlan plan, CancellationToken cancellationToken)
    {
        var setup = Stopwatch.StartNew();
        VoipCall? call = null;
        try
        {
            call = client.Call(plan.Target);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(plan.SetupTimeout);
            await call.Connected.WaitAsync(timeout.Token).ConfigureAwait(false);
            setup.Stop();

            if (plan.ToneHz > 0)
            {
                call.SendAudio(Tone(plan.ToneHz, plan.CallDuration), 16000);
            }

            await Task.Delay(plan.CallDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await EndedAsync(call, false, setup, 408, "setup timed out").ConfigureAwait(false);
        }
        catch (VoipException ex)
        {
            return await EndedAsync(call, false, setup, call?.LastStatusCode ?? 0, ex.Message).ConfigureAwait(false);
        }

        try
        {
            await call.HangupAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (VoipException)
        {
            // The far end hung up first, which is a perfectly good ending.
        }

        return await EndedAsync(call, true, setup, call.LastStatusCode, string.Empty).ConfigureAwait(false);
    }

    /// <summary>Reads the quality off a finished call, waiting briefly for the engine's final numbers.</summary>
    private static async Task<LoadCallResult> EndedAsync(VoipCall? call, bool connected, Stopwatch setup, int statusCode, string reason)
    {
        if (call is null)
        {
            return new LoadCallResult(false, setup.Elapsed.TotalMilliseconds, statusCode, reason, 0, 0);
        }

        if (!connected && call.IsActive)
        {
            call.Hangup();
        }

        // The engine reports the last statistics with the terminated event, which can land just after
        // the hangup returns.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (call.FinalStatistics is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20).ConfigureAwait(false);
        }

        var stats = call.FinalStatistics;
        return new LoadCallResult(
            connected,
            setup.Elapsed.TotalMilliseconds,
            statusCode,
            reason,
            stats?.Mos ?? 0,
            stats?.LossPercent ?? 0);
    }

    private static short[] Tone(int frequency, TimeSpan duration)
    {
        var samples = new short[(int)(16000 * Math.Clamp(duration.TotalSeconds, 0.1, 60))];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * frequency * i / 16000));
        }

        return samples;
    }
}
