# Voip.NET

**A Rust-powered, .NET-friendly VoIP + AI platform.** SIP, RTP and SRTP from a native engine; calls, conversations and contact-centre building blocks from a modern .NET 10 API; speech and language models wired straight into the audio path.

> Made by **Gravicode Studios**, led by **Kang Fadhil**.
> 🇮🇩 Baca dalam Bahasa Indonesia: [README.id.md](README.id.md)

![Softphone sample during a call](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/softphone-call.png)

```csharp
await using var client = new VoipClient(new VoipClientOptions
{
    Domain = "pbx.example.com", Username = "1001", Password = "secret", RegisterOnStart = true,
});
await client.StartAsync();

var call = await client.CallAsync("sip:1002@pbx.example.com");
call.SendAudio(pcm16k, sampleRate: 16000);
Console.WriteLine($"{call.Codec} · MOS {call.GetStatistics().Mos:0.0}");
await call.HangupAsync();
```

## Why Voip.NET

- **Native where it matters.** The SIP user agent, SDP negotiation, RTP/RTCP, SRTP, the adaptive jitter buffer, codecs and ICE live in Rust. Audio crosses into .NET through zero-copy callbacks.
- **.NET where you work.** `async`/`await`, events, `IAsyncEnumerable` audio streams, dependency injection, `System.Diagnostics.Metrics`.
- **AI in the call, not beside it.** Speech-to-text, any `IChatClient` model, text-to-speech and barge-in run as one loop on the live call. Realtime speech-to-speech models are supported too.
- **Contact centre included.** IVR flows with AI hand-off, queues with skill routing, bridged agent legs, recording, analytics and CRM tools.

## Packages

| Package | What it gives you |
| --- | --- |
| `VoipNet.Core` | `VoipClient`, `VoipCall`, conferences, DTMF, SRTP and DTLS-SRTP, TLS and WebSocket signaling, diagnostics (pcap, RTP analyser, metrics). Contains the native engine. |
| `VoipNet.Audio` | Microphone and speakers (OpenAL, cross-platform), WAV/MP3 call recording, resampling, voice activity detection, tones. |
| `VoipNet.Video` | H.264 encoding and decoding, camera and screen capture through the platform's own codec (Media Foundation on Windows), a test pattern for machines with neither, a grid/PiP compositor, and BGRA/NV12 conversion. |
| `VoipNet.AI` | Chat clients for OpenAI, Azure OpenAI, DeepSeek/OpenAI-compatible, Anthropic Claude and Google Gemini; speech providers; `VoiceAgent`; `RealtimeVoiceAgent`. |
| `VoipNet.Enterprise` | IVR builder and runner, `CallCenterService` (queues, agents, routing), `RecordingService`, `ConferenceRecorder`, CRM tool set. |
| `VoipNet.Cli` | `voipnet` .NET tool: SIP ping/register/call/listen/message and RTP analysis. |

## Features

| Area | Status |
| --- | --- |
| SIP: REGISTER (digest auth, refresh, NAT keep-alive), INVITE/ACK/BYE/CANCEL, re-INVITE hold, REFER blind and attended (Replaces), OPTIONS, INFO, MESSAGE, NOTIFY | ✅ |
| Transports: UDP, TCP, TLS (certificate pinning), WebSocket `ws`/`wss` (RFC 7118) | ✅ |
| RTP/RTCP with adaptive jitter buffer, packet-loss concealment, symmetric RTP | ✅ |
| Codecs: Opus (48 kHz, in-band FEC), G.722, G.711 μ-law/A-law, L16 (native) · H.264 encoded and decoded through the platform codec (`VoipNet.Video`, Windows so far) · G.729, SILK, Speex, VP8, VP9 negotiated as pass-through | ✅ |
| DTMF: RFC 4733, SIP INFO, in-band generation and detection | ✅ |
| SRTP: AES-CM-128-HMAC-SHA1-80, AEAD-AES-128/256-GCM; keys via SDES or DTLS-SRTP | ✅ |
| WebRTC browsers calling SIP (SIP over WebSocket, ICE, DTLS-SRTP, data channels), verified with Edge and Firefox | ✅ |
| ICE (RFC 8445): candidate pairs, nomination, role conflicts, peer-reflexive candidates, trickle ICE, ICE restart, consent freshness · STUN, TURN | ✅ |
| Conferencing with mix-minus, speaker-focus video forwarding, simulcast both ways, screen sharing to the whole room | ✅ |
| Recording WAV/MP3, stereo or mono · video calls to MP4 or AVI · a whole conference as one composed picture | ✅ |
| LLMs: OpenAI, Azure OpenAI, DeepSeek & compatible servers, Anthropic, Gemini — streaming and tool calling | ✅ |
| STT: Deepgram and Amazon Transcribe (streaming), OpenAI, Google Cloud, ElevenLabs, ElBruno.Realtime · TTS: ElevenLabs, OpenAI, Google Cloud, Amazon Polly, ElBruno.Realtime | ✅ |
| Voice agent: barge-in, sentence streaming, call-control tools, conversation memory, hand-off · Realtime speech-to-speech agents (OpenAI, Azure OpenAI, Gemini Live) | ✅ |
| IVR builder, queues & skill routing, supervisor listen-in, recording service, metrics, CRM tools | ✅ |
| Video: H.264 encode and decode, camera and screen capture, a grid compositor, a test pattern (`VoipNet.Video`, Windows so far) | ✅ |
| Diagnostics: SIP to pcap, RTP stream analyser, `dotnet-counters` metrics | ✅ |
| Platforms: Windows x64 built and tested; Linux/macOS build from source | ✅ / see [building](docs/en/building.md) |

