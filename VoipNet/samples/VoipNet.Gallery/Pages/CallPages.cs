using VoipNet.Audio;
using VoipNet.Gallery.Infrastructure;

namespace VoipNet.Gallery.Pages;

public sealed class OverviewPage : DemoPage
{
    public OverviewPage()
        : base("START", "Overview", "Voip.NET pairs a Rust SIP/RTP engine with a .NET API, AI conversation pipelines and contact-centre building blocks. Every page in this gallery runs the real engine on your machine.")
    {
        SetMetric("Native engine", VoipClient.EngineVersion);
        SetMetric(".NET runtime", Environment.Version.ToString());
        SetMetric("OS", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        SetMetric("Audio devices", AudioDevices.IsAvailable ? $"{AudioDevices.Playback().Count} out · {AudioDevices.Capture().Count} in" : "not available");
        Write("Made by Gravicode Studios — led by Kang Fadhil.");
        Write("Pick a page on the left. Each demo shows its C# on the right.");
    }

    public override string Code => """
        // dotnet add package VoipNet.Core
        await using var client = new VoipClient(new VoipClientOptions
        {
            Domain = "pbx.example.com",
            Username = "1001",
            Password = "secret",
            RegisterOnStart = true,
        });
        await client.StartAsync();

        var call = await client.CallAsync("sip:1002@pbx.example.com");
        call.SendAudio(pcm16k, sampleRate: 16000);
        await call.HangupAsync();

        // Architecture
        //  .NET: VoipClient · VoipCall · AI agents · IVR · queues
        //    │  P/Invoke (C ABI, zero-copy audio callbacks)
        //  Rust: SIP UA · SDP · RTP/SRTP · jitter buffer · codecs · ICE/STUN/TURN
        """;
}

public sealed class FirstCallPage : DemoPage
{
    private readonly Lab _lab = new();
    private VoipCall? _call;

    public FirstCallPage()
        : base("CALLS", "Place a call", "Two endpoints on the loopback interface: Alice calls Bob, Bob answers, and Alice sends audio. The trace shows what Bob actually receives.")
    {
        AddAction("Call Bob", CallAsync, "go");
        AddAction("Hang up", HangupAsync, "danger");
    }

    public override string Code => """
        var alice = new VoipClient(new VoipClientOptions { Username = "alice" });
        var bob = new VoipClient(new VoipClientOptions { Username = "bob" });
        await alice.StartAsync();
        await bob.StartAsync();

        bob.IncomingCall += async (_, e) => await e.Call.AnswerAsync();

        var call = await alice.CallAsync($"sip:bob@{bob.LocalAddress}");
        Console.WriteLine($"{call.Codec} @ {call.SampleRate} Hz");

        call.SendAudio(samples, sampleRate: 16000);   // resampled to the codec
        var stats = call.GetStatistics();              // MOS, loss, jitter
        await call.HangupAsync();
        """;

    private async Task CallAsync()
    {
        await ResetAsync();
        var alice = await _lab.StartAsync("alice");
        var bob = await _lab.StartAsync("bob");
        Lab.AutoAnswer(bob);
        bob.IncomingCall += (_, e) =>
        {
            Write($"Bob: incoming call from {e.From}");
            TraceAudio(e.Call);
        };

        Write($"Alice ({alice.LocalAddress}) calls Bob ({bob.LocalAddress})");
        _call = alice.Call(Lab.Uri(bob, "bob"));
        _call.StateChanged += (_, e) => Write($"Alice: {e.State} {(e.StatusCode > 0 ? e.StatusCode : string.Empty)} {e.Reason}");
        await _call.Connected.WaitAsync(TimeSpan.FromSeconds(10));

        SetMetric("Codec", $"{_call.Codec} · {_call.SampleRate} Hz");
        _call.SendAudio(Voice(6000), 16000);
        Write("Alice is speaking for six seconds…");
    }

