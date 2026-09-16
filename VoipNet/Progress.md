# Progress · Kemajuan

Voip.NET — made by Gravicode Studios, led by Kang Fadhil · dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

Last updated · Terakhir diperbarui: **2026-09-17** · Version · Versi: **1.0.0**

Legend · Keterangan: ✅ done · selesai — 🟡 partial · sebagian — ⏳ planned · direncanakan (see · lihat [PLAN.md](PLAN.md))

## Verification · Verifikasi

| Suite | Result · Hasil |
| --- | --- |
| Rust engine `cargo test --lib` | **53 passed** · lulus — codecs, SIP parser, digest auth, SDP, jitter buffer, SRTP (RFC 3711 vectors), STUN, loopback media, full SIP call flows, FFI |
| .NET `tests/VoipNet.Tests` | **35 passed** · lulus — calls/audio/DTMF/hold/conference over the real engine, audio & recording, chat connector protocols, **live Azure OpenAI (gpt-5-mini, tool calling) and DeepSeek**, voice agent + barge-in, IVR, queue bridging + supervisor listen-only, recording service, CRM tools, pcap/RTP analyser/metrics |
| `dotnet build Voip.Net.slnx -c Release` | 13 projects · proyek, **0 warnings, 0 errors** |
| CLI end-to-end · ujung ke ujung | `sip listen --echo` ↔ `sip call --dtmf 123 --pcap`: MOS 4.38, all DTMF received · semua DTMF diterima |
| Samples · Sample | Softphone, Gallery, Call Centre, IVR Studio run; screenshots rendered from the running apps · berjalan; screenshot diambil dari aplikasi yang berjalan |
| Realtime Agent demo | Scripted caller ↔ real gpt-5-mini conversation incl. memory restore · percakapan dengan model nyata termasuk pemulihan memori |

## Core architecture · Arsitektur inti

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| Rust SIP/RTP engine | ✅ | `native/voipnet-core` |
| .NET bindings, async/await, DI | ✅ | `LibraryImport`, unmanaged callbacks, `AddVoipClient`, hosted service |
| Cross-platform runtime | 🟡 | Windows x64 built & tested locally; CI builds win/linux/osx x64+arm64 and runs tests on Windows, Linux, macOS · CI membangun dan menguji lintas platform |
| NuGet distribution | 🟡 | CI in Vibe_Communication builds 6 runtimes, packs and publishes on `voipnet-v*` tags · CI membangun 6 runtime, membuat paket, dan publish pada tag `voipnet-v*` |

## Telephony · Telefoni

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| REGISTER, INVITE, ACK, BYE, CANCEL, REFER, OPTIONS, INFO, MESSAGE, NOTIFY, UPDATE | ✅ | RFC 3261 transactions and timers · transaksi dan timer |
| Digest auth (401/407), NAT rport learning, keep-alive | ✅ | |
| Transfer blind / attended (Replaces), hold, conferencing, recording | ✅ | |
| UDP, TCP transports | ✅ | |
| TLS transport (SIPS) | ⏳ | |
| RTP/RTCP, adaptive jitter buffer, PLC | ✅ | |
| G.711, G.722, L16 | ✅ | native |
| G.729, Opus, SILK, Speex | 🟡 | negotiated, pass-through payloads · dinegosiasikan, payload pass-through |
| H.264, VP8/VP9 | 🟡 | negotiated, pass-through · dinegosiasikan, pass-through |
| DTMF RFC 4733 / SIP INFO / in-band | ✅ | Goertzel detector · detektor Goertzel |
| SIMD acceleration | 🟡 | table-driven codecs and auto-vectorised loops; no hand-written intrinsics · codec berbasis tabel dan loop tervektorisasi otomatis |
| GPU acceleration | ⏳ | not needed for audio codecs; planned with video (PLAN 1.3) · belum diperlukan untuk audio; direncanakan bersama video (PLAN 1.3) |

## WebRTC

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| ICE host/srflx/relay candidates, connectivity checks | 🟡 | ICE-lite style responder + checks; no full ICE state machine · belum state machine ICE penuh |
| STUN, TURN allocation / permissions / send-data | ✅ | |
| SRTP (SDES) | ✅ | |
| DTLS-SRTP | ⏳ | required for browsers · diperlukan untuk browser |
| Data channels (SCTP) | ⏳ | |
| Browser interop | ⏳ | depends on DTLS-SRTP · bergantung pada DTLS-SRTP |

## 🎥 Video · Fitur video

