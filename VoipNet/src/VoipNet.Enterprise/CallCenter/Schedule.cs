using System.Globalization;

namespace VoipNet.Enterprise.CallCenter;

/// <summary>
/// One opening period on a day of the week, read in the schedule's own time zone. A period whose
/// closing time is at or before its opening time runs past midnight into the following day, so
/// <c>(Friday, 22:00, 02:00)</c> keeps the queue open until two on Saturday morning.
/// </summary>
/// <param name="Day">The day the period starts on.</param>
/// <param name="Opens">When the queue starts taking calls.</param>
/// <param name="Closes">When it stops.</param>
public readonly record struct OpeningHours(DayOfWeek Day, TimeOnly Opens, TimeOnly Closes)
{
    /// <summary>The whole day, for a queue that runs around the clock on that day.</summary>
    /// <param name="day">The day.</param>
    public static OpeningHours AllDay(DayOfWeek day) => new(day, TimeOnly.MinValue, TimeOnly.MaxValue);

    /// <summary>How long the period lasts, counting past midnight when it crosses it.</summary>
    public TimeSpan Length => Closes > Opens ? Closes - Opens : TimeSpan.FromDays(1) - (Opens - Closes);
}

/// <summary>
/// A date that does not follow the weekly pattern: a public holiday, or a day with different hours.
/// </summary>
public sealed class ScheduleException
{
    /// <summary>The date, in the schedule's time zone.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>Why the day is different, for logs and announcements ("Idul Fitri").</summary>
    public string? Reason { get; init; }

    /// <summary>Hours to use on that date instead of the weekly ones; empty means closed all day.
    /// The <see cref="OpeningHours.Day"/> of each period is ignored.</summary>
    public IList<OpeningHours> Hours { get; init; } = [];
}

/// <summary>What a schedule says about one moment.</summary>
/// <param name="IsOpen">True when the queue takes calls.</param>
/// <param name="Until">When the state changes: the closing time when open, the next opening when
/// closed. Null when nothing is scheduled to change within a fortnight.</param>
/// <param name="Reason">Why the day is out of the ordinary, from the matching exception.</param>
public readonly record struct ScheduleStatus(bool IsOpen, DateTimeOffset? Until, string? Reason);

/// <summary>
/// When a queue takes calls, and where callers go when it does not. Attach one to
/// <see cref="CallQueueOptions.Schedule"/> and callers arriving out of hours are sent to
/// <see cref="ClosedTarget"/> instead of waiting for an agent who is not there.
/// </summary>
public sealed class RoutingSchedule
{
    /// <summary>How far ahead <see cref="Check"/> looks for the next opening.</summary>
    private static readonly int LookaheadDays = 14;

    /// <summary>The time zone the hours are written in: an IANA id (<c>Asia/Jakarta</c>), a Windows id,
    /// or a fixed offset (<c>+07:00</c>). Zone ids depend on what the operating system knows, so an
    /// offset is the portable way to write a schedule down in a configuration file. An id this machine
    /// does not know is read as UTC.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>The ordinary week. An empty list means the queue is always open.</summary>
    public IList<OpeningHours> Hours { get; init; } = [];

    /// <summary>Dates that override the weekly pattern.</summary>
    public IList<ScheduleException> Exceptions { get; init; } = [];

    /// <summary>Where callers go while the queue is closed — a voicemail box, or another queue.
    /// Without one the call is simply not queued.</summary>
    public string? ClosedTarget { get; init; }

    /// <summary>Opening hours Monday to Friday, in one time zone.</summary>
    /// <param name="opens">When each weekday starts.</param>
    /// <param name="closes">When each weekday ends.</param>
    /// <param name="timeZone">The time zone the hours are written in.</param>
    public static RoutingSchedule Weekdays(TimeOnly opens, TimeOnly closes, string timeZone = "UTC") => new()
    {
        TimeZone = timeZone,
        Hours = [.. Enumerable.Range(1, 5).Select(d => new OpeningHours((DayOfWeek)d, opens, closes))],
    };

