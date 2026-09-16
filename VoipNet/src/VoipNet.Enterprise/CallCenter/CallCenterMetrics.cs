using System.Collections.Concurrent;

namespace VoipNet.Enterprise.CallCenter;

/// <summary>Live figures for one queue.</summary>
/// <param name="Queue">Queue name.</param>
/// <param name="Offered">Calls that entered the queue.</param>
/// <param name="Answered">Calls an agent answered.</param>
/// <param name="Abandoned">Callers who hung up while waiting.</param>
/// <param name="Overflowed">Calls sent to the overflow target.</param>
/// <param name="AverageWait">Mean wait of answered calls.</param>
/// <param name="LongestWait">Longest wait of any call.</param>
/// <param name="AverageTalk">Mean talk time.</param>
/// <param name="ServiceLevel">Share of answered calls within the target, from 0 to 1.</param>
public sealed record QueueSnapshot(
    string Queue,
    int Offered,
    int Answered,
    int Abandoned,
    int Overflowed,
    TimeSpan AverageWait,
    TimeSpan LongestWait,
    TimeSpan AverageTalk,
    double ServiceLevel)
{
    /// <summary>Abandoned calls as a share of offered calls, from 0 to 1.</summary>
    public double AbandonRate => Offered == 0 ? 0 : Abandoned / (double)Offered;
}

/// <summary>Collects queue statistics. Thread safe.</summary>
public sealed class CallCenterMetrics
{
    private readonly ConcurrentDictionary<string, Counters> _queues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever a figure changes, so dashboards can refresh.</summary>
    public event EventHandler? Changed;

    internal void RecordOffered(string queue)
    {
        var c = Get(queue);
        lock (c)
        {
            c.Offered++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RecordOutcome(string queue, QueueResult result, TimeSpan serviceLevelTarget)
    {
        var c = Get(queue);
        lock (c)
        {
            c.LongestWait = result.Waited > c.LongestWait ? result.Waited : c.LongestWait;
            switch (result.Outcome)
            {
                case QueueOutcome.Answered:
                    c.Answered++;
                    c.TotalWait += result.Waited;
                    if (result.Waited <= serviceLevelTarget)
                    {
                        c.WithinTarget++;
                    }

                    break;
                case QueueOutcome.Abandoned:
                    c.Abandoned++;
                    break;
                case QueueOutcome.Overflowed:
                    c.Overflowed++;
                    break;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void RecordTalkTime(string queue, TimeSpan talk)
    {
        var c = Get(queue);
        lock (c)
        {
            c.TotalTalk += talk;
            c.TalkSamples++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Current figures for every queue.</summary>
    public IReadOnlyList<QueueSnapshot> Snapshot() =>
        _queues.Select(pair => ToSnapshot(pair.Key, pair.Value)).OrderBy(s => s.Queue).ToList();

    /// <summary>Current figures for one queue.</summary>
    /// <param name="queue">Queue name.</param>
    public QueueSnapshot Snapshot(string queue) => ToSnapshot(queue, Get(queue));

    /// <summary>Clears every counter, for example at the start of a shift.</summary>
    public void Reset()
    {
        _queues.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Counters Get(string queue) => _queues.GetOrAdd(queue, _ => new Counters());

    private static QueueSnapshot ToSnapshot(string queue, Counters c)
    {
        lock (c)
        {
            return new QueueSnapshot(
                queue,
                c.Offered,
                c.Answered,
                c.Abandoned,
                c.Overflowed,
                c.Answered == 0 ? TimeSpan.Zero : c.TotalWait / c.Answered,
                c.LongestWait,
                c.TalkSamples == 0 ? TimeSpan.Zero : c.TotalTalk / c.TalkSamples,
                c.Answered == 0 ? 1 : c.WithinTarget / (double)c.Answered);
        }
    }

    private sealed class Counters
    {
        public int Offered;
        public int Answered;
        public int Abandoned;
        public int Overflowed;
        public int WithinTarget;
        public int TalkSamples;
        public TimeSpan TotalWait;
        public TimeSpan LongestWait;
        public TimeSpan TotalTalk;
    }
}
