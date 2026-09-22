using VoipNet.Cli;
using Xunit;

namespace VoipNet.Tests;

/// <summary>The load generator behind <c>voipnet load</c>, run against a loopback callee.</summary>
public sealed class LoadTests
{
    [Fact]
    public async Task LoadRunnerPlacesCallsAtTheRequestedRate()
    {
        await using var caller = new VoipClient(TestHelpers.LoopbackOptions("loader"));
        await using var callee = new VoipClient(TestHelpers.LoopbackOptions("target"));
        await caller.StartAsync();
        await callee.StartAsync();
        // The callee answers and talks back, so the report has media to score.
        callee.IncomingCall += (_, e) => _ = Task.Run(async () =>
        {
            await e.Call.AnswerAsync();
            e.Call.SendAudio(TestHelpers.Tone(16000, 1000, 500), 16000);
        });

        var progress = new List<LoadProgress>();
        var plan = new LoadPlan
        {
            Target = $"sip:target@{callee.LocalAddress}",
            Calls = 6,
            Concurrency = 2,
            CallsPerSecond = 8,
            CallDuration = TimeSpan.FromMilliseconds(700),
            SetupTimeout = TimeSpan.FromSeconds(10),
            ToneHz = 440,
        };

        var report = await LoadRunner.RunAsync(caller, plan, p => { lock (progress) progress.Add(p); });

        Assert.Equal(6, report.Attempted);
        Assert.Equal(6, report.Connected);
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Failures);
        // Concurrency is a ceiling, and two calls at 700 ms each keep it there.
        Assert.InRange(report.PeakConcurrency, 1, 2);
        Assert.True(report.SetupMs(0.5) > 0, "setup times were measured");
        Assert.True(report.AverageMos > 3, $"average MOS was {report.AverageMos:F2}");

        lock (progress)
        {
            Assert.NotEmpty(progress);
            Assert.Equal(6, progress[^1].Connected);
        }
    }

    [Fact]
    public async Task CallsNobodyAnswersAreReportedAsFailures()
    {
        await using var caller = new VoipClient(TestHelpers.LoopbackOptions("loader"));
        await using var callee = new VoipClient(TestHelpers.LoopbackOptions("busy"));
        await caller.StartAsync();
        await callee.StartAsync();
        callee.IncomingCall += (_, e) => e.Call.Reject(486);

        var plan = new LoadPlan
        {
            Target = $"sip:busy@{callee.LocalAddress}",
            Calls = 3,
            Concurrency = 3,
            CallsPerSecond = 10,
            CallDuration = TimeSpan.FromMilliseconds(200),
            SetupTimeout = TimeSpan.FromSeconds(5),
            ToneHz = 0,
        };

        var report = await LoadRunner.RunAsync(caller, plan);

        Assert.Equal(3, report.Attempted);
        Assert.Equal(0, report.Connected);
        Assert.Equal(3, report.Failed);
        Assert.Equal(486, report.Failures[0].StatusCode);
    }
}