    private async Task HangupAsync()
    {
        if (_call is { IsActive: true })
        {
            await _call.HangupAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (_call.FinalStatistics is { } s)
            {
                SetMetric("Final MOS", $"{s.Mos:0.00}");
                SetMetric("Packets", $"{s.PacketsSent} sent");
            }
        }
    }

    public override void Tick()
    {
        base.Tick();
        if (_call is { IsActive: true })
        {
            var s = _call.GetStatistics();
            SetMetric("Duration", _call.Duration.ToString(@"mm\:ss"));
            SetMetric("MOS", $"{s.Mos:0.00}");
            SetMetric("Jitter", $"{s.JitterMs:0.0} ms · buffer {s.JitterBufferMs} ms");
            SetMetric("Queued audio", $"{s.OutboundQueuedMs} ms");
        }
    }

    public override async Task ResetAsync()
    {
        _call = null;
        await _lab.ResetAsync();
    }
}

public sealed class CodecsPage : DemoPage
{
    private readonly Lab _lab = new();

    public CodecsPage()
        : base("CALLS", "Codecs", "Offer one codec at a time and see what gets negotiated and how it sounds on the wire. Opus is fullband (48 kHz) at about 32 kbit/s with loss recovery; G.722 is wideband (16 kHz); G.711 μ-law and A-law are the narrowband standard; L16 is uncompressed.")
    {
        foreach (var codec in new[] { "opus", "G722", "PCMU", "PCMA", "L16" })
        {
            AddAction(codec, () => TryAsync(codec), codec == "opus" ? "primary" : "quiet");
        }
    }

    public override string Code => """
        var options = new VoipClientOptions
        {
            // Preference order. Native: opus, G722, PCMU, PCMA, L16.
            // G729, H264, VP8… negotiate as pass-through:
            // use call.EncodedReceived / call.SendEncoded.
            AudioCodecs = ["opus", "G722", "PCMU", "PCMA"],
            PtimeMs = 20,
            JitterMinMs = 40,
            JitterMaxMs = 300,
        };
        """;

    private async Task TryAsync(string codec)
    {
        await _lab.ResetAsync();
        var caller = await _lab.StartAsync("caller", o => o.AudioCodecs = [codec]);
        var callee = await _lab.StartAsync("callee");
        Lab.AutoAnswer(callee, 100);
        VoipCall? remote = null;
        callee.IncomingCall += (_, e) =>
        {
            remote = e.Call;
            TraceAudio(e.Call);
        };

        var call = await caller.CallAsync(Lab.Uri(callee, "callee")).WaitAsync(TimeSpan.FromSeconds(10));
        call.SendAudio(Voice(1500, 240), 16000);
        await Task.Delay(1700);
        var stats = remote!.GetStatistics();
        Write($"{codec}: negotiated {call.Codec} @ {call.SampleRate} Hz, {stats.BytesReceived / Math.Max(stats.PacketsReceived, 1)} bytes per packet");
        SetMetric("Negotiated", $"{call.Codec} · {call.SampleRate} Hz");
        SetMetric("Payload per 20 ms", $"{stats.BytesReceived / Math.Max(stats.PacketsReceived, 1)} bytes");
        SetMetric("Bitrate", $"{stats.BytesReceived * 8 / Math.Max(stats.PacketsReceived * 20, 1)} kbit/s");
        SetMetric("MOS", $"{stats.Mos:0.00}");
        await call.HangupAsync();
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}

public sealed class DtmfPage : DemoPage
{
    private readonly Lab _lab = new();

    public DtmfPage()
        : base("CALLS", "DTMF", "Keypad digits travel three ways: RTP telephone-events (RFC 4733), SIP INFO, or audible tones that the receiver detects with a Goertzel filter.")
    {
        AddAction("Send 1234# as RFC 4733", () => SendAsync(DtmfMode.Rfc4733));
        AddAction("Send via SIP INFO", () => SendAsync(DtmfMode.SipInfo), "quiet");
        AddAction("Send in-band tones", () => SendAsync(DtmfMode.InBand), "quiet");
    }

