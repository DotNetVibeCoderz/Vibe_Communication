using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using VoipNet.Diagnostics;
using VoipNet.Enterprise.CallCenter;
using VoipNet.Enterprise.Ivr;
using VoipNet.Gallery.Infrastructure;

namespace VoipNet.Gallery.Pages;

public sealed class IvrPage : DemoPage
{
    private readonly Lab _lab = new();

    public IvrPage()
        : base("CONTACT CENTRE", "IVR builder", "Menus, key presses, collected values and actions — transfer, queue, AI hand-off or hang up. The simulated caller below presses 2, keys in an account number, then 9.")
    {
        AddAction("Run the IVR", RunAsync, "go");
    }

    public override string Code => """
        var flow = IvrFlow.Create()
            .Welcome("Selamat datang di Gravicode Net.")
            .Menu("main", "Tekan 1 untuk penjualan, 2 untuk bantuan teknis.", m => m
                .Option('1', "Penjualan", new IvrAction.Transfer("sip:sales@pbx"))
                .Option('2', "Bantuan", new IvrAction.Collect(
                    "account", "Masukkan nomor pelanggan, akhiri dengan pagar.", 8, "support")))
            .Menu("support", "Tekan 9 untuk bicara dengan agen AI.", m => m
                .Option('9', "Agen AI", new IvrAction.Handoff("ai",
                    (call, ctx, ct) => agent.RunAsync(call, ct))))
            .Build();

        var result = await new IvrRunner(textToSpeech).RunAsync(call, flow);
        // result.Context.Values["account"], result.Outcome
        """;

