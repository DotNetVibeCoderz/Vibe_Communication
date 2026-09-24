# Tools, diagnostics and samples

🇮🇩 [Bahasa Indonesia](../id/tools-and-diagnostics.md) · Made by Gravicode Studios, led by Kang Fadhil

## The `voipnet` CLI

```bash
dotnet tool install -g VoipNet.Cli
voipnet --help
```

| Command | Purpose |
| --- | --- |
| `voipnet version` | SDK, engine and runtime versions |
| `voipnet sip ping <uri> [--count 4]` | OPTIONS round-trip times and the responder's User-Agent |
| `voipnet sip register -d pbx -u 1001 -p secret` | test credentials against a registrar |
| `voipnet sip call <uri> [--duration 10] [--tone 440] [--dtmf 123] [--record out.wav] [--register] [--video clip.h264] [--video-fps 15]` | place a test call and watch MOS, loss, jitter live; `--video` streams an H.264 file and reports the video that comes back |
| `voipnet sip listen [--sip-port 5060] [--echo] [--video]` | answer calls; `--echo` turns it into an echo test service, `--video` accepts (and echoes) video |
| `voipnet sip message <uri> "text"` | send a SIP MESSAGE |
| `voipnet load <uri> [-n 20] [--concurrency 4] [--cps 2] [--duration 5]` | place calls at a steady rate and report setup times, failures and media quality |
| `voipnet rtp analyze capture.pcap [--json]` | loss, jitter, ordering and MOS for every RTP stream in a capture |
| `voipnet rtp listen --port 40000 [--seconds 30]` | receive RTP on a port and analyse it live |

Common options for `sip` commands: `--domain`, `--user`, `--password`, `--proxy`, `--transport udp|tcp|tls|ws|wss`, `--tls-pin fingerprint`, `--tls-insecure`, `--srtp`, `--dtls` (DTLS-SRTP keys), `--bind`, `--port`, `--trace` (print SIP), `--pcap file` (write SIP to a capture).

Example — an echo test in two terminals:

```bash
voipnet sip listen --sip-port 5090 --echo
voipnet sip call sip:echo@127.0.0.1:5090 --duration 5 --dtmf 123 --pcap call.pcap
```

```
Connected codec G722 @ 16000 Hz
╭─────────────────────────┬─────────────────╮
│ MOS (estimated)         │            4.38 │
│ Packets sent / received │       194 / 174 │
│ Lost / late             │    0 / 0 (0.0%) │
│ Jitter                  │          0.2 ms │
╰─────────────────────────┴─────────────────╯
```

Example — 50 calls at 5 per second, 10 at a time:

```bash
voipnet load sip:echo@pbx.example.com -n 50 --cps 5 --concurrency 10 --duration 8
```

```
╭───────────────────────┬────────────────────╮
│ Calls placed          │ 50                 │
│ Connected             │ 50                 │
│ Failed                │ 0                  │
│ Achieved rate         │ 4.91 calls/s       │
│ Peak concurrent       │ 10                 │
│ Setup p50 / p95 / max │ 112 / 186 / 233 ms │
│ MOS (average)         │ 4.31               │
│ Packet loss (average) │ 0.04 %             │
╰───────────────────────┴────────────────────╯
```

The achieved rate is what the far end really saw: concurrency and call duration cap it, so a rate
below the one you asked for means calls were queueing behind the limit. The command exits non-zero
when any call failed, which is what a CI job wants. MOS is only scored when the target sends audio
back — point the run at an echo service or an IVR, not at something that answers in silence.

Example — a video echo test, with a clip made by ffmpeg:

```bash
ffmpeg -f lavfi -i testsrc=size=320x240:rate=15:duration=3 -c:v libx264 -g 15 -f h264 clip.h264
voipnet sip listen --echo --video
voipnet sip call sip:echo@127.0.0.1:5060 --duration 4 --tone 0 --video clip.h264
```

```
Video: H264
╭────────────────────┬────────────╮
│ Frames / keyframes │     52 / 6 │
│ Frame rate         │   12.4 fps │
│ Bitrate            │ 150 kbit/s │
│ Keyframe every     │   9 frames │
│ Longest freeze     │      82 ms │
╰────────────────────┴────────────╯
```

The file is read as Annex B and sent one access unit per frame, looping until the call ends; parameter
sets travel with the keyframe they describe, so the far end can start decoding on it. What comes back is
measured the way a viewer would notice it: frame rate, bitrate, how often a keyframe arrives (that is how
long a late joiner waits for a picture) and the longest gap between frames, which is a freeze.

## Diagnostics in code

### SIP capture

```csharp
var client = new VoipClient(new VoipClientOptions { TraceSip = true, … });
using var capture = new PcapWriter("sip.pcap");
capture.Attach(client);         // open the file in Wireshark: SIP is decoded
client.SipTrace += (_, e) => logger.LogDebug("{Dir} {Remote}\n{Msg}", e.Outgoing ? "→" : "←", e.RemoteEndPoint, e.Message);
```

### RTP analysis

```csharp
foreach (var s in RtpStreamAnalyzer.AnalyzeFile("trunk.pcap"))
    Console.WriteLine($"0x{s.Ssrc:X8} {s.Codec} {s.Source}→{s.Destination} loss {s.LossPercent:0.0}% jitter {s.JitterMs:0.0} ms MOS {s.Mos:0.00}");

var live = new RtpStreamAnalyzer();
live.Add(new UdpDatagram(DateTimeOffset.UtcNow, remote, local, packet));
```

