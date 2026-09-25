# Progress · Kemajuan

Voip.NET — made by Gravicode Studios, led by Kang Fadhil · dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

Last updated · Terakhir diperbarui: **2026-09-17** · Version · Versi: **1.2.0**

Legend · Keterangan: ✅ done · selesai — 🟡 partial · sebagian — ⏳ planned · direncanakan (see · lihat [PLAN.md](PLAN.md))

## Verification · Verifikasi

| Suite | Result · Hasil |
| --- | --- |
| Rust engine `cargo test --lib` | **123 passed** · lulus — codecs, SIP parser, digest auth, SDP, jitter buffer, SRTP (RFC 3711 and RFC 7714 GCM vectors), DTLS-SRTP handshake, ICE agent (nomination, role conflict, peer-reflexive, restart, consent), STUN, SCTP data channels, WebSocket framing, loopback media, full SIP call flows over UDP/TLS/WS/WSS, FFI |
| .NET `tests/VoipNet.Tests` | **99 passed** · lulus — calls/audio/DTMF/hold/conference over the real engine, TLS with pinning, WebSocket + DTLS-SRTP, ICE selection and restart, data channels, queue opening hours, audio & recording, chat connector protocols, **live Azure OpenAI (gpt-5-mini, tool calling), Azure OpenAI realtime on a call, and DeepSeek**, voice agent + barge-in, IVR, queue bridging + supervisor listen-only, recording service, CRM tools, pcap/RTP analyser/metrics |
| `dotnet build Voip.Net.slnx -c Release` | 15 projects · proyek, **0 warnings, 0 errors** |
| Benchmarks · Benchmark | `cargo bench --bench media` on a laptop (20 ms frame): opus encode 550 µs · decode 127 µs, G.722 encode 16 µs · decode 13 µs, G.711 encode 150 ns, SRTP protect 2.6 µs, conference mix-minus 3.7 µs at 50 participants; run in CI to catch breakage · dijalankan di CI |
| CLI end-to-end · ujung ke ujung | `sip listen --echo` ↔ `sip call --dtmf 123 --pcap`: MOS 4.38, all DTMF received · semua DTMF diterima |
| CLI over TLS and WebSocket · CLI lewat TLS dan WebSocket | `--transport tls --tls-pin …` and `--transport ws --dtls`: MOS 4.38, SRTP on · SRTP aktif |
| Browser interop · Interop browser | `tools/VoipNet.DocShots webphone` with a fake microphone against `samples/VoipNet.WebPhone`: **Edge** — trickle ICE over INFO, DTLS, `SRTP_AES128_CM_HMAC_SHA1_80`; **Firefox** (WebDriver BiDi) — trickle ICE, DTLS, `SRTP_AEAD_AES_128_GCM`; both now negotiate **Opus** end to end (~190 packets each way, 0 lost) · keduanya ~200 paket tiap arah, 0 hilang |
| Samples · Sample | Softphone, Gallery, Call Centre, IVR Studio run; screenshots rendered from the running apps · berjalan; screenshot diambil dari aplikasi yang berjalan |
| Realtime Agent demo | Scripted caller ↔ real gpt-5-mini conversation incl. memory restore · percakapan dengan model nyata termasuk pemulihan memori |

## Core architecture · Arsitektur inti

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| Rust SIP/RTP engine | ✅ | `native/voipnet-core` |
| .NET bindings, async/await, DI | ✅ | `LibraryImport`, unmanaged callbacks, `AddVoipClient`, hosted service |
| Metrics and tracing | ✅ | meter `VoipNet` (calls, duration, MOS, loss) and activity source `VoipNet` (a span per call, child spans per agent turn), both exportable with OpenTelemetry · meter dan activity source `VoipNet`, siap diekspor lewat OpenTelemetry |
| Cross-platform runtime | 🟡 | Windows x64 built & tested locally; CI builds win/linux/osx x64+arm64 and runs tests on Windows, Linux, macOS · CI membangun dan menguji lintas platform |
| NuGet distribution | 🟡 | CI in Vibe_Communication builds 6 runtimes, packs and publishes on `voipnet-v*` tags · CI membangun 6 runtime, membuat paket, dan publish pada tag `voipnet-v*` |

