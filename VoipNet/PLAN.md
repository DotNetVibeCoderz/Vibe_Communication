# Roadmap · Rencana Pengembangan

Voip.NET — made by Gravicode Studios, led by Kang Fadhil · dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

Current state · Kondisi saat ini: see · lihat [Progress.md](Progress.md).

## 1.1 — Secure transports and browser reach · Transport aman dan jangkauan browser

SIP over TLS and WebSocket, DTLS-SRTP, the full ICE agent with trickle and restart, and browser interop with Edge and Firefox are done (see Progress.md). What remains: · Sudah selesai: SIP melalui TLS dan WebSocket, DTLS-SRTP, agen ICE penuh dengan trickle dan restart, serta interop dengan Edge dan Firefox. Sisanya:

- Browser interop runs in CI (Edge and Firefox on Linux, Safari on macOS). · Uji interop browser di CI, termasuk Safari.
- **Data channels** (SCTP over DTLS, RFC 8831) for chat and file transfer next to calls. · Data channel untuk chat dan transfer file.
- Mutual TLS (client certificates) and certificate reload without restart. · TLS dua arah dan muat ulang sertifikat.
- Sending our own candidates by trickle ICE (useful when STUN/TURN gathering is slow). · Mengirim kandidat sendiri lewat trickle ICE.

## 1.2 — Codecs and media · Codec dan media

- G.729 (royalty-free since 2017) native implementation. · G.729 native.
- Tune echo cancellation for device delays (the pipeline is in place; `set_stream_delay_ms` is not wired to the audio devices yet). · Penyetelan AEC untuk delay perangkat.
- Track benchmark results over time (the numbers are printed in CI, but nothing compares them between runs yet). · Lacak hasil benchmark antar-run.

## 1.3 — 🎥 Video · Fitur video

Today H.264, VP8 and VP9 are negotiated as pass-through payloads only. This milestone makes video a first-class media type. · Saat ini H.264, VP8, dan VP9 hanya dinegosiasikan sebagai payload pass-through; milestone ini menjadikan video tipe media utama.

- **Video call support** — voice + video in one SIP session: `m=video` offer/answer next to audio, a second RTP stream per call (BUNDLE-ready), H.264 (RFC 6184) and VP8 (RFC 7741) packetisation, jitter buffer and frame assembly, keyframe requests (RTCP PLI/FIR), bandwidth estimation and adaptive bitrate, lip sync from RTCP sender reports. .NET API: `VoipCall.VideoFrameReceived`, `SendVideoFrame`, camera capture and decode/encode through platform codecs (Media Foundation, VideoToolbox, VA-API/FFmpeg). · **Dukungan panggilan video** — suara + video dalam satu sesi SIP: offer/answer `m=video`, stream RTP kedua per panggilan, paketisasi H.264/VP8, jitter buffer dan perakitan frame, permintaan keyframe (PLI/FIR), estimasi bandwidth dan bitrate adaptif, sinkronisasi bibir; API .NET untuk frame video, kamera, serta encode/decode via codec platform.
- **Video conferencing** — multiparty video with layout control: server-side mixing (compositor with grid, speaker-focus and picture-in-picture layouts driven by active-speaker detection) and an SFU mode that forwards streams with simulcast/SVC layer selection; per-participant mute, pin and spotlight; `VoipConference.Layout`. · **Konferensi video** — video multipihak dengan kontrol layout: mode mixing (compositor grid, fokus pembicara, picture-in-picture berdasarkan deteksi pembicara aktif) dan mode SFU dengan simulcast/SVC; mute, pin, dan spotlight per peserta.
- **Screen sharing** for desktop and web apps: capture a screen, window or region (Windows Graphics Capture, ScreenCaptureKit, PipeWire), send it as a second video stream (`a=content:slides`, BFCP-style floor control for SIP endpoints), content-optimised encoding (high resolution, low frame rate); browser sharing via `getDisplayMedia` over the existing WebRTC media path; Softphone and Blazor samples. · **Berbagi layar** untuk aplikasi desktop/web: tangkap layar, jendela, atau area, kirim sebagai stream video kedua dengan floor control, encoding yang dioptimalkan untuk konten; di browser lewat `getDisplayMedia` lewat jalur media WebRTC yang sudah ada.
- **Video recording** — save audio + video to MP4 (H.264/AAC) or AVI: `CallRecorder` gains video tracks, fragmented MP4 for crash-safe long recordings, timestamp alignment from RTP, conference recordings from the compositor output, `RecordingService` indexing and in-browser playback in the Call Centre sample. · **Perekaman video** — simpan audio + video ke MP4 (H.264/AAC) atau AVI: track video di `CallRecorder`, fragmented MP4 agar aman untuk rekaman panjang, penyelarasan timestamp RTP, rekaman konferensi dari keluaran compositor, indeks di `RecordingService`, dan pemutaran di sample Call Centre.
- **Samples and tools** — video in Softphone and Gallery, a Blazor video meeting page, `voipnet sip call --video` and RTP video stream analysis (frame rate, keyframe interval, freeze detection). · **Sample dan tools** — video di Softphone dan Gallery, halaman rapat video Blazor, `voipnet sip call --video`, dan analisis stream video RTP.
- **GPU acceleration** — hardware encode/decode (NVENC, Quick Sync, AMF, VideoToolbox) and GPU scaling/compositing for conferences. · **Akselerasi GPU** untuk encode/decode dan compositing konferensi.