The full list, including what is planned, lives in [PLAN.md](PLAN.md) and [Progress.md](Progress.md).

## Samples

| Sample | Kind | Shows |
| --- | --- | --- |
| [VoipNet.Softphone](samples/VoipNet.Softphone) | Avalonia desktop | Dial pad, live call with audio traces, camera in and out, hold/mute/transfer/record, AI call summaries, built-in demo lines. |
| [VoipNet.Gallery](samples/VoipNet.Gallery) | Avalonia desktop | Every SDK feature as a live demo with its C#. |
| [VoipNet.CallCenter](samples/VoipNet.CallCenter) | Blazor Server | Wallboard: queues, agents, live call quality, recordings, AI supervisor. |
| [VoipNet.IvrStudio](samples/VoipNet.IvrStudio) | Blazor Server | Design an IVR, call it from the browser, hand the caller to an AI agent. |
| [VoipNet.WebPhone](samples/VoipNet.WebPhone) | Blazor Server | WebRTC gateway: the browser calls over SIP WebSocket and DTLS-SRTP, with video and a data channel, bridged to a SIP phone over UDP. |
| [VoipNet.Meeting](samples/VoipNet.Meeting) | Blazor Server | Meeting room: every browser joins one conference, audio mixed minus the listener and the speaker's camera forwarded to everyone else. |
| [VoipNet.RealtimeAgent](samples/VoipNet.RealtimeAgent) | Console | An AI agent that answers SIP calls (pipeline or realtime model). |

| | |
| --- | --- |
| ![Gallery: voice agent](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/gallery-voice-agent.png) | ![Call centre wallboard](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/callcenter-ai-supervisor.png) |
| ![IVR Studio test call](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/ivrstudio-test-call.png) | ![Gallery: language models](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/gallery-ai-models.png) |

![WebRTC gateway: a browser call bridged to SIP](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/webphone-call.png)

## Quick start

```bash
# Requirements: .NET 10 SDK, Rust 1.80+ (only to build the engine from source)
git clone https://github.com/DotNetVibeCoderz/Vibe_Communication && cd Vibe_Communication/VoipNet
./build/build.ps1            # or ./build/build.sh on Linux/macOS
dotnet run --project samples/VoipNet.Softphone
```

Then read:

- [Getting started](docs/en/getting-started.md)
- [Architecture](docs/en/architecture.md)
- [Calls and media](docs/en/calls-and-media.md)
- [AI: models, speech and agents](docs/en/ai.md)
- [Contact centre: IVR, queues, recording, CRM](docs/en/contact-centre.md)
- [Tools, diagnostics and samples](docs/en/tools-and-diagnostics.md)
- [Configuration reference](docs/en/reference.md)
- [Building and packaging](docs/en/building.md)

## Credits

Voip.NET is made by **Gravicode Studios**, led by **Kang Fadhil**. Licensed under the [MIT License](LICENSE). Sample apps use IBM Plex (SIL OFL) and Bricolage Grotesque (SIL OFL).
