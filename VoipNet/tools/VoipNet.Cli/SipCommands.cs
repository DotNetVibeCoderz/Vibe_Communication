using System.CommandLine;
using Spectre.Console;
using VoipNet.Audio;
using VoipNet.Video;
using VoipNet.Diagnostics;

namespace VoipNet.Cli;

internal static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Show the SDK and engine versions.");
        command.SetAction(_ =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[bold]Voip.NET CLI[/]", typeof(VersionCommand).Assembly.GetName().Version?.ToString() ?? "1.0.0");
            grid.AddRow("[bold]Native engine[/]", VoipClient.EngineVersion);
            grid.AddRow("[bold].NET runtime[/]", Environment.Version.ToString());
            grid.AddRow("[bold]Made by[/]", "Gravicode Studios — led by Kang Fadhil");
            AnsiConsole.Write(grid);
            return 0;
        });
        return command;
    }
}

/// <summary>Options shared by every SIP command.</summary>
internal sealed class AccountOptions
{
    public Option<string?> Domain { get; } = new("--domain", "-d") { Description = "SIP domain or PBX host." };
    public Option<string> User { get; } = new("--user", "-u") { Description = "SIP user name.", DefaultValueFactory = _ => "voipnet" };
    public Option<string?> Password { get; } = new("--password", "-p") { Description = "Password for digest authentication." };
    public Option<string?> Proxy { get; } = new("--proxy") { Description = "Outbound proxy host[:port]." };
    public Option<string> Transport { get; } = new("--transport", "-t") { Description = "udp, tcp, tls, ws or wss.", DefaultValueFactory = _ => "udp" };
    public Option<string[]> TlsPin { get; } = new("--tls-pin") { Description = "Accept only TLS certificates with this SHA-256 fingerprint (repeatable)." };
    public Option<bool> TlsInsecure { get; } = new("--tls-insecure") { Description = "Do not validate the TLS server certificate." };
    public Option<bool> Srtp { get; } = new("--srtp") { Description = "Require SRTP media encryption." };
    public Option<bool> Dtls { get; } = new("--dtls") { Description = "Exchange SRTP keys with DTLS (WebRTC style) instead of SDES; implies --srtp." };
    public Option<string> Bind { get; } = new("--bind") { Description = "Local address to bind.", DefaultValueFactory = _ => "0.0.0.0" };
    public Option<int> Port { get; } = new("--port") { Description = "Local SIP port (0 picks a free one).", DefaultValueFactory = _ => 0 };
    public Option<bool> Trace { get; } = new("--trace") { Description = "Print every SIP message." };
    public Option<string?> Pcap { get; } = new("--pcap") { Description = "Write the SIP exchange to a pcap file for Wireshark." };

    public void AddTo(Command command)
    {
        foreach (var option in new Option[] { Domain, User, Password, Proxy, Transport, TlsPin, TlsInsecure, Srtp, Dtls, Bind, Port, Trace, Pcap })
        {
            command.Options.Add(option);
        }
    }

    public VoipClientOptions Build(ParseResult result, int? port = null) => new()
    {
        Domain = result.GetValue(Domain) ?? string.Empty,
        Username = result.GetValue(User) ?? "voipnet",
        Password = result.GetValue(Password) ?? string.Empty,
        OutboundProxy = result.GetValue(Proxy),
        Transport = result.GetValue(Transport)?.ToLowerInvariant() switch
        {
            "tcp" => SipTransport.Tcp,
            "tls" => SipTransport.Tls,
            "ws" => SipTransport.Ws,
            "wss" => SipTransport.Wss,
            _ => SipTransport.Udp,
        },
        TlsPinnedFingerprints = result.GetValue(TlsPin) ?? [],
        TlsVerifyServer = !result.GetValue(TlsInsecure),
        Srtp = result.GetValue(Srtp) || result.GetValue(Dtls) ? SrtpMode.Mandatory : SrtpMode.Disabled,
        SrtpKeying = result.GetValue(Dtls) ? SrtpKeying.Dtls : SrtpKeying.Sdes,
        BindAddress = result.GetValue(Bind) ?? "0.0.0.0",
        SipPort = port ?? result.GetValue(Port),
        TraceSip = result.GetValue(Trace) || result.GetValue(Pcap) is not null,
        UserAgent = "voipnet-cli/1.0",
    };

