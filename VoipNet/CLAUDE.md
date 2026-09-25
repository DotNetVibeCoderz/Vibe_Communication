# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Voip.NET: a Rust SIP/RTP engine (`native/voipnet-core`) with a .NET 10 SDK on top (`src/`), AI voice agents, contact-centre components, a CLI, and Avalonia/Blazor samples. Full requirements: `requirements.md`. Status and known gaps: `Progress.md`; roadmap: `PLAN.md`.

## Commands

```bash
# Native engine (must be rebuilt after Rust changes — .NET loads native/target/release/voipnet_core.dll)
cd native && cargo build --release
cd native && cargo test --lib                                   # all engine tests
cd native && cargo test --lib sip::endpoint::tests::hold        # single test (name filter)

# .NET
dotnet build Voip.Net.slnx -c Release
dotnet run --project tests/VoipNet.Tests -c Debug               # run xUnit v3 tests (see note below)
dotnet run --project tests/VoipNet.Tests -- -method "*CallsCarryAudio*"   # single test
dotnet run --project tests/VoipNet.Tests -- -class VoipNet.Tests.EnterpriseTests

# Everything (engine + solution + tests [+ NuGet packages])
./build/build.ps1 [-Pack] [-SkipTests]
```

- `dotnet test` does not work with this xUnit v3 + .NET 10 SDK setup; run the test project with `dotnet run`.
- CI runners (especially macOS) are slow: tests must wait for conditions (`wait_frames`, `wait_dtmf`, `TestHelpers.WaitUntilAsync`, prompt drained) instead of sleeping a fixed time before asserting.
- Live LLM tests read keys from the file named by `VOIPNET_TEST_KEYS` (e.g. `C:\Users\mifma\Documents\CodeSandbox\testkey.txt`) and skip without it. Samples read `VOIPNET_AI_ENDPOINT`, `VOIPNET_AI_KEY`, `VOIPNET_AI_MODEL`. Never copy key values into the repo or output.
- Screenshots for docs: `dotnet run --project samples/VoipNet.Softphone -- --screenshot docs/images`, same for `samples/VoipNet.Gallery`; Blazor apps via `tools/VoipNet.DocShots` (`ivrstudio|callcenter|webphone|meeting <baseUrl> docs/images [--firefox <firefox.exe>]`) while the app runs. `meeting` starts two browsers and fails unless each decodes the other's forwarded camera. `webphone` is a real browser interop test (fake microphone, exits 1 on failure); Edge via CDP, Firefox via WebDriver BiDi (Firefox quirks live in `Firefox.cs`: a new tab for the viewport, fake-media prefs in `user.js`, drained stdout).
- Avoid rewriting source files with Windows PowerShell `Get-Content`/`Set-Content` (corrupts UTF-8); use editor tools or bash `sed`.

## Architecture (big picture)