    /// <summary>True when the queue takes calls at that moment.</summary>
    /// <param name="when">The moment to check; now when omitted.</param>
    public bool IsOpen(DateTimeOffset? when = null) => Check(when ?? DateTimeOffset.UtcNow).IsOpen;

    /// <summary>Reads the schedule at one moment: open or closed, why, and when that changes.</summary>
    /// <param name="when">The moment to check.</param>
    public ScheduleStatus Check(DateTimeOffset when)
    {
        if (Hours.Count == 0 && Exceptions.Count == 0)
        {
            return new ScheduleStatus(true, null, null);
        }

        var zone = Zone();
        var local = TimeZoneInfo.ConvertTime(when, zone).DateTime;
        var today = DateOnly.FromDateTime(local);

        // Yesterday's periods are checked too, since one of them may run past midnight into today.
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            foreach (var (start, end, reason) in PeriodsOn(day))
            {
                if (local >= start && local < end)
                {
                    return new ScheduleStatus(true, ToOffset(end, zone), reason);
                }
            }
        }

        // Closed: find the next period that starts after now, and say why today is unusual.
        var closedReason = ExceptionOn(today)?.Reason;
        for (var ahead = 0; ahead <= LookaheadDays; ahead++)
        {
            foreach (var (start, _, _) in PeriodsOn(today.AddDays(ahead)))
            {
                if (start > local)
                {
                    return new ScheduleStatus(false, ToOffset(start, zone), closedReason);
                }
            }
        }

        return new ScheduleStatus(false, null, closedReason);
    }

    /// <summary>Every opening period that starts on one date, in order, as local date and time.</summary>
    private IEnumerable<(DateTime Start, DateTime End, string? Reason)> PeriodsOn(DateOnly date)
    {
        var exception = ExceptionOn(date);
        var hours = exception is not null ? exception.Hours : Hours.Where(h => h.Day == date.DayOfWeek);
        foreach (var period in hours.OrderBy(h => h.Opens))
        {
            var start = date.ToDateTime(period.Opens);
            var end = date.ToDateTime(period.Closes);
            if (end <= start)
            {
                // Runs past midnight: it ends on the following day.
                end = end.AddDays(1);
            }

            yield return (start, end, exception?.Reason);
        }
    }

    private ScheduleException? ExceptionOn(DateOnly date) => Exceptions.FirstOrDefault(e => e.Date == date);

    private TimeZoneInfo Zone()
    {
        var id = TimeZone?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return TimeZoneInfo.Utc;
        }

        if (Offset(id) is { } offset)
        {
            var name = $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration().Hours:00}:{offset.Duration().Minutes:00}";
            return TimeZoneInfo.CreateCustomTimeZone(name, offset, name, name);
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // An id this machine does not know must not silently move the hours by a random amount:
            // fall back to UTC, which is what they mean when nobody has said otherwise.
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Reads a fixed offset such as <c>+07:00</c>, <c>-05:30</c> or <c>+07</c>.</summary>
    private static TimeSpan? Offset(string id)
    {
        if (id.Length < 2 || (id[0] != '+' && id[0] != '-'))
        {
            return null;
        }

        var body = id[1..];
        var value = TimeSpan.Zero;
        var parsed = body.Contains(':')
            ? TimeSpan.TryParseExact(body, @"h\:mm", CultureInfo.InvariantCulture, out value)
            : int.TryParse(body, CultureInfo.InvariantCulture, out var hours) && hours is >= 0 and <= 14 && Set(hours, ref value);
        return parsed ? id[0] == '-' ? -value : value : null;

        static bool Set(int hours, ref TimeSpan value)
        {
            value = TimeSpan.FromHours(hours);
            return true;
        }
    }

    /// <summary>Turns a local wall-clock time into an instant, stepping over a daylight-saving gap.</summary>
    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
