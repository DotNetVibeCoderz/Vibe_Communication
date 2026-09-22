using System.Globalization;
using System.Text;

namespace VoipNet.Enterprise.CallCenter;

/// <summary>One finished queue call, as the history remembers it.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="QueueName">Queue the caller waited in.</param>
/// <param name="RemoteUri">The caller.</param>
/// <param name="AgentId">Agent who answered, or null.</param>
/// <param name="Outcome">How the queue call ended.</param>
/// <param name="EnqueuedAt">When the caller joined the queue.</param>
/// <param name="Waited">How long they waited.</param>
/// <param name="Talked">How long they spoke to an agent.</param>
/// <param name="Node">The node that handled it.</param>
public sealed record CallRecord(
    string Id,
    string QueueName,
    string RemoteUri,
    string? AgentId,
    QueueOutcome Outcome,
    DateTimeOffset EnqueuedAt,
    TimeSpan Waited,
    TimeSpan Talked,
    string Node);

/// <summary>Figures for one queue over one stretch of time.</summary>
/// <param name="Queue">Queue name.</param>
/// <param name="Start">Start of the interval.</param>
/// <param name="Offered">Calls that entered the queue.</param>
/// <param name="Answered">Calls an agent answered.</param>
/// <param name="Abandoned">Callers who hung up while waiting.</param>
/// <param name="Overflowed">Calls sent somewhere else.</param>
/// <param name="AverageWait">Mean wait of the calls that were answered.</param>
/// <param name="LongestWait">Longest wait of any call.</param>
/// <param name="AverageTalk">Mean talk time.</param>
/// <param name="ServiceLevel">Share answered within the target, from 0 to 1.</param>
public sealed record ReportRow(
    string Queue,
    DateTimeOffset Start,
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

/// <summary>
/// Turns stored calls into the rows a workforce report is made of: per queue, per interval, with the
/// figures a supervisor plans staffing from. Everything is derived from the records, so the same
/// numbers come out whether they are read from a database or from a file.
/// </summary>
public static class WorkforceReport
{
    /// <summary>Groups calls into intervals and summarises each one.</summary>
    /// <param name="records">Finished calls, in any order.</param>
    /// <param name="interval">Length of each reporting interval; half an hour is the usual choice.</param>
    /// <param name="serviceLevelTarget">Answer time the service level measures against.</param>
    public static IReadOnlyList<ReportRow> Summarize(
        IEnumerable<CallRecord> records,
        TimeSpan interval,
        TimeSpan serviceLevelTarget)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "An interval has to have a length.");
        }

        return records
            .GroupBy(r => (r.QueueName, Start: Floor(r.EnqueuedAt, interval)))
            .OrderBy(g => g.Key.Start)
            .ThenBy(g => g.Key.QueueName, StringComparer.Ordinal)
            .Select(group =>
            {
                var answered = group.Where(r => r.Outcome == QueueOutcome.Answered).ToArray();
                return new ReportRow(
                    group.Key.QueueName,
                    group.Key.Start,
                    group.Count(),
                    answered.Length,
                    group.Count(r => r.Outcome == QueueOutcome.Abandoned),
                    group.Count(r => r.Outcome == QueueOutcome.Overflowed),
                    Average(answered.Select(r => r.Waited)),
                    group.Select(r => r.Waited).DefaultIfEmpty(TimeSpan.Zero).Max(),
                    Average(answered.Select(r => r.Talked)),
                    answered.Length == 0 ? 0 : answered.Count(r => r.Waited <= serviceLevelTarget) / (double)answered.Length);
            })
            .ToArray();
    }

    /// <summary>How much of their signed-in time each agent spent talking, and how many calls they took.</summary>
    /// <param name="records">Finished calls.</param>
    public static IReadOnlyList<(string AgentId, int Calls, TimeSpan TalkTime, TimeSpan AverageTalk)> ByAgent(IEnumerable<CallRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .Where(r => r.AgentId is { Length: > 0 })
            .GroupBy(r => r.AgentId!)
            .OrderByDescending(g => g.Sum(r => r.Talked.Ticks))
            .Select(g =>
            {
                var talk = TimeSpan.FromTicks(g.Sum(r => r.Talked.Ticks));
                return (g.Key, g.Count(), talk, TimeSpan.FromTicks(talk.Ticks / g.Count()));
            })
            .ToArray();
    }

    /// <summary>Writes report rows as CSV, for a spreadsheet or a warehouse load.</summary>
    /// <param name="rows">Rows to write.</param>
    public static string ToCsv(IEnumerable<ReportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var csv = new StringBuilder("queue,interval_start,offered,answered,abandoned,overflowed,average_wait_seconds,longest_wait_seconds,average_talk_seconds,service_level,abandon_rate\n");
        foreach (var row in rows)
        {
            csv.Append(Escape(row.Queue)).Append(',')
                .Append(row.Start.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Offered).Append(',')
                .Append(row.Answered).Append(',')
                .Append(row.Abandoned).Append(',')
                .Append(row.Overflowed).Append(',')
                .Append(Seconds(row.AverageWait)).Append(',')
                .Append(Seconds(row.LongestWait)).Append(',')
                .Append(Seconds(row.AverageTalk)).Append(',')
                .Append(row.ServiceLevel.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.AbandonRate.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }

        return csv.ToString();
    }

    private static DateTimeOffset Floor(DateTimeOffset at, TimeSpan interval) =>
        new(at.Ticks - (at.Ticks % interval.Ticks), at.Offset);

    private static TimeSpan Average(IEnumerable<TimeSpan> values)
    {
        var list = values.ToArray();
        return list.Length == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(list.Sum(v => v.Ticks) / list.Length);
    }

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Quotes a field when it holds something CSV would otherwise misread.</summary>
    private static string Escape(string value) =>
        value.AsSpan().IndexOfAny(",\"\n") >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
}