**Native engine** (`native/voipnet-core/src`):
- `sip/dns.rs`: a small DNS client (queries, name compression, NAPTR/SRV/A) plus RFC 3263 resolution with a 60 s cache and a penalty list, so a server that stops answering goes last. `endpoint.rs` uses it from `resolve_addresses`; an explicit port or a numeric host skips it.
- `sip/endpoint.rs` is the user agent: transactions keyed by `branch|METHOD`, dialogs, digest auth, registration refresh, re-INVITE hold, REFER/NOTIFY, a 50 ms timer thread. State is behind one mutex; events go through an mpsc channel to a dispatcher thread, so callbacks never run while the state lock is held. Keep that invariant (callbacks into .NET may re-enter the engine).
- `media/session.rs`: per call a UDP socket plus receive and playout threads. Outbound audio is queued and paced one frame per ptime; `send_audio` resamples to the codec rate. Sink callbacks must also be invoked outside rx/tx locks.
- `sip/transport.rs`: UDP plus one stream model for TCP/TLS/WS/WSS (a `Conn` = TCP or rustls byte stream, optionally WebSocket-framed, one reader thread each; the rustls lock is never held across a blocking read). `sip/tls.rs` builds rustls configs (ring provider, Mozilla roots, CA file, SHA-256 pinning). A send failure on a stream transport marks the client transaction `transport_failed` → 503 on the next timer tick.
- `media/enhance.rs`: WebRTC audio processing (AEC3, noise suppression, AGC2) via the pure-Rust `sonora` crate, behind the default `audio-processing` feature. The playout loop feeds decoded inbound audio as the render (far-end) signal; `transmit_frame` cleans the outgoing frame. It only runs at 8/16/32/48 kHz and on whole 10 ms frames.
- Reflexive gathering: with ICE on, `MediaSession` sends the STUN probe without waiting (the receive loop matches the answer by transaction id, since it owns the socket) and reports the candidate as a `ice-candidate` media event; the endpoint's dispatcher turns that into a trickle INFO. Without ICE the address goes in the SDP, so it is still resolved before the offer.
- `media/sctp.rs`: sans-IO SCTP for data channels (RFC 8831/8832), carried as DTLS application data (`DtlsEvent::Data`/`send_data`). Association handshake, ordered reliable DATA with SACK and retransmission, fragmentation, DCEP. The `m=application` line is a `MediaSession` of its own (`Call::data`, `Direction::Inactive`, no RTP), so it has its own port and DTLS, like a video stream.
- `media/ice.rs`: sans-IO ICE agent (RFC 8445): pairs from two local bases (the socket and the TURN relay), pacing, nomination, 487 role conflicts, prflx learning, consent freshness, restart. `session.rs` feeds it STUN and polls it from the playout loop; `IceOutput::Selected` sets the media destination. Remote trickle candidates arrive via INFO `application/trickle-ice-sdpfrag` in `endpoint.rs`.
- `media/zrtp.rs`: sans-IO ZRTP (RFC 6189) — Hello/Commit/DHPart/Confirm on the media port, EC25 via ring, AES-128 CFB for Confirm, base32 SAS. `classify` gives ZRTP its own packet class; `MediaSession::enable_zrtp` starts it once the peer address is known, and the derived keys replace the (clear) RTP mid-call.
- `media/dtls.rs`: DTLS-SRTP on dimpl (sans-IO, pure Rust, `rust-crypto` feature — keep aws-lc out of the tree). The DTLS engine must get `handle_timeout` before its first packet. `secure_required` on a session blocks plain RTP until keys arrive; only call `enable_dtls()` when DTLS is really negotiated.
- `native/voipnet-core/benches/media.rs`: criterion benchmarks (`cargo bench --bench media`) for codecs, SRTP and the conference mixer; CI runs them briefly. Shared runners and laptops are too noisy for differences under ~20%, so treat single runs with suspicion.
- `media/conference.rs`: mix-minus audio plus video routing. Video is forwarded, never mixed: `video_targets` picks the active speaker (energy measured in `contribute`, with hold and margin) or the pinned call, and holds a viewer back until a keyframe arrives. `SinkAdapter::on_video_frame` calls `Inner::forward_conference_video`, which sends the frame on each viewer's own video session.
- `rtp/quality.rs`: burst/gap classification (RFC 3611) fed from the playout loop.
- `rtp/video.rs`: H.264/VP8 RTP packetisation and frame reassembly. A call with `video: true` gets a `MediaSession` per video stream (`Call::videos`, in m-line order: `main`, then `slides` for a screen share) put in video mode with `enable_video`, negotiated by `negotiate_video_offers`/`apply_video_answer`; frames carry their stream's `a=content` label through `send_video_frame` and `MediaSink::on_video_frame`. A withdrawn stream keeps its m-line (port 0) so both sides keep counting alike. Encoding is the application's job (PLAN 1.3).
- `rtp/packet.rs` also builds and parses RTCP: SR/RR with report blocks, PLI/FIR and REMB (`build_receiver_estimate`, `RtcpPacket::ReceiverEstimate`); `BandwidthEstimator` in `media/session.rs` produces the number a video stream reports, and the peer's estimate lands in `MediaStats::remote_estimate_bps`. `media/session.rs` sends one a second into the call and every 4 s (video streams included — `send_encoded` keeps the sender counters, which is what lip sync reads through `presentation_ntp`/`playout_ntp`), and feeds the peer's numbers (loss, jitter, RTT) to `AudioCodec::set_network_quality`, which is how Opus adapts.
- `rtp/jitter.rs`, `codec/` (G.711, G.722, L16, Opus via libopus behind the default `opus` feature — needs CMake to build; DTMF, PLC; `AudioCodec::conceal` lets a codec do its own PLC/FEC), `srtp.rs` (AES-CM-HMAC-SHA1-80 and AEAD-AES-GCM; SDES or DTLS keys), `stun.rs` (STUN/ICE candidates/TURN), `sdp.rs`.
- `ffi.rs`: C ABI. Config and events are JSON (camelCase); audio is raw `int16*`; stats is a `#[repr(C)]` struct mirrored in `src/VoipNet.Core/Interop/NativeMethods.cs`. Changing one side requires changing the other.