## Telephony · Telefoni

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| REGISTER, INVITE, ACK, BYE, CANCEL, REFER, OPTIONS, INFO, MESSAGE, NOTIFY, UPDATE | ✅ | RFC 3261 transactions and timers · transaksi dan timer |
| Digest auth (401/407), NAT rport learning, keep-alive | ✅ | |
| DNS NAPTR/SRV resolution (RFC 3263) | ✅ | own DNS client (no new async runtime): NAPTR picks the transport, SRV gives hosts and ports in priority/weight order, A/AAAA resolves them, and an address that times out goes last for a minute · klien DNS sendiri: NAPTR, SRV berurutan prioritas/bobot, A/AAAA, dan alamat yang gagal diprioritaskan terakhir |
| Session timers (RFC 4028), reliable provisionals + PRACK (RFC 3262) | ✅ | `SessionExpires`/`MinSessionExpires` refresh calls by re-INVITE and hang up unrefreshed ones (422 when the offer is shorter than the minimum); a 180 carries `RSeq` and is resent until the caller PRACKs it · memperbarui panggilan lewat re-INVITE dan menutup yang tidak diperbarui; respons 180 dikirim ulang sampai di-PRACK |
| Transfer blind / attended (Replaces), hold, conferencing, recording | ✅ | |
| UDP, TCP transports | ✅ | |
| WebSocket transports `ws`/`wss` (RFC 7118) | ✅ | server and client, `sip` subprotocol, shared TLS stack · server dan client, subprotokol `sip` |
| Certificate reload without restart | ✅ | `ReloadTls()` swaps the certificate for new connections; calls in progress are untouched · panggilan yang sedang berjalan tidak terganggu |
| Mutual TLS (client certificates) | ✅ | `TlsRequireClientCertificate`; the same pinning and CA rules apply to callers, and a rejected handshake fails the request at once · aturan pinning dan CA yang sama berlaku untuk penelepon |
| TLS transport (SIPS) | ✅ | rustls (TLS 1.2/1.3, ring), Mozilla roots + custom CA, SHA-256 pinning, self-signed or PEM identity · root Mozilla + CA sendiri, pinning SHA-256, identitas self-signed atau PEM |
| RTP/RTCP, adaptive jitter buffer, PLC | ✅ | |
| RTCP sender/receiver reports (RFC 3550) | ✅ | report blocks, loss and jitter the peer sees, round-trip time; Opus adapts its bitrate and FEC to them · report block, loss dan jitter dari sisi lawan, RTT; Opus menyesuaikan bitrate dan FEC |
| RTCP XR VoIP metrics (RFC 3611) | ✅ | sent every report and parsed from the peer: loss/discard rate, delays, R factor, MOS-LQ/CQ, jitter buffer · dikirim tiap report dan dibaca dari lawan |
| Echo cancellation, noise suppression, gain control | ✅ | WebRTC AEC3/NS/AGC2 through the pure-Rust `sonora` port, `audio-processing` cargo feature; off unless `EchoCancellation`/`NoiseSuppression`/`AutoGain` are set. `CallAudioBridge` reports its device round trip so AEC3 starts aligned (`StreamDelayMs`, `SetAudioDelay`) · lewat port `sonora`, aktif bila opsi dinyalakan; latensi perangkat dilaporkan ke AEC |
| Opus (RFC 6716/7587) | ✅ | libopus (`opus` cargo feature, on by default): 48 kHz mono, 32 kbit/s, in-band FEC, FEC recovery from the next buffered packet, libopus PLC; verified with Edge and Firefox · diverifikasi dengan Edge dan Firefox |
| G.711, G.722, L16 | ✅ | native |
| G.729, SILK, Speex | 🟡 | negotiated, pass-through payloads · dinegosiasikan, payload pass-through |
| H.264, VP8/VP9 | 🟡 | negotiated, pass-through · dinegosiasikan, pass-through |
| DTMF RFC 4733 / SIP INFO / in-band | ✅ | Goertzel detector · detektor Goertzel |
| SIMD acceleration | 🟡 | table-driven codecs and auto-vectorised loops; hand-written intrinsics were measured and did not pay off — G.722 is dominated by its serial ADPCM state machine, not by the QMF · intrinsik manual sudah diukur dan tidak memberi keuntungan |
| GPU acceleration | ⏳ | not needed for audio codecs; planned with video (PLAN 1.3) · belum diperlukan untuk audio; direncanakan bersama video (PLAN 1.3) |