libpcap files with Ethernet, raw IP, Linux cooked or loopback link types are supported (convert pcapng with `editcap -F pcap`).

### Metrics

Meter `VoipNet`:

| Instrument | Type |
| --- | --- |
| `voipnet.calls.started` (tag `direction`) | counter |
| `voipnet.calls.answered` | counter |
| `voipnet.calls.failed` (tag `code`) | counter |
| `voipnet.calls.active` | up-down counter |
| `voipnet.call.duration` (s) | histogram |
| `voipnet.call.mos` | histogram |
| `voipnet.call.packet_loss` (%) | histogram |
| `voipnet.registrations` (tag `state`) | counter |

```bash
dotnet-counters monitor --counters VoipNet -n VoipNet.CallCenter
```

Or export with OpenTelemetry: `builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("VoipNet"))`.

### Tracing

Activity source `VoipNet`. Every call is a span (`sip.call`) that opens when the call is placed or
arrives and closes when it ends, tagged with the direction, the remote URI, the final status code, the
codec and the quality the call finished with (`voip.mos`, `voip.loss_percent`, `voip.jitter_ms`). An
outbound call continues whatever activity placed it, so a call appears under the request that caused
it. Voice agents add a child span per turn (`voip.agent.turn`) with the model and an event when the
caller interrupts.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(VoipTelemetry.ActivitySourceName).AddOtlpExporter())
    .WithMetrics(m => m.AddMeter(VoipMetrics.MeterName));
```

Nothing is recorded while no listener is attached; `VoipTelemetry.Enabled` says whether one is.

### Logging

Pass an `ILogger<VoipClient>`; engine log events (NAT discovery, send failures, media errors) are forwarded at matching levels.

## Samples

### Softphone (Avalonia)

![Softphone](../images/softphone-idle.png)

- Demo mode starts two in-process lines: **echo** (hear yourself) and **music** (a melody that answers keypad presses with tones).
- Live audio traces for both directions, codec and MOS, mute/hold/keypad/record/transfer.
- Notes panel with **Summarise with AI** (any OpenAI-compatible endpoint).
- `dotnet run --project samples/VoipNet.Softphone -- --screenshot docs/images` renders the documentation screenshots headlessly.

### Gallery (Avalonia)

![Gallery](../images/gallery-call-center.png)

Thirteen live pages: overview, place a call, codecs, DTMF, hold and transfer, conference, recording, SRTP, language models (with a real tool call), voice agent, IVR builder, queues and agents, diagnostics. Each page shows the C# that does what the demo does. Configure models with `VOIPNET_AI_ENDPOINT`, `VOIPNET_AI_KEY`, `VOIPNET_AI_MODEL`.

### Call Centre (Blazor Server)

A PBX, five agent softphones and a Poisson traffic generator, all real SIP/RTP on loopback. Wallboard with service level, waiting callers drawn against their target, agent board, live call quality, events, traffic controls and an **AI supervisor**; a Recordings page with in-browser playback.

### IVR Studio (Blazor Server)

Edit menus, options and AI instructions; see the call path; press keys on a phone in the browser that dials the flow for real; type as the caller once the IVR hands off to the AI agent. Flows are saved to `App_Data/flow.json` and exported at `/flow.json`.

### WebPhone (Blazor Server)

![WebRTC gateway](../images/webphone-call.png)

A WebRTC gateway on one machine. The page's script is a small SIP-over-WebSocket client: it calls the gateway endpoint (`ws://host:5090`) with an `RTCPeerConnection` offer, and the gateway answers with ICE and DTLS-SRTP, then relays the audio to an ordinary SIP/UDP call to a desk phone (an echo or a tone player). The signal path at the top lights up hop by hop; both legs show codec, encryption, packets and MOS, next to what the browser itself reports from `getStats()`.

### Realtime Agent (console)

```bash
dotnet run --project samples/VoipNet.RealtimeAgent             # answer SIP calls on :5070
dotnet run --project samples/VoipNet.RealtimeAgent -- --demo   # scripted caller + real model
```

Configure providers in `appsettings.json` (`AI:Chat`, `AI:SpeechToText`, `AI:TextToSpeech`, `AI:Realtime`) and set `Agent:Mode` to `pipeline` or `realtime`.

### Documentation screenshots

`tools/VoipNet.DocShots` drives headless Edge/Chrome (DevTools protocol) or Firefox (WebDriver BiDi, `--firefox [path]`) to capture the Blazor samples. The `webphone` scenario is also a browser interop test: it prints the browser's WebRTC statistics and exits with 1 when the call does not carry encrypted audio both ways.

```bash
dotnet run --project tools/VoipNet.DocShots -- ivrstudio http://127.0.0.1:5209 docs/images
dotnet run --project tools/VoipNet.DocShots -- callcenter http://127.0.0.1:5184 docs/images
dotnet run --project tools/VoipNet.DocShots -- webphone http://localhost:5190 docs/images
dotnet run --project tools/VoipNet.DocShots -- webphone http://localhost:5190 out --firefox "C:\Program Files\Mozilla Firefox\firefox.exe"
```
