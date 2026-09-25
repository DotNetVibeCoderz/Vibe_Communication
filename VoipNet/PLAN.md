# Roadmap · Rencana Pengembangan

Voip.NET — made by Gravicode Studios, led by Kang Fadhil · dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

Current state · Kondisi saat ini: see · lihat [Progress.md](Progress.md).

## 1.1 — Secure transports and browser reach · Transport aman dan jangkauan browser

SIP over TLS and WebSocket, DTLS-SRTP, the full ICE agent with trickle and restart, and browser interop with Edge and Firefox are done (see Progress.md). What remains: · Sudah selesai: SIP melalui TLS dan WebSocket, DTLS-SRTP, agen ICE penuh dengan trickle dan restart, serta interop dengan Edge dan Firefox. Sisanya:

- Safari interop (no macOS machine here to try it). · Interop Safari.

## 1.2 — Codecs and media · Codec dan media

- G.729 (royalty-free since 2017) native implementation. · G.729 native.

## 1.3 — 🎥 Video · Fitur video

Today H.264, VP8 and VP9 are negotiated as pass-through payloads only. This milestone makes video a first-class media type. · Saat ini H.264, VP8, dan VP9 hanya dinegosiasikan sebagai payload pass-through; milestone ini menjadikan video tipe media utama.

- **Video call support** — voice + video in one SIP session: `m=video` offer/answer next to audio, a second RTP stream per call, H.264 (RFC 6184) and VP8 (RFC 7741) packetisation with frame assembly, the .NET API `VoipCall.SendVideoFrame`/`VideoFrameReceived` and keyframe requests (RTCP PLI/FIR, automatic on loss and on demand through `RequestKeyframe`) (all done); still to do: camera capture, and platform codecs beyond Windows — `VoipNet.Video` encodes and decodes H.264 through Media Foundation, VideoToolbox and VA-API/FFmpeg are not wired up. · **Dukungan panggilan video** — suara + video dalam satu sesi SIP: offer/answer `m=video`, stream RTP kedua per panggilan, paketisasi H.264/VP8, jitter buffer dan perakitan frame, permintaan keyframe (PLI/FIR), estimasi bandwidth (REMB), sinkronisasi bibir lewat sender report; API .NET untuk frame video, kamera, serta encode/decode via codec platform.
- **Video conferencing** — speaker-focus and pin are done (the bridge forwards one participant's video to the rest, with active-speaker detection and a keyframe before each switch); still to do: a compositor for grid and picture-in-picture layouts (needs decode/encode), sending simulcast (receiving and choosing between a peer's encodings is done), and SVC layer selection. · **Konferensi video** — video multipihak dengan kontrol layout: mode mixing (compositor grid, fokus pembicara, picture-in-picture berdasarkan deteksi pembicara aktif) dan mode SFU dengan simulcast/SVC; mute, pin, dan spotlight per peserta.
- **Screen sharing**: the second video stream (`a=content:slides`, offered and withdrawn by re-INVITE, frames labelled per stream) is done; still to do: capture (Windows Graphics Capture, ScreenCaptureKit, PipeWire), content-optimised encoding, BFCP-style floor control, browser sharing via `getDisplayMedia`, and Softphone and Blazor samples. · **Berbagi layar** untuk aplikasi desktop/web: tangkap layar, jendela, atau area, kirim sebagai stream video kedua dengan floor control, encoding yang dioptimalkan untuk konten; di browser lewat `getDisplayMedia` lewat jalur media WebRTC yang sudah ada.
- **Video recording** — done for a single call: `RecordingFormat.Mp4` (H.264 with PCM audio, one duration per frame from the RTP timestamps) and `RecordingFormat.Avi` (H.264 or VP8, one measured frame rate), neither re-encoding anything. Still to do: AAC audio, which needs an encoder this SDK does not carry, conference recordings from the compositor output, and in-browser playback in the Call Centre sample. · **Perekaman video** — untuk satu panggilan sudah selesai: `RecordingFormat.Mp4` (H.264 + audio PCM, durasi tiap frame dari timestamp RTP) dan `RecordingFormat.Avi`, keduanya tanpa encode ulang. Sisanya: audio AAC (butuh encoder), rekaman konferensi dari keluaran compositor, dan pemutaran di sample Call Centre.
- **Samples and tools** — the Blazor meeting page (`samples/VoipNet.Meeting`), `voipnet sip call --video` and RTP video stream analysis are done; still to do: video in the Softphone and Gallery samples, which needs decoding. · **Sample dan tools** — halaman rapat Blazor, `voipnet sip call --video`, dan analisis stream video RTP sudah ada; sisanya video di sample Softphone dan Gallery.
- **GPU acceleration** — hardware encode/decode (NVENC, Quick Sync, AMF, VideoToolbox) and GPU scaling/compositing for conferences. · **Akselerasi GPU** untuk encode/decode dan compositing konferensi.

## 1.4 — AI · Kecerdasan buatan

- Amazon Transcribe streaming (event-stream over HTTP/2, SigV4). · Amazon Transcribe streaming.
- Realtime agent for Gemini Live (OpenAI and Azure OpenAI realtime are done). · Agen realtime Gemini Live.

## 1.5 — Contact centre · Contact center

Everything here has shipped: the IVR designer with versions and A/B tests, scheduled routing, callbacks and estimated wait, shared state in SQL, historical reports with a Grafana dashboard, CRM connectors, and ZRTP. · Semua sudah selesai: desainer IVR dengan versi dan uji A/B, routing terjadwal, callback dan estimasi tunggu, state bersama di SQL, laporan historis dengan dashboard Grafana, konektor CRM, dan ZRTP.

## 1.6 — Platform and operations · Platform dan operasional

Everything here has shipped: the six-runtime build/test/publish matrix, benchmarks with a committed baseline, browser interop on every build, container images with a Helm chart, and nightly live-provider tests. · Semua sudah selesai: matriks enam runtime, benchmark dengan baseline, interop browser tiap build, image container dan chart Helm, serta uji provider langsung tiap malam.

## Principles · Prinsip

1. Real protocols, real tests: every feature ships with a loopback or live test. · Setiap fitur disertai test.
2. Native for the media path, .NET for everything developers touch. · Native untuk media, .NET untuk developer.
3. Honest status: pass-through and planned items are labelled as such. · Status jujur.
