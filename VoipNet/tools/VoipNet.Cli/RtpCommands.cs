using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Spectre.Console;
using VoipNet.Diagnostics;

namespace VoipNet.Cli;

internal static class RtpCommands
{
    public static Command Create()
    {
        var rtp = new Command("rtp", "Analyse RTP streams.");
        rtp.Subcommands.Add(Analyze());
        rtp.Subcommands.Add(Listen());
        return rtp;
    }

    private static Command Analyze()
    {
        var file = new Argument<FileInfo>("capture") { Description = "A libpcap (.pcap) file." };
        var json = new Option<bool>("--json") { Description = "Print the report as JSON." };
        var command = new Command("analyze", "Report loss, jitter and MOS for every RTP stream in a capture.") { file, json };
        command.SetAction(result =>
        {
            var capture = result.GetValue(file)!;
            if (!capture.Exists)
            {
                AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(capture.FullName)}");
                return 1;
            }

            var reports = RtpStreamAnalyzer.AnalyzeFile(capture.FullName);
            Print(reports, result.GetValue(json));
            return reports.Count > 0 ? 0 : 2;
        });
        return command;
    }

    private static Command Listen()
    {
        var port = new Option<int>("--port") { Description = "UDP port to receive RTP on.", Required = true };
        var seconds = new Option<int>("--seconds") { Description = "How long to listen.", DefaultValueFactory = _ => 30 };
        var command = new Command("listen", "Receive RTP on a UDP port and report its quality live.") { port, seconds };
        command.SetAction(async (result, cancellationToken) =>
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, result.GetValue(port)));
            var analyzer = new RtpStreamAnalyzer();
            var local = (IPEndPoint)socket.Client.LocalEndPoint!;
            AnsiConsole.MarkupLine($"Receiving RTP on [bold]{local}[/] for {result.GetValue(seconds)} s…");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(result.GetValue(seconds)));
            await ConsoleLive.RunAsync(new Markup("[grey]waiting for packets…[/]"), async update =>
            {
                var lastDraw = DateTime.UtcNow;
                try
                {
                    while (!deadline.IsCancellationRequested)
                    {
                        var packet = await socket.ReceiveAsync(deadline.Token);
                        analyzer.Add(new UdpDatagram(DateTimeOffset.UtcNow, packet.RemoteEndPoint, local, packet.Buffer));
                        if (DateTime.UtcNow - lastDraw > TimeSpan.FromSeconds(1))
                        {
                            lastDraw = DateTime.UtcNow;
                            update(Table(analyzer.Reports()));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Time is up.
                }

                update(Table(analyzer.Reports()));
            });
            return 0;
        });
        return command;
    }

    private static void Print(IReadOnlyList<RtpStreamReport> reports, bool json)
    {
        if (json)
        {
            var rows = reports.Select(r => new
            {
                ssrc = $"0x{r.Ssrc:X8}",
                source = r.Source.ToString(),
                destination = r.Destination.ToString(),
                codec = r.Codec,
                r.Packets,
                r.Expected,
                r.Lost,
                lossPercent = Math.Round(r.LossPercent, 2),
                jitterMs = Math.Round(r.JitterMs, 2),
                maxJitterMs = Math.Round(r.MaxJitterMs, 2),
                maxDeltaMs = Math.Round(r.MaxDeltaMs, 1),
                r.OutOfOrder,
                r.Duplicates,
                durationSeconds = Math.Round(r.Duration.TotalSeconds, 2),
                mos = Math.Round(r.Mos, 2),
            });
            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        if (reports.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No RTP streams found.[/]");
            return;
        }

        AnsiConsole.Write(Table(reports));
    }

    private static Table Table(IReadOnlyList<RtpStreamReport> reports)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]RTP streams[/]");
        foreach (var column in new[] { "SSRC", "Path", "Codec", "Packets", "Lost", "Jitter", "Max Δ", "Order", "Duration", "MOS" })
        {
            table.AddColumn(column);
        }

        foreach (var r in reports)
        {
            var mos = r.Mos >= 4 ? "green" : r.Mos >= 3.5 ? "yellow" : "red";
            table.AddRow(
                $"0x{r.Ssrc:X8}",
                Markup.Escape($"{r.Source} → {r.Destination}"),
                r.Codec,
                r.Packets.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{r.Lost} ({r.LossPercent:F1}%)",
                $"{r.JitterMs:F1} ms",
                $"{r.MaxDeltaMs:F0} ms",
                r.OutOfOrder + r.Duplicates == 0 ? "[green]ok[/]" : $"{r.OutOfOrder} ooo / {r.Duplicates} dup",
                $"{r.Duration.TotalSeconds:F1} s",
                $"[{mos}]{r.Mos:F2}[/]");
        }

        return table;
    }
}