Planned in · Direncanakan di [PLAN.md 1.3](PLAN.md#13---video--fitur-video).

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| Video call support — voice + video in one SIP session · panggilan suara + video dalam satu sesi SIP | 🟡 | `m=video` lines are negotiated and H.264/VP8/VP9 payloads can be exchanged with `SendEncoded`/`EncodedReceived`; no camera capture, codec, packetiser or frame jitter buffer yet · baris `m=video` dinegosiasikan dan payload bisa dipertukarkan secara pass-through; belum ada kamera, codec, paketisasi, atau jitter buffer frame |
| Video conferencing with layout control · konferensi video multipihak dengan kontrol layout | ⏳ | audio conferencing exists; video compositor/SFU planned · konferensi audio sudah ada; compositor/SFU video direncanakan |
| Screen sharing (desktop & web) · berbagi layar (desktop & web) | ⏳ | web sharing depends on DTLS-SRTP/WebRTC · berbagi dari web bergantung pada DTLS-SRTP/WebRTC |
| Video recording to MP4/AVI · perekaman audio + video ke MP4/AVI | ⏳ | audio recording (WAV/MP3) exists · perekaman audio (WAV/MP3) sudah ada |

## AI call centre · AI call center

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| OpenAI, Azure OpenAI, OpenAI-compatible (DeepSeek…), Anthropic, Gemini | ✅ | `IChatClient`, streaming, tools; Azure/DeepSeek verified live · diverifikasi langsung |
| Realtime models | ✅ | `RealtimeVoiceAgent` (OpenAI realtime) — not verified live (no key) · belum diuji langsung (tanpa key) |
| Kernel / AI functions (Semantic Kernel, Microsoft.Extensions.AI) | ✅ | call-control tools, CRM tools |
| audio → STT → LLM → TTS → RTP loop, barge-in | ✅ | `VoiceAgent` |
| Context persistence, hand-off | ✅ | conversation stores, `HandOffAsync`, `transfer_call` |

## STT / TTS

| Provider | STT | TTS | Notes · Catatan |
| --- | --- | --- | --- |
| ElevenLabs | ✅ | ✅ | not verified live · belum diuji langsung |
| Deepgram | ✅ streaming | — | not verified live · belum diuji langsung |
| OpenAI Whisper / TTS | ✅ | ✅ | not verified live · belum diuji langsung |
| Google Cloud | ✅ | ✅ | not verified live · belum diuji langsung |
| Amazon Polly / Transcribe | ⏳ | ✅ Polly | Transcribe planned · direncanakan |
| ElBruno.Realtime | ✅ | ✅ | documented WS/HTTP protocol · protokol terdokumentasi |

> Only Azure OpenAI and DeepSeek keys were available, so speech providers are implemented against their public APIs and covered by the offline pipeline tests, but not exercised against the live services yet. · Hanya kunci Azure OpenAI dan DeepSeek yang tersedia, jadi provider suara diimplementasikan sesuai API publiknya dan diuji lewat pipeline offline, tetapi belum diuji ke layanan aslinya.

## Developer experience

| Item | Status |
| --- | --- |
| `VoipClient.CallAsync("sip:user@domain")`, event model | ✅ |
| Sample: AI softphone (Avalonia) | ✅ |
| Sample: call center dashboard (Blazor Server) | ✅ |
| Sample: IVR with AI (Blazor Server) | ✅ |
| Sample: realtime agent demo (console) | ✅ |
| Sample: Voip.NET Gallery (Avalonia) | ✅ |
| CLI: SIP tester, RTP analyser | ✅ |
| Diagnostics: logging, packet capture (pcap), performance counters (Metrics) | ✅ |

## Enterprise

| Item | Status |
| --- | --- |
| IVR builder with AI dialog | ✅ |
| Queue management, agent monitoring, supervisor listen/whisper | ✅ |
| Recording WAV/MP3 (MP3 on Windows; WAV elsewhere · MP3 di Windows, WAV di platform lain), analytics dashboards | ✅ |
| TLS, SRTP, ZRTP, end-to-end encryption | 🟡 SRTP ✅ · TLS ⏳ · ZRTP ⏳ |
| CRM integration via AI functions | ✅ |

## Documentation · Dokumentasi

| Item | Status |
| --- | --- |
| README EN + ID | ✅ |
| docs/en + docs/id (8 guides each · 8 panduan per bahasa) | ✅ |
| Screenshots from real apps · Screenshot dari aplikasi nyata | ✅ 16 images |
| Gravicode Studios / Kang Fadhil credit in apps & docs · kredit di aplikasi & dokumen | ✅ |

## Changelog

### 1.0.0 — 2026-09-16

- First complete implementation of engine, SDK, AI, enterprise packages, CLI, five samples, bilingual docs. · Implementasi lengkap pertama.
- Fixes found during verification · Perbaikan yang ditemukan saat verifikasi: CANCEL/INVITE transaction key collision; REFER target losing its port; voice agent unable to barge in while speaking; supervisor listen-in muting the supervisor instead of the bridge; reasoning-model request parameters (`max_completion_tokens`, `reasoning_effort`); CRM phone matching across `+62`/`0` prefixes; events arriving before request waiters registered.