    public override string Code => """
        var sender = new VoipClient(new VoipClientOptions { DtmfMode = DtmfMode.Rfc4733 });
        var receiver = new VoipClient(new VoipClientOptions { DetectInbandDtmf = true });

        receiver.DtmfReceived += (_, e) =>
            Console.WriteLine($"{e.Digit} via {e.Source}");   // Rfc4733 · SipInfo · InBand

        call.SendDtmf("1234#", durationMs: 120);
        """;

    private async Task SendAsync(DtmfMode mode)
    {
        await _lab.ResetAsync();
        var sender = await _lab.StartAsync("keypad", o => o.DtmfMode = mode);
        var receiver = await _lab.StartAsync("ivr", o => o.DetectInbandDtmf = true);
        Lab.AutoAnswer(receiver, 100);
        var received = string.Empty;
        receiver.DtmfReceived += (_, e) =>
        {
            received += e.Digit;
            Write($"received {e.Digit} via {e.Source}");
            SetMetric("Received", received);
        };
        receiver.IncomingCall += (_, e) => TraceAudio(e.Call);

        var call = await sender.CallAsync(Lab.Uri(receiver, "ivr")).WaitAsync(TimeSpan.FromSeconds(10));
        Write($"sending 1234# using {mode}");
        SetMetric("Mode", mode.ToString());
        SetMetric("Received", "…");
        call.SendDtmf("1234#", 100);
        await Task.Delay(2200);
        await call.HangupAsync();
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}

public sealed class HoldTransferPage : DemoPage
{
    private readonly Lab _lab = new();

    public HoldTransferPage()
        : base("CALLS", "Hold and transfer", "Hold uses a re-INVITE with sendonly media; the other side sees RemoteHold. Blind transfer sends REFER: Bob calls Carol himself and Alice drops out when Carol answers.")
    {
        AddAction("Run the scenario", RunAsync, "go");
    }

    public override string Code => """
        call.SetHold(true);          // re-INVITE a=sendonly → remote state RemoteHold
        call.SetHold(false);         // back to sendrecv

        call.Transfer("sip:carol@pbx");          // blind transfer (REFER)
        call.TransferTo(consultationCall);       // attended transfer (REFER + Replaces)

        client.TransferProgress += (_, e) =>
            Console.WriteLine($"transfer: {e.StatusCode} {e.Reason}");
        """;

    private async Task RunAsync()
    {
        await _lab.ResetAsync();
        var alice = await _lab.StartAsync("alice");
        var bob = await _lab.StartAsync("bob");
        var carol = await _lab.StartAsync("carol");
        Lab.AutoAnswer(bob, 100);
        Lab.AutoAnswer(carol, 600);
        bob.CallStateChanged += (_, e) => Write($"Bob   · {e.Call.RemoteUri} → {e.State}");
        carol.IncomingCall += (_, e) => { Write($"Carol · ringing, call from {e.From}"); TraceAudio(e.Call); };
        alice.TransferProgress += (_, e) => Write($"Alice · transfer progress {e.StatusCode} {e.Reason}");

        var call = await alice.CallAsync(Lab.Uri(bob, "bob")).WaitAsync(TimeSpan.FromSeconds(10));
        Write("Alice · connected to Bob");
        call.SetHold(true);
        await Task.Delay(800);
        SetMetric("Alice", call.State.ToString());
        Write("Alice · resumes");
        call.SetHold(false);
        await Task.Delay(800);

        Write("Alice · transfers Bob to Carol");
        call.Transfer(Lab.Uri(carol, "carol"));
        await call.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        SetMetric("Alice", "left the call");
        await Task.Delay(500);
        var bobToCarol = bob.Calls.FirstOrDefault(c => c.IsActive);
        SetMetric("Bob ↔ Carol", bobToCarol?.State.ToString() ?? "not connected");
        bobToCarol?.SendAudio(Voice(2500), 16000);
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}

public sealed class ConferencePage : DemoPage
{
    private readonly Lab _lab = new();