## 1.4 — AI · Kecerdasan buatan

- Amazon Transcribe streaming (event-stream over HTTP/2, SigV4). · Amazon Transcribe streaming.
- Azure AI Speech STT/TTS; Deepgram Aura TTS; Cartesia TTS. · Provider tambahan.
- Live verification suite for every speech provider (opt-in keys). · Uji langsung setiap provider suara.
- Semantic turn detection (end-of-utterance model) in addition to VAD. · Deteksi akhir ucapan berbasis semantik.
- Realtime agent for Gemini Live (OpenAI and Azure OpenAI realtime are done). · Agen realtime Gemini Live.
- Post-call analytics: transcription, summary, sentiment, QA scoring on `RecordingService` output. · Analitik pasca-panggilan.
- Agent assist: live transcript and suggested replies for human agents. · Agent assist untuk agen manusia.

## 1.5 — Contact centre · Contact center

- Persistent queue and agent state (Redis / SQL) for multiple PBX nodes. · State antrean dan agen yang persisten.
- Callbacks (keep place in queue), estimated wait time, scheduled routing. · Callback dan estimasi waktu tunggu.
- Visual IVR designer: drag-and-drop graph, versioning, A/B tests. · Desainer IVR visual.
- Workforce dashboards: historical reports, CSV/Parquet export, Grafana templates. · Laporan historis.
- ZRTP (RFC 6189) end-to-end media encryption. · Enkripsi ZRTP.
- CRM connectors: HubSpot, Salesforce, Dynamics 365, Odoo. · Konektor CRM.

## 1.6 — Platform and operations · Platform dan operasional

- Benchmarks and nightly live-provider test jobs in CI (the six-runtime build/test/publish matrix already ships in 1.0). · Benchmark dan uji provider langsung terjadwal di CI (matriks enam runtime sudah ada sejak 1.0).
- Container images for the Realtime Agent and Call Centre samples; Helm chart. · Image container dan Helm chart.
- OpenTelemetry tracing across SIP dialogs and AI turns. · Tracing OpenTelemetry.
- Load testing tool (`voipnet load`) for concurrent calls and CPS. · Tool uji beban.
- DNS SRV/NAPTR resolution (RFC 3263) and failover. · Resolusi DNS SRV/NAPTR.
- SIP session timers (RFC 4028), PRACK (RFC 3262). · Session timer dan PRACK.

## Principles · Prinsip

1. Real protocols, real tests: every feature ships with a loopback or live test. · Setiap fitur disertai test.
2. Native for the media path, .NET for everything developers touch. · Native untuk media, .NET untuk developer.
3. Honest status: pass-through and planned items are labelled as such. · Status jujur.
