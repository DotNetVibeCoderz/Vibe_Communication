using System.CommandLine;
using Spectre.Console;

namespace VoipNet.Cli;

/// <summary>The <c>voipnet load</c> command: place many calls and report what the far end did.</summary>
internal static class LoadCommands
{
    public static Command Create()
    {
        var account = new AccountOptions();
        var target = new Argument<string>("target") { Description = "Who to call: SIP URI or extension." };
        var calls = new Option<int>("--calls", "-n") { Description = "Calls to place in total.", DefaultValueFactory = _ => 20 };
        var concurrency = new Option<int>("--concurrency") { Description = "Calls allowed up at the same time.", DefaultValueFactory = _ => 4 };
        var cps = new Option<double>("--cps") { Description = "New calls per second.", DefaultValueFactory = _ => 2 };
        var duration = new Option<int>("--duration") { Description = "Seconds each call stays connected.", DefaultValueFactory = _ => 5 };
        var tone = new Option<int>("--tone") { Description = "Test tone to send while connected (Hz); 0 sends silence.", DefaultValueFactory = _ => 440 };
        var register = new Option<bool>("--register") { Description = "Register before placing the calls." };
        var command = new Command("load", "Place calls at a steady rate and report setup times and media quality.")
        {
            target, calls, concurrency, cps, duration, tone, register,
        };
        account.AddTo(command);

        command.SetAction(async (result, cancellationToken) =>
        {
            var (client, capture) = account.CreateClient(result);
            await using var _ = client;
            using var __ = capture;
            await client.StartAsync(cancellationToken);
            capture?.Attach(client);

            if (result.GetValue(register))
            {
                var registration = await client.RegisterAsync(cancellationToken);
                AnsiConsole.MarkupLine($"Registration: {registration.State} ({registration.StatusCode})");
                if (registration.State != RegistrationState.Registered)
                {
                    return 1;
                }
            }

            var plan = new LoadPlan
            {
                Target = result.GetValue(target)!,
                Calls = Math.Max(result.GetValue(calls), 1),
                Concurrency = Math.Max(result.GetValue(concurrency), 1),
                CallsPerSecond = Math.Max(result.GetValue(cps), 0),
                CallDuration = TimeSpan.FromSeconds(Math.Max(result.GetValue(duration), 1)),
                ToneHz = result.GetValue(tone),
            };

            AnsiConsole.MarkupLine(
                $"Calling [bold]{Markup.Escape(plan.Target)}[/]: {plan.Calls} calls, {plan.CallsPerSecond:0.##}/s, up to {plan.Concurrency} at once, {plan.CallDuration.TotalSeconds:0.#} s each.");

            LoadReport? report = null;
            await ConsoleLive.RunAsync(Progress(new LoadProgress(0, 0, 0, 0, TimeSpan.Zero), plan), async update =>
            {
                report = await LoadRunner.RunAsync(client, plan, progress => update(Progress(progress, plan)), cancellationToken);
            });

            AnsiConsole.WriteLine();
            AnsiConsole.Write(Summary(report!));
            foreach (var (code, reason, count) in report!.Failures)
            {
                AnsiConsole.MarkupLine($"[red]{count} × {code}[/] {Markup.Escape(reason)}");
            }

            return report.Failed == 0 ? 0 : 1;
        });
        return command;
    }

    private static Table Progress(LoadProgress progress, LoadPlan plan)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Placed").AddColumn("Active").AddColumn("Connected").AddColumn("Failed").AddColumn("Elapsed");
        table.AddRow(
            $"{progress.Placed}/{plan.Calls}",
            progress.Active.ToString(),
            $"[green]{progress.Connected}[/]",
            progress.Failed > 0 ? $"[red]{progress.Failed}[/]" : "0",
            $"{progress.Elapsed.TotalSeconds:0.0} s");
        return table;
    }

    private static Table Summary(LoadReport report)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Measure").AddColumn("Value");
        table.AddRow("Calls placed", report.Attempted.ToString());
        table.AddRow("Connected", $"[green]{report.Connected}[/]");
        table.AddRow("Failed", report.Failed > 0 ? $"[red]{report.Failed}[/]" : "0");
        table.AddRow("Achieved rate", $"{report.CallsPerSecond:0.##} calls/s");
        table.AddRow("Peak concurrent", report.PeakConcurrency.ToString());
        table.AddRow("Setup p50 / p95 / max", $"{report.SetupMs(0.5):F0} / {report.SetupMs(0.95):F0} / {report.SetupMs(1):F0} ms");
        table.AddRow("MOS (average)", report.AverageMos > 0 ? $"{report.AverageMos:F2}" : "—");
        table.AddRow("Packet loss (average)", $"{report.AverageLossPercent:F2} %");
        return table;
    }
}
