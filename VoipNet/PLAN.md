# Roadmap · Rencana Pengembangan

Voip.NET — made by Gravicode Studios, led by Kang Fadhil · dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

Current state · Kondisi saat ini: see · lihat [Progress.md](Progress.md).

## 1.1 — Secure transports and browser reach · Transport aman dan jangkauan browser

- **SIP over TLS (SIPS)** with rustls; certificate pinning option. · SIP melalui TLS dengan rustls; opsi certificate pinning.
- **DTLS-SRTP** (RFC 5763/5764) with self-signed certificate fingerprints in SDP — the prerequisite for browser WebRTC. · Prasyarat WebRTC di browser.
- **Full ICE** agent: candidate pairing, priorities, nomination, consent freshness, ICE restarts. · Agen ICE penuh.
- **WebRTC gateway sample**: browser softphone (JS `RTCPeerConnection`) ↔ Voip.NET ↔ SIP. · Sample gateway WebRTC.
- SIP over WebSocket (RFC 7118). · SIP melalui WebSocket.

## 1.2 — Codecs and media · Codec dan media

- **Opus** native encode/decode (optional `opus` cargo feature), in-band FEC and DTX. · Opus native.
- G.729 (royalty-free since 2017) native implementation. · G.729 native.
- Acoustic echo cancellation and noise suppression for the softphone path (WebRTC AEC3-style). · Echo cancellation dan noise suppression.
- Hand-tuned SIMD for G.722 QMF and the conference mixer; benchmarks in CI. · SIMD manual dan benchmark.
- Video: RTP packetisation helpers for H.264 (RFC 6184) and VP8 (RFC 7741), keyframe requests (RTCP PLI/FIR). · Helper paketisasi video.
- RTCP receiver reports with round-trip time; XR VoIP metrics (RFC 3611). · RTCP RR dan XR.

## 1.3 — AI · Kecerdasan buatan

- Amazon Transcribe streaming (event-stream over HTTP/2, SigV4). · Amazon Transcribe streaming.
- Azure AI Speech STT/TTS; Deepgram Aura TTS; Cartesia TTS. · Provider tambahan.
- Live verification suite for every speech provider (opt-in keys). · Uji langsung setiap provider suara.
- Semantic turn detection (end-of-utterance model) in addition to VAD. · Deteksi akhir ucapan berbasis semantik.
- Realtime agents for Gemini Live and Azure OpenAI realtime. · Agen realtime Gemini Live dan Azure.
- Post-call analytics: transcription, summary, sentiment, QA scoring on `RecordingService` output. · Analitik pasca-panggilan.
- Agent assist: live transcript and suggested replies for human agents. · Agent assist untuk agen manusia.

## 1.4 — Contact centre · Contact center

- Persistent queue and agent state (Redis / SQL) for multiple PBX nodes. · State antrean dan agen yang persisten.
- Callbacks (keep place in queue), estimated wait time, scheduled routing. · Callback dan estimasi waktu tunggu.
- Visual IVR designer: drag-and-drop graph, versioning, A/B tests. · Desainer IVR visual.
- Workforce dashboards: historical reports, CSV/Parquet export, Grafana templates. · Laporan historis.
- ZRTP (RFC 6189) end-to-end media encryption. · Enkripsi ZRTP.
- CRM connectors: HubSpot, Salesforce, Dynamics 365, Odoo. · Konektor CRM.

## 1.5 — Platform and operations · Platform dan operasional

- CI matrix: win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64; packages carry all runtimes. · Matriks CI lintas platform.
- Container images for the Realtime Agent and Call Centre samples; Helm chart. · Image container dan Helm chart.
- OpenTelemetry tracing across SIP dialogs and AI turns. · Tracing OpenTelemetry.
- Load testing tool (`voipnet load`) for concurrent calls and CPS. · Tool uji beban.
- DNS SRV/NAPTR resolution (RFC 3263) and failover. · Resolusi DNS SRV/NAPTR.
- SIP session timers (RFC 4028), PRACK (RFC 3262). · Session timer dan PRACK.

## Principles · Prinsip

1. Real protocols, real tests: every feature ships with a loopback or live test. · Setiap fitur disertai test.
2. Native for the media path, .NET for everything developers touch. · Native untuk media, .NET untuk developer.
3. Honest status: pass-through and planned items are labelled as such. · Status jujur.
