# Architecture

🇮🇩 [Bahasa Indonesia](../id/architecture.md) · Made by Gravicode Studios, led by Kang Fadhil

```
┌──────────────────────────── .NET 10 ─────────────────────────────┐
│ VoipNet.Enterprise  IvrRunner · CallCenterService · Recording     │
│ VoipNet.AI          IChatClient connectors · STT/TTS · VoiceAgent │
│ VoipNet.Audio       OpenAL devices · WAV/MP3 · VAD · resampler    │
│ VoipNet.Core        VoipClient · VoipCall · VoipConference        │
│                     Diagnostics: PcapWriter · RtpStreamAnalyzer   │
│    │  P/Invoke (LibraryImport), function-pointer callbacks        │
├────┼──────────────────────── C ABI ───────────────────────────────┤
│    ▼                        Rust (voipnet-core)                    │
│ ffi.rs        handles, JSON config/events, zero-copy audio         │
│ sip/          transport (UDP/TCP/TLS/WS) · tls · message · UA      │
│ sdp.rs        offer/answer · codec negotiation                     │
│ media/        session (threads, ICE, SRTP) · dtls · conference     │
│ rtp/          packet · adaptive jitter buffer                      │
│ codec/        G.711 · G.722 · L16 · DTMF (RFC 4733, Goertzel) · PLC │
│ srtp.rs       AES-CM-128-HMAC-SHA1-80 · AES-GCM · replay window    │
│ stun.rs       STUN · ICE candidates · TURN allocation              │
└───────────────────────────────────────────────────────────────────┘
```

## The native engine

**Endpoint (`sip/endpoint.rs`).** One SIP user agent per `VoipClient`. It owns the transport, client and server transactions (keyed by branch + method, so CANCEL never collides with its INVITE), dialogs, registration with digest authentication, and every call. A 50 ms timer thread drives RFC 3261 retransmissions (T1/T2), 2xx retransmission until ACK, registration refresh at 85% of the granted lifetime, NAT keep-alives, and purging of ended calls.

**Threads and locks.** The transport thread parses and dispatches messages; state lives behind one mutex. Events are never raised while that mutex is held: they go through a channel to a dedicated dispatcher thread, so application handlers can call back into the engine without deadlocks. Audio callbacks come from media threads and are also invoked outside media locks.

**Media session (`media/session.rs`).** Each call has a UDP socket (RTP and RTCP multiplexed) and two threads:

- *receive*: STUN (to the ICE agent in `media/ice.rs`), DTLS records, SRTP unprotect, RTP parse, telephone-events, jitter buffer insert, pass-through payloads to the application;
- *playout*: every packetization interval it drives ICE and DTLS timers, pops the jitter buffer, decodes or conceals, detects in-band DTMF, delivers PCM to .NET, mixes conferences, then sends one paced frame from the outbound queue.

Outbound audio is **queued and paced**: `SendAudio` can be called with bursts (for example TTS output) and the engine transmits exactly one frame per ptime. `ClearAudio` drops the queue for barge-in. Input at any sample rate is resampled to the codec rate.

**Jitter buffer (`rtp/jitter.rs`).** Orders by extended sequence number, tracks RFC 3550 inter-arrival jitter and sets its target depth to about three times the jitter plus one frame, between the configured minimum and maximum. Loss is reported so the decoder can conceal it; sustained excess depth is trimmed to keep latency low.

**Security.** SRTP contexts use AES-CM-128-HMAC-SHA1-80 (checked against the RFC 3711 key-derivation vectors) or AEAD-AES-128/256-GCM (RFC 7714 vectors); the roll-over counter is estimated per §3.3.1 and a 64-packet replay window rejects duplicates. Keys arrive by SDES (`a=crypto`) or by DTLS-SRTP: `media/dtls.rs` drives the pure-Rust dimpl DTLS 1.2 engine on the media socket (packets are told apart from STUN and RTP by their first byte), checks the peer certificate against `a=fingerprint`, and installs the exported keys; until then no RTP is sent or accepted. `SrtpMode.Mandatory` offers RTP/SAVP (or UDP/TLS/RTP/SAVP with DTLS); `Optional` offers SDES keys in RTP/AVP; `Disabled` refuses secure offers with 488.

**Signaling transports.** `sip/transport.rs` treats TCP, TLS, WS and WSS as one connection model: a byte stream (plain, or rustls with the ring provider) that optionally carries WebSocket frames, with SIP framed by Content-Length. Each connection has a reader thread; senders share the rustls state behind a lock that is never held across a blocking read. A connection that fails its TLS handshake fails the pending transaction at once with 503. Server certificates are verified against the Mozilla roots plus an optional CA file, or pinned by SHA-256 fingerprint.

## The boundary

The C ABI (`ffi.rs`) is deliberately small:

- configuration and events are JSON (rare, rich, easy to evolve);
- audio is a raw `int16*` pointer and length (hot path, no copies, no marshalling);
- statistics are a blittable struct.

.NET binds with `LibraryImport` source generation and `delegate* unmanaged` callbacks marked `[UnmanagedCallersOnly]`; the client is pinned with a `GCHandle` for the lifetime of the endpoint. Exceptions never cross into Rust.

## The .NET layer

`VoipClient` turns engine events into typed events and `VoipCall` objects. Every call exposes:

- `AudioReceived` — a `ReadOnlySpan<short>` delegate for zero-allocation processing;
- `ReadAudioAsync()` — an `IAsyncEnumerable<AudioSegment>` for pipelines (speech recognition, realtime models);
- `Connected` and `Completion` tasks for linear `async` code.

Set `VoipClientOptions.EventSynchronizationContext` in UI apps so events arrive on the UI thread.

## The AI loop

```
call audio ──► ISpeechToText ──► IChatClient (+tools) ──► sentence splitter ──► ITextToSpeech ──► SendAudio
    ▲               │ interim result while agent speaks                                            │
    └───────────────┴──────────────── barge-in: cancel turn + ClearAudio ◄─────────────────────────┘
```

`VoiceAgent` keeps listening while it speaks: the recogniser loop and the agent's turn run concurrently, so an interim transcript cancels the turn and clears queued audio immediately. Answers are spoken sentence by sentence as the model streams. `RealtimeVoiceAgent` replaces the whole chain with one web socket to a speech-to-speech model.

## Contact centre

`CallCenterService` places callers in priority-ordered queues, reserves an agent by strategy (round robin, longest idle, fewest calls, skill based), calls the agent, and bridges both legs in a conference. Keeping the bridge in the application means recording, supervisor listen-in (`Monitor`) and metrics all keep working. `IvrRunner` executes `IvrFlow` graphs and can hand the call to any async handler — typically a `VoiceAgent`.