    private async Task RunAsync()
    {
        await _lab.ResetAsync();
        var caller = await _lab.StartAsync("caller");
        var ivr = await _lab.StartAsync("ivr");

        var flow = IvrFlow.Create()
            .Welcome("Selamat datang di Gravicode Net.")
            .Menu("main", "Tekan satu untuk penjualan, dua untuk bantuan teknis.", m => m
                .Option('1', "Penjualan", new IvrAction.Transfer("sip:sales@pbx"))
                .Option('2', "Bantuan", new IvrAction.Collect("account", "Masukkan nomor pelanggan, akhiri dengan pagar.", 8, "support")))
            .Menu("support", "Tekan sembilan untuk masuk antrean agen.", m => m
                .Option('9', "Antrean", new IvrAction.Enqueue("support", "Mohon tunggu, kami sambungkan.")))
            .Build();

        IvrResult? result = null;
        ivr.IncomingCall += (_, e) => Lab.Safe(async () =>
        {
            await e.Call.AnswerAsync();
            TraceAudio(e.Call, AudioDirection.Outbound);
            result = await new IvrRunner(new SyllableSpeech()).RunAsync(e.Call, flow);
        });

        var call = await caller.CallAsync(Lab.Uri(ivr, "ivr")).WaitAsync(TimeSpan.FromSeconds(10));
        call.DtmfReceived += (_, d) => Write($"caller heard DTMF {d.Digit}");
        foreach (var (delay, keys) in new[] { (3500, "2"), (3000, "4321#"), (3200, "9") })
        {
            await Task.Delay(delay);
            Write($"caller presses {keys}");
            call.SendDtmf(keys, 90);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (result is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (result is not null)
        {
            SetMetric("Outcome", result.Outcome.ToString());
            SetMetric("Path", string.Join(" → ", result.Context.Path));
            SetMetric("Account", result.Context.Values.GetValueOrDefault("account", "—"));
            SetMetric("Queue", result.Context.RequestedQueue ?? "—");
            Write($"IVR finished: {result.Outcome}");
        }

        await call.HangupAsync();
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}

public sealed class CallCenterPage : DemoPage
{
    private readonly Lab _lab = new();
    private CallCenterService? _center;

    public CallCenterPage()
        : base("CONTACT CENTRE", "Queues and agents", "Callers wait in a queue; the longest-idle available agent is rung and both legs are bridged in a conference, so recording and supervisor listen-in keep working.")
    {
        AddAction("Simulate a busy minute", RunAsync, "go");
    }

    public override string Code => """
        var center = new CallCenterService(pbx, textToSpeech);
        center.AddQueue(new CallQueueOptions
        {
            Name = "support",
            Strategy = RoutingStrategy.LongestIdle,
            ServiceLevelTarget = TimeSpan.FromSeconds(20),
            MusicOnHoldFile = "hold.wav",
        });
        center.AddAgent(new Agent { Id = "a1", Name = "Sari", Uri = "sip:2001@pbx", Skills = { "support" } });
        center.SetAgentState("a1", AgentState.Available);

        pbx.IncomingCall += async (_, e) =>
        {
            await e.Call.AnswerAsync();
            var result = await center.EnqueueAsync(e.Call, "support");
        };

        var sla = center.Metrics.Snapshot("support").ServiceLevel;
        """;

    private async Task RunAsync()
    {
        await ResetAsync();
        var pbx = await _lab.StartAsync("pbx");
        var agentPhones = await _lab.StartAsync("agents");
        var customers = await _lab.StartAsync("customers");
        Lab.AutoAnswer(agentPhones, 700);

        _center = new CallCenterService(pbx);
        _center.AddQueue(new CallQueueOptions { Name = "support", AnnouncePosition = false, WrapupTime = TimeSpan.FromSeconds(1) });
        foreach (var (id, name) in new[] { ("a1", "Sari"), ("a2", "Dimas") })
        {
            _center.AddAgent(new Agent { Id = id, Name = name, Uri = $"sip:{id}@{agentPhones.LocalAddress}" });
            _center.SetAgentState(id, AgentState.Available);
        }

        _center.AgentStateChanged += (_, a) => Write($"agent {a.Name} → {a.State}");
        _center.CallDequeued += (_, e) => Write($"caller {e.Call.Call.RemoteUri} → {e.Result.Outcome} after {e.Result.Waited.TotalSeconds:0.0} s{(e.Result.Agent is { } ag ? $" with {ag.Name}" : string.Empty)}");

        var center = _center;
        pbx.IncomingCall += (_, e) => Lab.Safe(async () =>
        {
            await e.Call.AnswerAsync();
            await center.EnqueueAsync(e.Call, "support");
        });

        var calls = new List<VoipCall>();
        for (var i = 1; i <= 4; i++)
        {
            Write($"customer {i} calls");
            var call = customers.Call(Lab.Uri(pbx, $"line{i}"));
            calls.Add(call);
            if (i == 1)
            {
                TraceAudio(call, AudioDirection.Outbound);
            }

            await Task.Delay(600);
        }

        foreach (var call in calls)
        {
            await Task.WhenAny(call.Connected, call.Completion);
            if (call.IsActive)
            {
                call.SendAudio(Voice(2500), 16000);
            }
        }

        // Customer 4 gives up while waiting; the others talk for a few seconds.
        await Task.Delay(1500);
        calls[3].Hangup();
        await Task.Delay(2500);
        foreach (var call in calls.Where(c => c.IsActive).Take(2))
        {
            call.Hangup();
            await Task.Delay(400);
        }

        await Task.Delay(4000);
        foreach (var call in calls.Where(c => c.IsActive))
        {
            call.Hangup();
        }

        await Task.Delay(1500);
    }

    public override void Tick()
    {
        base.Tick();
        if (_center is null)
        {
            return;
        }

        var m = _center.Metrics.Snapshot("support");
        SetMetric("Offered · answered · lost", $"{m.Offered} · {m.Answered} · {m.Abandoned}");
        SetMetric("Waiting now", _center.Waiting("support").Count.ToString(CultureInfo.InvariantCulture));
        SetMetric("Average wait", $"{m.AverageWait.TotalSeconds:0.0} s");
        SetMetric("Service level", $"{m.ServiceLevel:P0}");
        SetMetric("Agents", string.Join(" · ", _center.Agents.Select(a => $"{a.Name} {a.State}")));
    }

    public override async Task ResetAsync()
    {
        if (_center is not null)
        {
            await _center.DisposeAsync();
            _center = null;
        }

        await _lab.ResetAsync();
    }
}

public sealed class DiagnosticsPage : DemoPage
{
    private readonly Lab _lab = new();

    public DiagnosticsPage()
        : base("OPERATIONS", "Diagnostics", "Capture SIP to a pcap that opens in Wireshark, analyse RTP streams for loss and jitter, and read engine counters through System.Diagnostics.Metrics.")
    {
        AddAction("Capture a call", CaptureAsync, "go");
        AddAction("Analyse an RTP stream", () => { AnalyzeSynthetic(); return Task.CompletedTask; }, "quiet");
    }

    public override string Code => """
        // SIP trace → pcap (Wireshark decodes it as SIP)
        using var capture = new PcapWriter("call.pcap");
        capture.Attach(client);                       // needs TraceSip = true

        // RTP quality from any capture
        foreach (var s in RtpStreamAnalyzer.AnalyzeFile("trunk.pcap"))
            Console.WriteLine($"{s.Ssrc:X8} {s.Codec} loss {s.LossPercent:0.0}% " +
                              $"jitter {s.JitterMs:0.0} ms MOS {s.Mos:0.00}");

        // Live counters
        // dotnet-counters monitor --counters VoipNet
        """;

    private async Task CaptureAsync()
    {
        await _lab.ResetAsync();
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-gallery-{DateTime.Now:HHmmss}.pcap");
        var counts = new Dictionary<string, double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == VoipMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((i, v, _, _) => { lock (counts) counts[i.Name] = counts.GetValueOrDefault(i.Name) + v; });
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => { lock (counts) counts[i.Name] = v; });
        listener.Start();

        var caller = await _lab.StartAsync("traced", o => o.TraceSip = true);
        var callee = await _lab.StartAsync("peer");
        Lab.AutoAnswer(callee, 100);
        callee.IncomingCall += (_, e) => TraceAudio(e.Call);
        caller.SipTrace += (_, e) => Write($"{(e.Outgoing ? "→" : "←")} {e.Message.Split('\r')[0]}");

        using (var writer = new PcapWriter(path))
        {
            writer.Attach(caller);
            var call = await caller.CallAsync(Lab.Uri(callee, "peer")).WaitAsync(TimeSpan.FromSeconds(10));
            call.SendAudio(Voice(1500), 16000);
            await Task.Delay(1600);
            await call.HangupAsync();
            await Task.Delay(300);
        }

        var sip = PcapReader.ReadUdp(path).Count();
        SetMetric("pcap", $"{Path.GetFileName(path)} · {sip} SIP packets");
        lock (counts)
        {
            SetMetric("voipnet.calls.started", counts.GetValueOrDefault("voipnet.calls.started").ToString(CultureInfo.InvariantCulture));
            SetMetric("voipnet.call.mos", $"{counts.GetValueOrDefault("voipnet.call.mos"):0.00}");
            SetMetric("voipnet.call.duration", $"{counts.GetValueOrDefault("voipnet.call.duration"):0.0} s");
        }
    }

    private void AnalyzeSynthetic()
    {
        var analyzer = new RtpStreamAnalyzer();
        var start = DateTimeOffset.UtcNow;
        var random = new Random(7);
        var source = new IPEndPoint(IPAddress.Parse("10.1.0.20"), 40002);
        var destination = new IPEndPoint(IPAddress.Parse("10.1.0.5"), 18000);
        for (var n = 0; n < 500; n++)
        {
            if (random.NextDouble() < 0.02)
            {
                continue;
            }

            var packet = new byte[172];
            packet[0] = 0x80;
            packet[1] = 8;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(1000 + n));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)(n * 160));
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x5EED1234);
            var arrival = start.AddMilliseconds((n * 20) + (random.NextDouble() * 18));
            analyzer.Add(new UdpDatagram(arrival, source, destination, packet));
        }

        var report = analyzer.Reports()[0];
        Write($"stream 0x{report.Ssrc:X8} {report.Source} → {report.Destination}");
        SetMetric("Codec", report.Codec);
        SetMetric("Packets", $"{report.Packets} of {report.Expected}");
        SetMetric("Loss", $"{report.Lost} ({report.LossPercent:0.0}%)");
        SetMetric("Jitter", $"{report.JitterMs:0.0} ms (max {report.MaxJitterMs:0.0})");
        SetMetric("MOS", $"{report.Mos:0.00}");
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}