## WebRTC

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| ICE agent (RFC 8445): pairs, pacing, nomination, role conflicts, peer-reflexive candidates, consent freshness (RFC 7675), restart | ✅ | `media/ice.rs`, sans-IO with unit tests · agen ICE penuh |
| Trickle ICE (RFC 8838/8840) | ✅ | receives candidates in INFO `application/trickle-ice-sdpfrag`; own candidates are gathered before the offer · menerima kandidat lewat INFO |
| STUN, TURN allocation / permissions / send-data | ✅ | |
| SRTP AES_CM_128_HMAC_SHA1_80 and AEAD_AES_128/256_GCM (RFC 7714) | ✅ | SDES or DTLS keys · kunci SDES atau DTLS |
| DTLS-SRTP (RFC 5763/5764) | ✅ | dimpl DTLS 1.2 (pure Rust), fingerprint check, `a=setup` roles, no plain RTP before keys · tanpa RTP polos sebelum kunci siap |
| Data channels (SCTP over DTLS, RFC 8831/8832) | ✅ | `media/sctp.rs`, sans-IO with unit tests: association, ordered reliable DATA with SACK and retransmission, fragmentation, DCEP. `m=application` beside the call, verified against Edge (a line of text from the browser and back) on every CI build. Partial reliability and unordered delivery not implemented · kanal data SCTP di dalam DTLS
| Visual IVR designer | ✅ | the studio's call path is a graph: menus are nodes you drag into place (their positions are saved with the flow), a handle drags a new key from one menu to another, every save keeps a version to restore, and any version can be run as variant B of an A/B test · desainer IVR visual dengan versi dan uji A/B |
| Container image and Helm chart | ✅ | `docker/Dockerfile` builds the engine and any one app (CLI, sample or your own) into one image; `deploy/helm/voipnet` deploys it with host networking, since media needs a port range a Service cannot forward. CI builds the image, runs the CLI from it and lints the chart · image container dan chart Helm, diperiksa di CI |
| Lip sync (RTCP sender reports) | ✅ | every stream sends sender reports — video streams did not before — and `VoipCall.PresentationTime`/`AudioPlayoutTime` put a frame's timestamp and the audio being heard on the sender's one clock · sinkronisasi bibir dari sender report |
| Bandwidth estimation (RTCP REMB) | ✅ | a receive-side loss estimator per video stream, sent to the peer so a browser encodes to fit (Chrome follows it), and the peer's own estimate reported as `CallStatistics.RemoteEstimateBps` and a `bandwidth-estimate` notification · estimasi bandwidth REMB dua arah |
| RTCP feedback negotiation (`a=rtcp-fb`: nack, nack pli, ccm fir, goog-remb) | ✅ | offered on video lines and echoed in answers (RFC 4585 4); without it a browser ignores the keyframe requests the conference needs to start a new viewer · negosiasi umpan balik RTCP di baris video |
| Browser video interop | ✅ | the WebPhone sample sends camera video over DTLS-SRTP; the gateway echoes the reassembled frames and the browser decodes them again (Edge: VP8 320×240, 64 frames out and back). Checked on every CI build · video kamera dari browser diuji pada setiap build CI |
| Browser interop | ✅ | Chrome/Edge and Firefox both gate every CI build (`tools/VoipNet.DocShots webphone`, and `meeting` for the conference): the build fails unless encrypted audio, video and a data channel message come back through each browser. Safari not tested (no macOS machine) · diuji di setiap build CI dengan Chrome dan Firefox; Safari belum diuji |

## 🎥 Video · Fitur video