**.NET** (`src/`):
- `VoipNet.Core`: `VoipClient` (engine lifetime, JSON event → typed events, `Check()` error mapping), `VoipCall` (control, `AudioReceived` span delegate, `ReadAudioAsync` channel, `Connected`/`Completion` tasks), `Interop/` (LibraryImport + `[UnmanagedCallersOnly]` callbacks via GCHandle), `Diagnostics/` (pcap, RTP analyser, `VoipMetrics` meter). Enum JSON names use `[JsonStringEnumMemberName]` to match Rust serde names.
- `VoipNet.Audio`: OpenAL devices (`CallAudioBridge`), `CallRecorder` (WAV/MP3 via NAudio.Lame, WAV fallback), `VoiceActivityDetector`, `AudioResampler`.
- `VoipNet.AI`: hand-written `IChatClient` connectors (`Llm/`), speech providers (`Speech/`, option types are `SpeechRecognitionOptions`/`SpeechSynthesisOptions` to avoid clashing with Microsoft.Extensions.AI), `Agents/VoiceAgent` (recognition loop runs concurrently with the agent's turn so interim results can cancel it — barge-in), `Realtime/RealtimeVoiceAgent`.
- `VoipNet.Enterprise`: `IvrFlow`/`IvrRunner`, `CallCenterService` (reserves agent, calls them, bridges legs in a conference; listen-only supervisors are put on hold from the bridge side, not muted), `RecordingService`, `CrmToolset`.

**Samples**: `samples/Shared/VoipNet.Samples.Theme` holds the shared Avalonia "Patch Bay" theme (fonts embedded, `TraceLine` control); Blazor samples mirror the same tokens in `wwwroot/app.css`. Demos run real endpoints on 127.0.0.1 with `SipPort = 0`. `samples/VoipNet.WebPhone` is the exception: a fixed WebSocket port 5090 on 0.0.0.0 so browsers can reach it; its SIP UA is hand-written in `wwwroot/webphone.js` (audio, video and a data channel, each on its own transport — `bundlePolicy: "max-compat"`). `samples/VoipNet.Meeting` does the same on port 5091: every browser joins one `VoipConference`, so the engine mixes the audio and forwards the speaker's camera; its UA is `wwwroot/meeting.js`.

## Conventions (from requirements.md)

- .NET 10, standard C# naming, Avalonia for desktop samples, Blazor Server for web samples; UI work uses the `frontend-design` skill.
- Optimise for performance and memory (span-based audio, no per-frame allocations on hot paths).
- README and docs are bilingual: `README.md`/`README.id.md`, `docs/en` and `docs/id` must stay in sync.
- Keep `Progress.md` and `PLAN.md` updated; label pass-through/planned features honestly.
- Credit "Gravicode Studios, led by Kang Fadhil" in apps and docs.
- Releases: the monorepo `DotNetVibeCoderz/Vibe_Communication` hosts this project under `VoipNet/`; `.github/workflows/voipnet-ci.yml` builds 6 native runtimes, tests on 3 OSes, packs, and publishes to NuGet (secret `NUGET_API_KEY`) on `voipnet-v*` tags. Pushing tags or publishing is outward-facing; confirm with the user first.