    public ConferencePage()
        : base("CALLS", "Conference", "The host bridges two calls. The engine mixes at 16 kHz and sends each participant everyone except themselves (mix-minus).")
    {
        AddAction("Start a three-way call", RunAsync, "go");
    }

    public override string Code => """
        var leg1 = await host.CallAsync("sip:bob@pbx");
        var leg2 = await host.CallAsync("sip:carol@pbx");

        using var conference = host.CreateConference();
        conference.Add(leg1);
        conference.Add(leg2);    // Bob and Carol now hear each other

        // Supervisors can listen in without being heard
        conference.Add(supervisorCall);
        supervisorCall.SetHold(true);   // listen-only: they hear the mix, the mix ignores them
        """;

    private async Task RunAsync()
    {
        await _lab.ResetAsync();
        var host = await _lab.StartAsync("host");
        var bob = await _lab.StartAsync("bob");
        var carol = await _lab.StartAsync("carol");
        Lab.AutoAnswer(bob, 50);
        Lab.AutoAnswer(carol, 50);
        VoipCall? bobLeg = null;
        bob.IncomingCall += (_, e) => bobLeg = e.Call;
        carol.IncomingCall += (_, e) => TraceAudio(e.Call);

        var toBob = await host.CallAsync(Lab.Uri(bob, "bob")).WaitAsync(TimeSpan.FromSeconds(10));
        var toCarol = await host.CallAsync(Lab.Uri(carol, "carol")).WaitAsync(TimeSpan.FromSeconds(10));
        var conference = host.CreateConference();
        conference.Add(toBob);
        conference.Add(toCarol);
        Write($"conference #{conference.Id} with {conference.Participants.Count} participants");
        SetMetric("Participants", conference.Participants.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await Task.Delay(300);
        Write("Bob speaks; the trace is what Carol hears through the bridge");
        bobLeg?.SendAudio(Voice(5000, 150), 16000);
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}

public sealed class RecordingPage : DemoPage
{
    private readonly Lab _lab = new();

    public RecordingPage()
        : base("CALLS", "Recording", "CallRecorder writes both directions to WAV or MP3 — stereo keeps caller and agent on separate channels for quality review and transcription.")
    {
        AddAction("Record a 3-second call", RunAsync, "go");
    }

    public override string Code => """
        using var recorder = CallRecorder.Start(
            call,
            "calls/2026-09-16-support.mp3",
            RecordingFormat.Mp3,
            RecordingLayout.Stereo);   // left: caller, right: this side

        // …or record every call automatically:
        var recordings = new RecordingService(client, new RecordingOptions
        {
            Directory = "recordings",
            Retention = TimeSpan.FromDays(90),
        });
        """;

    private async Task RunAsync()
    {
        await _lab.ResetAsync();
        var caller = await _lab.StartAsync("caller");
        var agent = await _lab.StartAsync("agent");
        Lab.AutoAnswer(agent, 50);
        VoipCall? agentLeg = null;
        agent.IncomingCall += (_, e) => agentLeg = e.Call;

        var call = await caller.CallAsync(Lab.Uri(agent, "agent")).WaitAsync(TimeSpan.FromSeconds(10));
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-gallery-{DateTime.Now:HHmmss}.wav");
        var recorder = CallRecorder.Start(call, path, RecordingFormat.Wav, RecordingLayout.Stereo);
        TraceAudio(call, AudioDirection.Outbound);
        call.SendAudio(Voice(3000, 170), 16000);
        await Task.Delay(1200);
        agentLeg?.SendAudio(Voice(1500, 260), 16000);
        await Task.Delay(2000);
        await call.HangupAsync();
        recorder.Dispose();

        SetMetric("File", Path.GetFileName(recorder.Path));
        SetMetric("Duration", $"{recorder.Duration.TotalSeconds:0.0} s");
        SetMetric("Size", $"{new FileInfo(recorder.Path).Length / 1024} KB");
        Write($"saved {recorder.Path}");
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}

public sealed class SecurityPage : DemoPage
{
    private readonly Lab _lab = new();

    public SecurityPage()
        : base("CALLS", "TLS & SRTP", "Signaling over TLS with certificate pinning, and media encryption with AES-CM-128 and HMAC-SHA1-80 keyed through SDP (SDES). A peer that insists on encryption is refused by one that disables it, with 488 Not Acceptable Here.")
    {
        AddAction("Mandatory ↔ optional", () => RunAsync(SrtpMode.Optional), "go");
        AddAction("Mandatory ↔ disabled", () => RunAsync(SrtpMode.Disabled), "quiet");
        AddAction("TLS + SRTP, pinned", RunTlsAsync, "go");
    }

    public override string Code => """
        var secure = new VoipClient(new VoipClientOptions
        {
            Srtp = SrtpMode.Mandatory,     // RTP/SAVP + a=crypto
            Transport = SipTransport.Tls,  // SIPS signaling, port 5061
            TlsPinnedFingerprints = ["3F:A2:…"],
        });

        var stats = call.GetStatistics();
        Console.WriteLine(stats.SecureRtp ? "encrypted" : "plain RTP");
        """;

    private async Task RunAsync(SrtpMode calleeMode)
    {
        await _lab.ResetAsync();
        var caller = await _lab.StartAsync("secure", o => o.Srtp = SrtpMode.Mandatory);
        var callee = await _lab.StartAsync("peer", o => o.Srtp = calleeMode);
        Lab.AutoAnswer(callee, 50);
        callee.IncomingCall += (_, e) => TraceAudio(e.Call);

        Write($"caller requires SRTP, callee is {calleeMode}");
        var call = caller.Call(Lab.Uri(callee, "peer"));
        var outcome = await Task.WhenAny(call.Connected, call.Completion).WaitAsync(TimeSpan.FromSeconds(10));
        if (outcome == call.Completion)
        {
            var ended = await call.Completion;
            Write($"refused: {ended.StatusCode} {ended.Reason}");
            SetMetric("Result", $"{ended.StatusCode} — media policies are incompatible");
            SetMetric("Media", "none");
            return;
        }

        call.SendAudio(Voice(2000), 16000);
        await Task.Delay(1500);
        var stats = call.GetStatistics();
        SetMetric("Result", "connected");
        SetMetric("Media", stats.SecureRtp ? "SRTP · AES_CM_128_HMAC_SHA1_80" : "plain RTP");
        Write($"SRTP active: {stats.SecureRtp}");
        await call.HangupAsync();
    }

    private async Task RunTlsAsync()
    {
        await _lab.ResetAsync();
        var callee = await _lab.StartAsync("peer", o => { o.Transport = SipTransport.Tls; o.Srtp = SrtpMode.Mandatory; });
        Write($"callee certificate {callee.TlsFingerprint}");
        var caller = await _lab.StartAsync("secure", o =>
        {
            o.Transport = SipTransport.Tls;
            o.Srtp = SrtpMode.Mandatory;
            o.TlsPinnedFingerprints = [callee.TlsFingerprint!];
        });
        Lab.AutoAnswer(callee, 50);
        callee.IncomingCall += (_, e) => TraceAudio(e.Call);

        var call = caller.Call(Lab.Uri(callee, "peer"));
        await call.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        call.SendAudio(Voice(2000), 16000);
        await Task.Delay(1500);
        var stats = call.GetStatistics();
        SetMetric("Result", "connected over TLS");
        SetMetric("Signaling", "TLS · pinned SHA-256");
        SetMetric("Media", stats.SecureRtp ? "SRTP · AES_CM_128_HMAC_SHA1_80" : "plain RTP");
        Write("SIP over TLS, SDES keys never cross the wire in clear text");
        await call.HangupAsync();
    }

    public override Task ResetAsync() => _lab.ResetAsync();
}
