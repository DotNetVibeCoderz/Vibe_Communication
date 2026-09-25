using VoipNet.Enterprise.CallCenter;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Opening hours: when a queue takes calls, and what happens when it does not.</summary>
public class ScheduleTests
{
    /// <summary>A fixed +07:00, so the arithmetic is the same on every machine: zone ids depend on what
    /// the operating system knows, and CI runners do not all know the same ones.</summary>
    private static RoutingSchedule OfficeHours() => new()
    {
        TimeZone = "+07:00",
        Hours =
        [
            new OpeningHours(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(17, 0)),
            new OpeningHours(DayOfWeek.Tuesday, new TimeOnly(8, 0), new TimeOnly(17, 0)),
            new OpeningHours(DayOfWeek.Wednesday, new TimeOnly(8, 0), new TimeOnly(17, 0)),
            new OpeningHours(DayOfWeek.Thursday, new TimeOnly(8, 0), new TimeOnly(17, 0)),
            new OpeningHours(DayOfWeek.Friday, new TimeOnly(8, 0), new TimeOnly(17, 0)),
        ],
        ClosedTarget = "sip:voicemail@pbx",
    };

    [Fact]
    public void OfficeHoursAreReadInTheirOwnTimeZone()
    {
        var schedule = OfficeHours();

        // Wednesday 09:00 in Jakarta is 02:00 UTC: open, and the caller is told when it closes.
        var open = schedule.Check(new DateTimeOffset(2026, 3, 4, 2, 0, 0, TimeSpan.Zero));
        Assert.True(open.IsOpen);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero), open.Until!.Value.ToUniversalTime());

        // The same wall-clock hour read as UTC would be 16:00 in Jakarta — still open, so the test
        // below picks a moment that only the time zone can decide: 02:00 UTC on Wednesday is 09:00
        // there, while 23:00 UTC on Wednesday is 06:00 Thursday, before anyone is in.
        var early = schedule.Check(new DateTimeOffset(2026, 3, 4, 23, 0, 0, TimeSpan.Zero));
        Assert.False(early.IsOpen);
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 1, 0, 0, TimeSpan.Zero), early.Until!.Value.ToUniversalTime());
    }

    [Fact]
    public void TheWeekendClosesTheQueueUntilMonday()
    {
        var schedule = OfficeHours();

        // Saturday afternoon in Jakarta.
        var status = schedule.Check(new DateTimeOffset(2026, 3, 7, 7, 0, 0, TimeSpan.Zero));

        Assert.False(status.IsOpen);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 1, 0, 0, TimeSpan.Zero), status.Until!.Value.ToUniversalTime());
    }

    [Fact]
    public void AHolidayClosesADayThatWouldOtherwiseBeOpen()
    {
        var schedule = OfficeHours();
        schedule.Exceptions.Add(new ScheduleException { Date = new DateOnly(2026, 3, 4), Reason = "Nyepi" });

        var status = schedule.Check(new DateTimeOffset(2026, 3, 4, 2, 0, 0, TimeSpan.Zero));

        Assert.False(status.IsOpen);
        Assert.Equal("Nyepi", status.Reason);
        // Back to normal on Thursday morning.
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 1, 0, 0, TimeSpan.Zero), status.Until!.Value.ToUniversalTime());
    }

    [Fact]
    public void AnExceptionCanShortenADayInsteadOfClosingIt()
    {
        var schedule = OfficeHours();
        schedule.Exceptions.Add(new ScheduleException
        {
            Date = new DateOnly(2026, 3, 4),
            Reason = "Christmas Eve hours",
            Hours = [new OpeningHours(DayOfWeek.Wednesday, new TimeOnly(8, 0), new TimeOnly(12, 0))],
        });

        Assert.True(schedule.IsOpen(new DateTimeOffset(2026, 3, 4, 2, 0, 0, TimeSpan.Zero)));      // 09:00
        Assert.False(schedule.IsOpen(new DateTimeOffset(2026, 3, 4, 6, 0, 0, TimeSpan.Zero)));     // 13:00
    }

    [Fact]
    public void APeriodThatCrossesMidnightStaysOpenIntoTheNextDay()
    {
        var schedule = new RoutingSchedule
        {
            TimeZone = "UTC",
            Hours = [new OpeningHours(DayOfWeek.Friday, new TimeOnly(22, 0), new TimeOnly(2, 0))],
        };

        Assert.True(schedule.IsOpen(new DateTimeOffset(2026, 3, 6, 23, 30, 0, TimeSpan.Zero)));    // Friday night
        Assert.True(schedule.IsOpen(new DateTimeOffset(2026, 3, 7, 1, 30, 0, TimeSpan.Zero)));     // Saturday morning
        Assert.False(schedule.IsOpen(new DateTimeOffset(2026, 3, 7, 2, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AnUnknownTimeZoneFallsBackToUtcRatherThanGuessing()
    {
        var schedule = new RoutingSchedule
        {
            TimeZone = "Mars/Olympus",
            Hours = [new OpeningHours(DayOfWeek.Wednesday, new TimeOnly(9, 0), new TimeOnly(17, 0))],
        };

        // 10:00 UTC on Wednesday: open, because the hours are read as UTC when the id means nothing here.
        Assert.True(schedule.IsOpen(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero)));
        Assert.False(schedule.IsOpen(new DateTimeOffset(2026, 3, 4, 20, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void AScheduleWithNoHoursIsAlwaysOpen()
    {
        var always = new RoutingSchedule();

        var status = always.Check(new DateTimeOffset(2026, 3, 7, 3, 0, 0, TimeSpan.Zero));

        Assert.True(status.IsOpen);
        Assert.Null(status.Until);
    }

    [Fact]
    public void WeekdaysBuildsTheOrdinaryWorkingWeek()
    {
        var schedule = RoutingSchedule.Weekdays(new TimeOnly(9, 0), new TimeOnly(17, 30));

        Assert.Equal(5, schedule.Hours.Count);
        Assert.DoesNotContain(schedule.Hours, h => h.Day is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.True(schedule.IsOpen(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero)));
        Assert.False(schedule.IsOpen(new DateTimeOffset(2026, 3, 4, 18, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task ACallerReachingAClosedQueueIsNotLeftWaiting()
    {
        await using var pair = await LoopbackPair.ConnectAsync("customer", "pbx");
        await using var center = new CallCenterService(pair.Callee);
        center.AddQueue(new CallQueueOptions
        {
            Name = "support",
            AnnouncePosition = false,
            // Closed today and tomorrow, so the moment the test runs does not matter.
            Schedule = new RoutingSchedule
            {
                Exceptions =
                [
                    new ScheduleException { Date = DateOnly.FromDateTime(DateTime.UtcNow), Reason = "stock-take" },
                    new ScheduleException { Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), Reason = "stock-take" },
                ],
            },
        });

        var result = await center.EnqueueAsync(pair.CalleeLeg, "support").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(QueueOutcome.Closed, result.Outcome);
        Assert.Empty(center.Waiting("support"));
        await pair.CallerLeg.HangupAsync();
    }
}