Planned in · Direncanakan di [PLAN.md 1.3](PLAN.md#13---video--fitur-video).

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| Video call support — voice + video in one SIP session · panggilan suara + video dalam satu sesi SIP | 🟡 | `VoipClientOptions.Video` offers and accepts an `m=video` stream next to audio: a second RTP stream per call with its own port, SDES/DTLS-SRTP and ICE, H.264 (RFC 6184 FU-A/STAP-A) and VP8 (RFC 7741) packetisation with frame reassembly and keyframe detection, `VoipCall.SendVideoFrame` and `VideoFrameReceived`, plus keyframe requests in both directions (RTCP PLI/FIR, sent automatically when a frame loses packets). The application supplies encoded frames: camera capture, platform encode/decode, bandwidth estimation and lip sync are still missing · stream `m=video` kedua dengan enkripsi, ICE, dan permintaan keyframe sudah berjalan beserta API .NET; kamera, encode/decode platform, estimasi bandwidth, dan lip sync belum ada |
| Video conferencing with layout control · konferensi video multipihak dengan kontrol layout | 🟡 | the bridge forwards one participant's video to everyone else — the active speaker (picked from the audio it already mixes, with hold and margin) or a pinned call — and holds a switch until the new source sends a keyframe. Mixed grid layouts need decode/encode the engine does not carry · bridge meneruskan video pembicara aktif atau peserta yang dipin; layout grid perlu decode/encode yang belum ada |
| Screen sharing (desktop & web) · berbagi layar (desktop & web) | 🟡 | `ShareScreen()` offers a second `m=video` marked `a=content:slides` in a re-INVITE, frames carry the label in both directions, and `StopScreenShare()` withdraws it with port 0; capture (Windows Graphics Capture, ScreenCaptureKit, PipeWire) and encoding are still the application's · stream layar kedua sudah dinegosiasikan dan dilabeli; penangkapan layar dan encoding masih di sisi aplikasi |
| Video recording to MP4/AVI · perekaman audio + video ke MP4/AVI | 🟡 | `RecordingFormat.Avi` writes the call's own video frames next to PCM audio (no re-encoding), with the measured frame rate patched into the header; MP4/AAC needs an AAC encoder the SDK does not carry · AVI sudah bisa (tanpa encode ulang); MP4/AAC menunggu encoder AAC |

## AI call centre · AI call center

| Item | Status | Notes · Catatan |
| --- | --- | --- |
| OpenAI, Azure OpenAI, OpenAI-compatible (DeepSeek…), Anthropic, Gemini | ✅ | `IChatClient`, streaming, tools; Azure/DeepSeek verified live · diverifikasi langsung |
| Realtime models | ✅ | `RealtimeVoiceAgent`, GA and beta protocols, OpenAI and Azure OpenAI; verified live on a call with Azure `gpt-realtime-2.1-mini` (greeting, then an answer to the caller's speech via server VAD) · diuji langsung pada panggilan dengan Azure |
| Kernel / AI functions (Semantic Kernel, Microsoft.Extensions.AI) | ✅ | call-control tools, CRM tools |
| audio → STT → LLM → TTS → RTP loop, barge-in | ✅ | `VoiceAgent` |
| Semantic turn detection | ✅ | `ITurnDetector`/`SemanticTurnDetector` holds the answer while a caller is mid-sentence and joins the parts, with a grace timeout and fail-open behaviour · menahan jawaban selama penelepon belum selesai, dengan batas waktu |
| Context persistence, hand-off | ✅ | conversation stores, `HandOffAsync`, `transfer_call` |
| Agent assist | ✅ | `AgentAssist` transcribes a live call (both directions optionally) and suggests replies for a human agent after every caller sentence, rate limited and grounded in the knowledge it is given · transkrip langsung dan saran balasan untuk agent manusia |
| Post-call analytics | ✅ | `CallAnalyzer`: summary, caller sentiment, topics, action items and a QA score against a configurable checklist, from a recording (transcribed first), a transcript, or an agent's turns; verified live with Azure OpenAI · dari rekaman, transkrip, atau percakapan agent; diuji langsung |

## STT / TTS

| Provider | STT | TTS | Notes · Catatan |
| --- | --- | --- | --- |
| ElevenLabs | ✅ | ✅ | verified live (Indonesian TTS/STT, voice agent on a call) · diuji langsung |
| Deepgram | ✅ streaming | ✅ Aura | not verified live · belum diuji langsung |
| OpenAI Whisper / TTS | ✅ | ✅ | not verified live · belum diuji langsung |
| Google Cloud | ✅ | ✅ | not verified live · belum diuji langsung |
| Azure AI Speech | ✅ | ✅ | short-audio recognition and neural voices, protocol tests offline; not verified live · pengenalan short-audio dan suara neural, belum diuji langsung |
| Cartesia | — | ✅ | raw PCM streaming, protocol tests offline; not verified live · streaming PCM mentah, belum diuji langsung |
| Amazon Polly / Transcribe | ⏳ | ✅ Polly | Transcribe planned · direncanakan |
| ElBruno.Realtime | ✅ | ✅ | documented WS/HTTP protocol · protokol terdokumentasi |

> Live keys cover Azure OpenAI (chat and realtime), DeepSeek and ElevenLabs. The other speech providers are implemented against their public APIs and covered by the offline protocol tests; `EverySpeechProviderWithAKeySpeaksAndListens` runs the same live round trip (speak a phrase, transcribe it back) for any provider whose key is added to the key file, and skips the rest. · Kunci langsung tersedia untuk Azure OpenAI (chat dan realtime), DeepSeek, dan ElevenLabs; provider suara lainnya diimplementasikan sesuai API publiknya dan diuji lewat test protokol offline; `EverySpeechProviderWithAKeySpeaksAndListens` menjalankan uji langsung (mengucapkan satu kalimat lalu mentranskripsikannya kembali) untuk provider yang kuncinya ditambahkan ke file kunci, dan melewati sisanya.

## Developer experience

| Item | Status |
| --- | --- |
| `VoipClient.CallAsync("sip:user@domain")`, event model | ✅ |
| Sample: AI softphone (Avalonia) | ✅ |
| Sample: call center dashboard (Blazor Server) | ✅ |
| Sample: IVR with AI (Blazor Server) | ✅ |
| Sample: realtime agent demo (console) | ✅ |
| Sample: Voip.NET Gallery (Avalonia) | ✅ |
| Sample: WebRTC gateway, browser ↔ SIP (Blazor Server) | ✅ |
| CLI: SIP tester, RTP analyser | ✅ |
| Diagnostics: logging, packet capture (pcap), performance counters (Metrics) | ✅ |

## Enterprise

| Item | Status |
| --- | --- |
| IVR builder with AI dialog | ✅ |
| Queue management, agent monitoring, supervisor listen/whisper | ✅ |
| CRM connectors | ✅ | HubSpot, Salesforce, Dynamics 365 and Odoo over their public APIs (lookup by phone, recent tickets, open a ticket, add a note), covered by protocol tests; not verified against live tenants · empat konektor CRM dengan test protokol, belum diuji ke tenant asli |
| Historical reports and CSV export | ✅ | finished queue calls are stored; `ReportAsync` summarises them per queue and interval (offered, answered, abandoned, waits, talk, service level) and `WorkforceReport.ToCsv` exports them · riwayat panggilan, ringkasan per interval, dan ekspor CSV |
| Persistent queue and agent state | ✅ | `ICallCenterStore` with a SQL implementation over any ADO.NET provider: agent states shared between nodes, callbacks that survive a restart, and an atomic claim so two nodes never ring the same caller · state agent dan callback yang persisten dan dapat dibagi antar-node |
| Callbacks and estimated wait time | ✅ | a caller keeps their place and hangs up; the service rings back when an agent is free and the callback has outwaited everyone still holding, with retries and an announcement · penelepon menyimpan posisinya lalu ditelepon balik, lengkap dengan percobaan ulang |
| Scheduled routing (opening hours) | ✅ | `RoutingSchedule` per queue: weekly hours in their own time zone, holidays and half days as exceptions, periods that cross midnight, and a `ClosedTarget` for callers who arrive out of hours; `Check` says when the queue opens again · jam buka per antrean dengan zona waktu, hari libur, dan tujuan saat tutup |
| Recording WAV/MP3 (MP3 on Windows; WAV elsewhere · MP3 di Windows, WAV di platform lain), analytics dashboards | ✅ |
| TLS, SRTP, ZRTP, end-to-end encryption | 🟡 SRTP ✅ · DTLS-SRTP ✅ · TLS ✅ · ZRTP ⏳ |
| CRM integration via AI functions | ✅ |

## Documentation · Dokumentasi

| Item | Status |
| --- | --- |
| README EN + ID | ✅ |
| docs/en + docs/id (8 guides each · 8 panduan per bahasa) | ✅ |
| Screenshots from real apps · Screenshot dari aplikasi nyata | ✅ 17 images |
| Gravicode Studios / Kang Fadhil credit in apps & docs · kredit di aplikasi & dokumen | ✅ |

## Changelog

### Unreleased · Belum dirilis

- The WebPhone sample sends video: the browser's camera goes over the same DTLS-SRTP path, the gateway echoes the frames it reassembles, and the returned picture plays in the page. The CI interop test now fails unless video comes back too. · Sample WebPhone mengirim video kamera; gateway memantulkannya dan CI menuntut video ikut kembali.
- Conference video: the bridge forwards the active speaker (or a pinned participant) to everyone else, asking the new source for a keyframe before it switches, and relays it on the viewer's own clock so a change of speaker never moves timestamps backwards. `FollowSpeaker()`, `Pin(call)` and `ActiveSpeaker` on `VoipConference`; `samples/VoipNet.Meeting` is two or more browsers in one room, checked on every CI build. · Video konferensi mengikuti pembicara aktif atau peserta yang dipin, dengan sample ruang rapat untuk browser.
- Screen sharing: a call can offer a second video stream marked `a=content:slides`, frames say which stream they belong to, and withdrawing the share leaves the call and the camera alone. · Berbagi layar sebagai stream video kedua, lengkap dengan label pada setiap frame.
- CRM connectors for HubSpot, Salesforce, Dynamics 365 and Odoo: look a caller up by number, read their recent tickets, open one, and add a note — the four operations a voice agent needs. · Konektor CRM untuk HubSpot, Salesforce, Dynamics 365, dan Odoo.
- Historical reporting: finished queue calls are stored, `ReportAsync` summarises them per queue and interval, and `WorkforceReport.ToCsv` exports them for a spreadsheet or a dashboard. · Laporan historis per antrean dan interval, lengkap dengan ekspor CSV.
- Shared call centre state (`ICallCenterStore`, `SqlCallCenterStore`): agent states and owed callbacks live in SQL, so a restart resumes them and several nodes can share a queue without ringing a caller twice. · State call center bersama di SQL: state agent dan callback bertahan setelah restart dan bisa dibagi antar-node.
- Benchmarks are compared with a committed baseline (`benchmarks/baseline.json`, refreshed with `build/bench-report.ps1 -Update`) and the table lands in the CI job summary, so a slow drift shows up instead of scrolling past in a log. · Benchmark dibandingkan dengan baseline yang tersimpan dan hasilnya tampil di ringkasan job CI.
- ICE calls no longer wait for the STUN server: the probe goes out without blocking the call and the reflexive candidate is trickled to the peer in an INFO when it arrives (RFC 8838). Calls without ICE still resolve it before the offer, because there it is the address in the SDP. · Panggilan ICE tidak lagi menunggu server STUN; kandidat reflexive dikirim belakangan lewat INFO.
- A live verification suite that runs a speak-and-listen round trip against every speech provider whose key is present, and skips the rest. · Uji langsung untuk setiap provider suara yang kuncinya tersedia.
- Semantic turn detection: the agent can ask a small model whether the caller has finished speaking instead of trusting silence, joining "my number is…" and what follows into one question. · Deteksi akhir ucapan berbasis model, bukan sekadar jeda.
- Queue callbacks and estimated wait time: a caller can keep their place and hang up, and the service rings them back when their turn comes and an agent is free (`RequestCallback`, `PendingCallbacks`, `EstimatedWait`). · Callback antrean dan estimasi waktu tunggu.
- Azure AI Speech (recognition and neural voices), Cartesia and Deepgram Aura (synthesis) join the speech providers, with `AddAzureSpeech` and `AddCartesiaTextToSpeech` for dependency injection. · Azure AI Speech dan Cartesia ditambahkan sebagai provider suara.
- Agent assist (`AgentAssist`): a live transcript plus suggested replies for a human agent, offered after each caller sentence and never sent to the caller. · Agent assist: transkrip langsung dan saran balasan untuk agent manusia.
- Post-call analytics (`CallAnalyzer`): summary, caller sentiment, topics, open action items and a QA score against a checklist, from a recording, a transcript or an agent's own turns. Verified live against Azure OpenAI. · Analitik pasca-panggilan: ringkasan, sentimen, topik, tindak lanjut, dan skor QA; diuji langsung dengan Azure OpenAI.
- Video calls record to AVI (`RecordingFormat.Avi`, `voipnet sip call --record out.avi`): the peer's own frames next to PCM audio, with the frame rate measured from the call. · Panggilan video bisa direkam ke AVI beserta audio PCM, tanpa encode ulang.
- `voipnet load` places calls at a chosen rate and concurrency and reports setup-time percentiles, failures by SIP code and the media quality it heard; it exits non-zero when any call failed. · `voipnet load` menguji beban dengan laju dan konkurensi tertentu lalu melaporkan waktu setup, kegagalan per kode SIP, dan kualitas media.
- OpenTelemetry tracing: every call is a span carrying its direction, codec and final quality, agent turns are child spans, and an outbound call continues the activity that placed it. · Tracing OpenTelemetry: setiap panggilan menjadi span dengan arah, codec, dan kualitas akhir; giliran agen menjadi span anak.
- The echo canceller is told the audio device's round trip (`StreamDelayMs`, `VoipCall.SetAudioDelay`, measured automatically by `CallAudioBridge`), so it lines the two signals up from the first frame. · Pembatal gema kini diberi tahu latensi perangkat audio sehingga penyelarasan tepat sejak awal.
- DNS SRV/NAPTR resolution (RFC 3263): a domain alone is enough to reach a provider, servers are tried in the order its records ask for, and one that stops answering is skipped for a minute. · Resolusi DNS SRV/NAPTR: cukup nama domain untuk menghubungi provider, server dicoba sesuai urutan record, dan yang tidak menjawab dilewati sementara.
- SIP session timers (RFC 4028) and reliable provisional responses with PRACK (RFC 3262): calls are refreshed by re-INVITE and hung up when nobody refreshes them, and a 180 is resent until the caller acknowledges it. · Session timer SIP dan respons provisional reliable dengan PRACK: panggilan diperbarui lewat re-INVITE dan ditutup bila tidak ada yang memperbaruinya.
- RTCP sender and receiver reports with report blocks: `CallStatistics` gains `RoundTripMs`, `RemoteLossPercent` and `RemoteJitterMs`, and the CLI shows them. · Report RTCP dengan report block; statistik panggilan menampilkan RTT dan laporan dari lawan.
- Opus adapts to those reports (lower bitrate and more FEC as loss rises) and supports DTX through `OpusDtx`. · Opus menyesuaikan diri dengan laporan itu dan mendukung DTX.
- RTCP XR VoIP metrics (RFC 3611) in both directions; `CallStatistics.RemoteMos` is what the peer hears. · Metrik VoIP RTCP XR dua arah; `RemoteMos` adalah MOS di sisi lawan.
- Echo cancellation, noise suppression and gain control on the WebRTC audio processing pipeline (`EchoCancellation`, `NoiseSuppression`, `AutoGain`). · Pembatalan gema, peredam bising, dan kontrol gain.
- Burst and gap metrics in RTCP XR, so clustered loss is visible and not just an average. · Metrik burst dan gap di RTCP XR.
- Certificates can be renewed at run time with `ReloadTls()`. · Sertifikat bisa diperbarui saat berjalan.
- Video calls: `Video = true` puts an `m=video` stream next to the audio one (its own RTP port, SRTP and ICE), and `SendVideoFrame`/`VideoFrameReceived` carry whole H.264 or VP8 frames. A frame that loses packets is dropped and the engine asks the sender for a keyframe (RTCP PLI/FIR); `RequestKeyframe()` asks on demand. Encoding stays with the application for now. · Panggilan video: stream `m=video` di samping audio beserta API pengiriman dan penerimaan frame; encoding masih di sisi aplikasi.
- Video RTP payload formats (H.264 FU-A/STAP-A, VP8) with frame reassembly and keyframe detection, the first piece of the video milestone. · Format payload RTP video dengan perakitan frame, langkah pertama milestone video.
- Mutual TLS: callers can be asked for a certificate, and a connection that dies mid-request (a rejected handshake, a restart) now fails that request immediately instead of after the 32 second timeout. · TLS dua arah, dan koneksi yang putus langsung menggagalkan request.
- Browser interop runs in CI: Chrome and Firefox place a real call through the WebRTC gateway sample on every build. · Interop browser dijalankan di CI pada setiap build.
- Criterion benchmarks for codecs, SRTP and the conference mixer, run in CI; the mixer no longer allocates per frame and the G.722 delay line no longer copies its history on every sample pair. · Benchmark Criterion dijalankan di CI.

### 1.2.0 — 2026-09-17

- `RealtimeVoiceAgent` speaks the GA realtime protocol and Azure OpenAI (`RealtimeVoiceOptions.ForAzure`, `ErrorReceived`); verified live on a call. · Agen realtime mendukung protokol GA dan Azure OpenAI; diuji langsung.
- ElevenLabs verified live: Indonesian TTS → STT round trip, and a `VoiceAgent` on a real call (ElevenLabs STT/TTS + Azure OpenAI) answering a spoken question. The default voice is now a premade voice that free plans may use. · ElevenLabs diuji langsung, termasuk voice agent pada panggilan nyata.
- Speech providers report the provider's error body (for example "paid_plan_required") instead of a bare status code; streamed TTS chunks always end on a sample boundary. · Pesan error provider suara kini jelas; chunk TTS selalu utuh per sampel.
- Agents no longer fail when the caller hangs up while they speak. · Agen tidak gagal saat penelepon menutup telepon ketika agen berbicara.
- Native Opus (libopus via the `opus` crate): offered first by default, FEC-based loss recovery, `telephone-event/48000` so DTMF follows the audio clock; browsers now get Opus instead of G.722. Building the engine needs CMake. · Opus native: default pertama, pemulihan FEC, DTMF 48 kHz; browser kini memakai Opus. Build engine memerlukan CMake.

### 1.1.0 — 2026-09-17

- SIP over TLS with rustls: Mozilla roots, custom CA, SHA-256 pinning, PEM or self-signed identity; a failed handshake ends the request with 503 at once. · SIP melalui TLS; handshake yang gagal langsung mengakhiri request dengan 503.
- SIP over WebSocket (`ws`, `wss`, RFC 7118), server and client. · SIP over WebSocket, server dan client.
- DTLS-SRTP keying (`SrtpKeying.Dtls`) and SRTP AEAD-AES-GCM profiles, so browsers can call Voip.NET directly. · Kunci DTLS-SRTP dan profil AES-GCM, sehingga browser bisa menelepon Voip.NET langsung.
- `samples/VoipNet.WebPhone`: a WebRTC to SIP gateway, verified with a real browser call. · Sample gateway WebRTC ke SIP, diverifikasi dengan panggilan browser sungguhan.
- Full ICE agent (RFC 8445) with trickle ICE, ICE restart (`VoipCall.RestartIce()`) and consent freshness; WebPhone now trickles candidates. · Agen ICE penuh dengan trickle ICE, ICE restart, dan consent freshness.
- Browser interop verified with Edge and Firefox; Firefox negotiates SRTP AEAD-AES-128-GCM. `tools/VoipNet.DocShots` drives Firefox over WebDriver BiDi (`--firefox`) and exits 1 when a browser call fails. · Interop browser diverifikasi dengan Edge dan Firefox.
- Tests wait for media and IVR prompts instead of sleeping fixed times, which failed on slow CI runners. · Test menunggu media dan prompt IVR, bukan jeda tetap.
- CLI: `--transport tls|ws|wss`, `--tls-pin`, `--tls-insecure`, `--dtls`. `--srtp` now applies to every `sip` command; before, `sip call` ignored it. · `--srtp` kini berlaku untuk semua perintah `sip`; sebelumnya `sip call` mengabaikannya.

### 1.0.0 — 2026-09-16

- First complete implementation of engine, SDK, AI, enterprise packages, CLI, five samples, bilingual docs. · Implementasi lengkap pertama.
- Fixes found during verification · Perbaikan yang ditemukan saat verifikasi: CANCEL/INVITE transaction key collision; REFER target losing its port; voice agent unable to barge in while speaking; supervisor listen-in muting the supervisor instead of the bridge; reasoning-model request parameters (`max_completion_tokens`, `reasoning_effort`); CRM phone matching across `+62`/`0` prefixes; events arriving before request waiters registered.