    public (VoipClient Client, PcapWriter? Capture) CreateClient(ParseResult result, int? port = null, Action<VoipClientOptions>? configure = null)
    {
        var options = Build(result, port);
        configure?.Invoke(options);
        var client = new VoipClient(options);
        PcapWriter? capture = null;
        if (result.GetValue(Trace))
        {
            client.SipTrace += (_, e) =>
            {
                var arrow = e.Outgoing ? "[cyan]──▶[/]" : "[green]◀──[/]";
                AnsiConsole.MarkupLine($"{arrow} [grey]{Markup.Escape(e.RemoteEndPoint)}[/]");
                AnsiConsole.WriteLine(e.Message.TrimEnd());
                AnsiConsole.WriteLine();
            };
        }

        if (result.GetValue(Pcap) is { } path)
        {
            capture = new PcapWriter(path);
        }

        return (client, capture);
    }
}

internal static class SipCommands
{
    public static Command Create()
    {
        var sip = new Command("sip", "Test SIP servers, trunks and phones.");
        sip.Subcommands.Add(Ping());
        sip.Subcommands.Add(Register());
        sip.Subcommands.Add(Call());
        sip.Subcommands.Add(Listen());
        sip.Subcommands.Add(Message());
        return sip;
    }

    private static Command Ping()
    {
        var account = new AccountOptions();
        var target = new Argument<string>("target") { Description = "SIP URI to ping, e.g. sip:pbx.example.com" };
        var count = new Option<int>("--count", "-c") { Description = "Number of pings.", DefaultValueFactory = _ => 4 };
        var command = new Command("ping", "Send OPTIONS requests and report round-trip times.") { target, count };
        account.AddTo(command);

        command.SetAction(async (result, cancellationToken) =>
        {
            var (client, capture) = account.CreateClient(result);
            await using var _ = client;
            using var __ = capture;
            await client.StartAsync(cancellationToken);
            capture?.Attach(client);

            var destination = result.GetValue(target)!;
            var times = new List<double>();
            var failures = 0;
            for (var i = 0; i < result.GetValue(count) && !cancellationToken.IsCancellationRequested; i++)
            {
                var reply = await client.PingAsync(destination, cancellationToken);
                if (reply.IsSuccess)
                {
                    times.Add(reply.Latency.TotalMilliseconds);
                    AnsiConsole.MarkupLine($"[green]{reply.StatusCode} {Markup.Escape(reply.Reason)}[/] from {Markup.Escape(destination)} time=[bold]{reply.Latency.TotalMilliseconds:F0} ms[/] {Markup.Escape(reply.UserAgent ?? string.Empty)}");
                }
                else
                {
                    failures++;
                    AnsiConsole.MarkupLine($"[red]{reply.StatusCode} {Markup.Escape(reply.Reason)}[/] after {reply.Latency.TotalMilliseconds:F0} ms");
                }

                await Task.Delay(1000, cancellationToken);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(times.Count == 0
                ? $"[red]No replies[/] ({failures} failed)."
                : $"Replies [bold]{times.Count}[/], failed [bold]{failures}[/], min/avg/max = {times.Min():F0}/{times.Average():F0}/{times.Max():F0} ms");
            return times.Count > 0 ? 0 : 1;
        });
        return command;
    }

    private static Command Register()
    {
        var account = new AccountOptions();
        var command = new Command("register", "Register with a PBX and show the result.");
        account.AddTo(command);

        command.SetAction(async (result, cancellationToken) =>
        {
            var (client, capture) = account.CreateClient(result);
            await using var _ = client;
            using var __ = capture;
            await client.StartAsync(cancellationToken);
            capture?.Attach(client);

            RegistrationEventArgs? outcome = null;
            await AnsiConsole.Status().StartAsync("Registering…", async _ =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(35));
                outcome = await client.RegisterAsync(timeout.Token);
            });

            if (outcome is { State: RegistrationState.Registered })
            {
                AnsiConsole.MarkupLine($"[green]Registered[/] as {Markup.Escape(result.GetValue(account.User)!)}@{Markup.Escape(result.GetValue(account.Domain) ?? string.Empty)} for {outcome.ExpiresSeconds} s");
                client.Unregister();
                await Task.Delay(500, cancellationToken);
                return 0;
            }

            AnsiConsole.MarkupLine($"[red]Registration failed:[/] {outcome?.StatusCode} {Markup.Escape(outcome?.Reason ?? "no response")}");
            return 1;
        });
        return command;
    }

    private static Command Call()
    {
        var account = new AccountOptions();
        var target = new Argument<string>("target") { Description = "Who to call: SIP URI or extension." };
        var duration = new Option<int>("--duration") { Description = "Seconds to stay connected.", DefaultValueFactory = _ => 10 };
        var tone = new Option<int>("--tone") { Description = "Send a test tone at this frequency (Hz). 0 sends nothing.", DefaultValueFactory = _ => 440 };
        var dtmf = new Option<string?>("--dtmf") { Description = "DTMF digits to send after answer." };
        var record = new Option<string?>("--record") { Description = "Record the call; the extension picks the format (.wav, .mp3, .avi with video)." };
        var video = new Option<string?>("--video") { Description = "Send this H.264 file (Annex B, as ffmpeg writes with -f h264) as video, looping until the call ends." };
        var videoFps = new Option<int>("--video-fps") { Description = "Frame rate for --video, --camera and --screen.", DefaultValueFactory = _ => 15 };
        var camera = new Option<bool>("--camera") { Description = "Send this machine's camera, encoded to H.264 (needs a platform codec)." };
        var screen = new Option<bool>("--screen") { Description = "Send this machine's screen, encoded to H.264 (needs a platform codec)." };
        var register = new Option<bool>("--register") { Description = "Register before calling." };
        var command = new Command("call", "Place a test call and report media quality.") { target, duration, tone, dtmf, record, register, video, videoFps, camera, screen };
        account.AddTo(command);

        command.SetAction(async (result, cancellationToken) =>
        {
            // Video is negotiated in the first offer, so it has to be on before the client starts.
            var videoFile = result.GetValue(video);
            var live = result.GetValue(camera) || result.GetValue(screen);
            var (client, capture) = account.CreateClient(result, configure: o => o.Video = videoFile is { Length: > 0 } || live);
            await using var _ = client;
            using var __ = capture;
            await client.StartAsync(cancellationToken);
            capture?.Attach(client);

            if (result.GetValue(register))
            {
                var reg = await client.RegisterAsync(cancellationToken);
                AnsiConsole.MarkupLine($"Registration: {reg.State} ({reg.StatusCode})");
            }

            var destination = result.GetValue(target)!;
            var call = client.Call(destination);
            call.StateChanged += (_, e) => AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss.fff}[/] {e.State} {e.StatusCode} {Markup.Escape(e.Reason)}");
            call.DtmfReceived += (_, e) => AnsiConsole.MarkupLine($"DTMF [bold]{e.Digit}[/] via {e.Source}");

            try
            {
                using var ring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ring.CancelAfter(TimeSpan.FromSeconds(60));
                await call.Connected.WaitAsync(ring.Token);
            }
            catch (Exception ex) when (ex is VoipException or OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"[red]Call failed:[/] {Markup.Escape(ex.Message)}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Connected[/] codec [bold]{call.Codec}[/] @ {call.SampleRate} Hz");
            var inbound = new VideoStreamStats();
            Task videoSender = Task.CompletedTask;
            if (videoFile is { Length: > 0 } || live)
            {
                call.VideoFrameReceived += (_, _, keyframe, frame, _) => inbound.Add(frame.Length, keyframe);
                videoSender = live
                    ? SendLiveVideoAsync(call, result.GetValue(screen), result.GetValue(videoFps), cancellationToken)
                    : SendVideoAsync(call, videoFile!, result.GetValue(videoFps), cancellationToken);
                AnsiConsole.MarkupLine($"Video: [bold]{call.VideoCodec ?? "not negotiated"}[/]");
            }
            // The extension says what to write: WAV, MP3, or MP4/AVI when the call carries video.
            using var recorder = result.GetValue(record) is { } file
                ? CallRecorder.Start(call, file, System.IO.Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".mp3" => RecordingFormat.Mp3,
                    ".avi" => RecordingFormat.Avi,
                    ".mp4" => RecordingFormat.Mp4,
                    _ => RecordingFormat.Wav,
                })
                : null;

            if (result.GetValue(tone) is > 0 and var frequency)
            {
                var seconds = result.GetValue(duration);
                var samples = new short[16000 * seconds];
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = (short)(8000 * Math.Sin(2 * Math.PI * frequency * i / 16000));
                }

                call.SendAudio(samples, 16000);
            }

            if (result.GetValue(dtmf) is { Length: > 0 } digits)
            {
                call.SendDtmf(digits);
            }

            var end = DateTime.UtcNow.AddSeconds(result.GetValue(duration));
            await ConsoleLive.RunAsync(StatsTable(call.GetStatistics()), async update =>
            {
                while (DateTime.UtcNow < end && call.IsActive && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(500, CancellationToken.None);
                    if (call.IsActive)
                    {
                        update(StatsTable(call.GetStatistics()));
                    }
                }
            });

            if (call.IsActive)
            {
                await call.HangupAsync(CancellationToken.None);
            }

            await videoSender;
            var final = call.FinalStatistics;
            if (final is not null)
            {
                AnsiConsole.MarkupLine($"Final: MOS [bold]{final.Mos:F2}[/], loss {final.LossPercent:F1}%, jitter {final.JitterMs:F1} ms");
            }

            if (inbound.Frames > 0)
            {
                AnsiConsole.Write(VideoTable(inbound));
            }

            return 0;
        });
        return command;
    }

    private static Command Listen()
    {
        var account = new AccountOptions();
        var sipPort = new Option<int>("--sip-port") { Description = "Port to listen on.", DefaultValueFactory = _ => 5060 };
        var echo = new Option<bool>("--echo") { Description = "Play the caller's audio back to them (echo test)." };
        var answerAfter = new Option<int>("--answer-after") { Description = "Milliseconds to ring before answering.", DefaultValueFactory = _ => 500 };
        var video = new Option<bool>("--video") { Description = "Accept a video stream as well; with --echo the video is sent back too." };
        var command = new Command("listen", "Answer incoming calls; with --echo acts as an echo test service.") { sipPort, echo, answerAfter, video };
        account.AddTo(command);

        command.SetAction(async (result, cancellationToken) =>
        {
            var (client, capture) = account.CreateClient(result, result.GetValue(sipPort), o => o.Video = result.GetValue(video));
            await using var _ = client;
            using var __ = capture;
            await client.StartAsync(cancellationToken);
            capture?.Attach(client);
            AnsiConsole.MarkupLine($"Listening on [bold]{client.LocalAddress}[/]. Press Ctrl+C to stop.");
            if (client.TlsFingerprint is { } fingerprint)
            {
                AnsiConsole.MarkupLine($"TLS certificate SHA-256 [bold]{fingerprint}[/] (pin with --tls-pin).");
            }

            client.IncomingCall += async (_, e) =>
            {
                AnsiConsole.MarkupLine($"[yellow]Incoming[/] from {Markup.Escape(e.DisplayName ?? string.Empty)} {Markup.Escape(e.From)}");
                await Task.Delay(result.GetValue(answerAfter));
                if (result.GetValue(echo))
                {
                    e.Call.AudioReceived += (call, direction, rate, samples) =>
                    {
                        if (direction == AudioDirection.Inbound)
                        {
                            call.SendAudio(samples, rate);
                        }
                    };

                    // Video goes back on the stream it arrived on, which makes this a video echo test.
                    e.Call.VideoFrameReceived += (call, timestamp, _, frame, content) =>
                    {
                        try
                        {
                            call.SendVideoFrame(timestamp, frame, content);
                        }
                        catch (VoipException)
                        {
                            // The caller hung up mid-frame.
                        }
                    };
                }

                e.Call.DtmfReceived += (_, d) => AnsiConsole.MarkupLine($"DTMF [bold]{d.Digit}[/] ({d.Source})");
                e.Call.StateChanged += (_, s) => AnsiConsole.MarkupLine($"[grey]{e.From}[/] {s.State} {s.StatusCode}");
                await e.Call.AnswerAsync();
            };
            client.MessageReceived += (_, m) => AnsiConsole.MarkupLine($"[blue]MESSAGE[/] from {Markup.Escape(m.From)}: {Markup.Escape(m.Body)}");

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C
            }

            return 0;
        });
        return command;
    }

    private static Command Message()
    {
        var account = new AccountOptions();
        var target = new Argument<string>("target") { Description = "Recipient SIP URI." };
        var text = new Argument<string>("text") { Description = "Message text." };
        var command = new Command("message", "Send a SIP MESSAGE.") { target, text };
        account.AddTo(command);

        command.SetAction(async (result, cancellationToken) =>
        {
            var (client, capture) = account.CreateClient(result);
            await using var _ = client;
            using var __ = capture;
            await client.StartAsync(cancellationToken);
            capture?.Attach(client);
            var reply = await client.SendMessageAsync(result.GetValue(target)!, result.GetValue(text)!, cancellationToken: cancellationToken);
            AnsiConsole.MarkupLine(reply.IsSuccess ? $"[green]Delivered[/] ({reply.StatusCode})" : $"[red]Not delivered:[/] {reply.StatusCode} {Markup.Escape(reply.Reason)}");
            return reply.IsSuccess ? 0 : 1;
        });
        return command;
    }

    /// <summary>
    /// Streams an H.264 file into the call at a fixed rate, looping until the call ends. A file is
    /// what makes the path testable from a shell on a machine with no camera and no codec.
    /// </summary>
    /// <summary>
    /// Sends the camera or the screen, encoded here. Both are read by pulling: the device paces the
    /// loop, so nothing queues up behind a slow encoder, and a keyframe goes out whenever the far end
    /// asks for one.
    /// </summary>
    private static async Task SendLiveVideoAsync(VoipCall call, bool screen, int fps, CancellationToken cancellationToken)
    {
        if (!VideoCodecs.IsH264Available)
        {
            AnsiConsole.MarkupLine("[yellow]This machine has no H.264 encoder, so there is nothing to send. Use --video with a file instead.[/]");
            return;
        }

        await Task.Yield();
        try
        {
            using var source = screen
                ? VideoCapture.OpenScreen(wholeDesktop: false, width: 1280, height: 720, framesPerSecond: fps)
                : VideoCapture.OpenCamera(width: 640, height: 360, framesPerSecond: fps);
            using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
            {
                Width = source.Width,
                Height = source.Height,
                FramesPerSecond = Math.Clamp(fps, 1, 60),
                BitsPerSecond = screen ? 1_500_000 : 800_000,
            });

            call.KeyframeRequested += (_, _) => encoder.RequestKeyframe();
            AnsiConsole.MarkupLine($"Sending [bold]{Markup.Escape(source.Name)}[/] at {source.Width}x{source.Height} · {Markup.Escape(encoder.Implementation)}");

            while (!cancellationToken.IsCancellationRequested && call.IsActive)
            {
                if (source.Read() is not { } picture)
                {
                    return;
                }

                foreach (var frame in encoder.Encode(picture))
                {
                    if (!call.IsActive)
                    {
                        return;
                    }

                    call.SendVideoFrame((uint)(picture.Timestamp.TotalSeconds * 90000), frame.Data.Span);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(ex.Message)}[/]");
        }
    }

    private static async Task SendVideoAsync(VoipCall call, string path, int fps, CancellationToken cancellationToken)
    {
        var frames = AnnexB.Frames(File.ReadAllBytes(path));
        if (frames.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(path)} holds no H.264 frames.[/]");
            return;
        }

        var step = TimeSpan.FromSeconds(1.0 / Math.Clamp(fps, 1, 60));
        var ticks = (uint)(90000 / Math.Clamp(fps, 1, 60));
        uint timestamp = 0;
        var index = 0;
        while (call.IsActive && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                call.SendVideoFrame(timestamp, frames[index % frames.Count]);
            }
            catch (VoipException)
            {
                return;   // the call ended between the check and the frame
            }

            timestamp += ticks;
            index++;
            await Task.Delay(step, CancellationToken.None);
        }
    }

    private static Table VideoTable(VideoStreamStats video)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Video received[/]");
        table.AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        table.AddRow("Frames / keyframes", $"{video.Frames} / {video.Keyframes}");
        table.AddRow("Frame rate", $"{video.FrameRate:F1} fps");
        table.AddRow("Bitrate", $"{video.Kbps:F0} kbit/s");
        table.AddRow("Keyframe every", video.Keyframes > 0 ? $"{video.KeyframeInterval:F0} frames" : "[grey]none seen[/]");
        var freeze = video.LongestGap.TotalMilliseconds;
        table.AddRow("Longest freeze", freeze > 500 ? $"[yellow]{freeze:F0} ms[/]" : $"{freeze:F0} ms");
        return table;
    }

    private static Table StatsTable(CallStatistics s)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Media quality[/]");
        table.AddColumn("Metric").AddColumn(new TableColumn("Value").RightAligned());
        var mosColour = s.Mos >= 4 ? "green" : s.Mos >= 3.5 ? "yellow" : "red";
        table.AddRow("MOS (estimated)", $"[{mosColour}]{s.Mos:F2}[/]");
        table.AddRow("Packets sent / received", $"{s.PacketsSent} / {s.PacketsReceived}");
        table.AddRow("Lost / late", $"{s.PacketsLost} / {s.PacketsLate} ({s.LossPercent:F1}%)");
        table.AddRow("Jitter", $"{s.JitterMs:F1} ms");
        table.AddRow("Jitter buffer", $"{s.JitterBufferMs} ms");
        // RTCP reports arrive a second into the call and then every few seconds.
        table.AddRow("Round trip (RTCP)", s.RoundTripMs > 0 ? $"{s.RoundTripMs:F1} ms" : "[grey]waiting[/]");
        table.AddRow("Peer reports", s.RoundTripMs > 0 ? $"{s.RemoteLossPercent:F1}% lost, {s.RemoteJitterMs:F1} ms jitter" : "[grey]waiting[/]");
        table.AddRow("MOS at the peer", s.RemoteMos > 0 ? $"{s.RemoteMos:F1}" : "[grey]waiting[/]");
        table.AddRow("Codec", $"PT {s.PayloadType} @ {s.SampleRate} Hz");
        table.AddRow("SRTP", s.SecureRtp ? "[green]on[/]" : "off");
        return table;
    }
}
