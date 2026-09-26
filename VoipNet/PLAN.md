# Roadmap · Rencana Pengembangan

Voip.NET — made by Gravicode Studios, led by Kang Fadhil · dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

Current state · Kondisi saat ini: see · lihat [Progress.md](Progress.md).

## 1.1 — Secure transports and browser reach · Transport aman dan jangkauan browser

SIP over TLS and WebSocket, DTLS-SRTP, the full ICE agent with trickle and restart, and browser interop with Edge and Firefox are done (see Progress.md). What remains: · Sudah selesai: SIP melalui TLS dan WebSocket, DTLS-SRTP, agen ICE penuh dengan trickle dan restart, serta interop dengan Edge dan Firefox. Sisanya:

- Safari interop (no macOS machine here to try it). · Interop Safari.

## 1.2 — Codecs and media · Codec dan media

- G.729 (royalty-free since 2017) native implementation. · G.729 native.

## 1.3 — 🎥 Video · Fitur video

Video is a first-class media type now: negotiated, packetised, encoded and decoded, recorded, composed
and forwarded. What is left is mostly other platforms and other codecs. · Video kini tipe media penuh:
dinegosiasikan, dipaketkan, di-encode dan didekode, direkam, disusun, dan diteruskan. Sisanya sebagian
besar soal platform dan codec lain.

- **Video calls** — `m=video` next to audio, a second RTP stream per call with its own port,
  encryption and ICE, H.264 (RFC 6184) and VP8 (RFC 7741) packetisation with frame assembly, keyframe
  requests both ways, bandwidth estimation (REMB) and lip sync. `VoipNet.Video` encodes and decodes
  H.264 and reads a camera through Media Foundation. Still to do: the same for other platforms —
  VideoToolbox, VA-API/FFmpeg, AVFoundation and V4L2. · **Panggilan video** — stream `m=video` kedua
  dengan enkripsi, ICE, paketisasi H.264/VP8, permintaan keyframe, REMB, dan sinkronisasi bibir;
  `VoipNet.Video` meng-encode/decode H.264 dan membaca kamera di Windows. Sisanya: platform lain.
- **Video conferencing** — the bridge forwards one participant to the rest, chosen by active speaker
  or by pin, holding each viewer until a keyframe arrives. Simulcast goes both ways: `VideoEncodings`
  offers several encodings and `SendVideoFrameAs` labels each frame, and a peer's encodings are
  measured and chosen between. `VideoCompositor` lays pictures out in a grid, picture-in-picture or
  spotlight for an application that would rather mix than forward. Still to do: SVC layer selection.
  · **Konferensi video** — bridge meneruskan satu peserta ke yang lain (pembicara aktif atau yang
  dipin); simulcast dua arah; `VideoCompositor` menyusun grid/PiP/spotlight. Sisanya: pemilihan layer
  SVC.
- **Screen sharing** — a second stream marked `a=content:slides`, offered and withdrawn by re-INVITE.
  The Softphone shares a screen with `VideoCapture.OpenScreen`, a browser shares one with
  `getDisplayMedia` in the meeting sample, and a room sends it to everybody at once. Still to do:
  capture beyond the GDI copy (Desktop Duplication, for the rate a driver will not give through GDI;
  ScreenCaptureKit; PipeWire), and the rest of BFCP floor control — the messages and the floor state
  machine are in `sip/bfcp.rs` with tests, and what is left is negotiating it in SDP and running it
  over its own socket. · **Berbagi layar** — stream kedua
  `a=content:slides`; Softphone membagikan layar, browser lewat `getDisplayMedia`, dan ruangan
  mengirimnya ke semua orang. Sisanya: penangkapan yang lebih cepat dan floor control BFCP.
- **Video recording** — `RecordingFormat.Mp4` (H.264 with each frame's own duration) and
  `RecordingFormat.Avi` (H.264 or VP8 at one measured rate), neither re-encoding anything, and
  `ConferenceRecorder` for a whole room as one composed picture with the voices mixed. Still to do:
  AAC audio, which needs an encoder this SDK does not carry, and in-browser playback of video
  recordings in the Call Centre sample. · **Perekaman video** — MP4 dan AVI tanpa encode ulang, serta
  `ConferenceRecorder` untuk seluruh ruangan. Sisanya: audio AAC dan pemutaran video di sample Call
  Centre.
- **Samples and tools** — the Blazor meeting room, the Softphone's camera and screen share, the
  gallery's video page, `voipnet sip call --video|--camera|--screen`, and RTP video stream analysis.
  · **Sample dan tools** — ruang rapat Blazor, kamera dan berbagi layar di Softphone, halaman video di
  gallery, serta opsi video di CLI.
- **GPU acceleration** — a card's encoder is used when it will work from ordinary memory, and
  `IVideoEncoder.Implementation` says which one answered. Still to do: the asynchronous transform
  model most cards want (NVENC, Quick Sync, AMF, VideoToolbox), Direct3D surfaces end to end, and GPU
  scaling for conferences. · **Akselerasi GPU** — encoder kartu grafis dipakai bila mau bekerja dari
  memori biasa; sisanya model asinkron, surface Direct3D, dan penskalaan di GPU.

## 1.4 — AI · Kecerdasan buatan

- Amazon Transcribe streaming is implemented (`AmazonTranscribeSpeechToText`) and tested against a stand-in; it still needs a run against the live service, which needs an AWS account. · Amazon Transcribe streaming sudah ada, tinggal diuji ke layanan aslinya.
- Gemini Live has an agent (`GeminiLiveVoiceAgent`) tested against a stand-in; it still needs a run against Google, which needs a key. · Agen Gemini Live sudah ada, tinggal diuji ke layanan aslinya.

## 1.5 — Contact centre · Contact center

Everything here has shipped: the IVR designer with versions and A/B tests, scheduled routing, callbacks and estimated wait, shared state in SQL, historical reports with a Grafana dashboard, CRM connectors, and ZRTP. · Semua sudah selesai: desainer IVR dengan versi dan uji A/B, routing terjadwal, callback dan estimasi tunggu, state bersama di SQL, laporan historis dengan dashboard Grafana, konektor CRM, dan ZRTP.

## 1.6 — Platform and operations · Platform dan operasional

Everything here has shipped: the six-runtime build/test/publish matrix, benchmarks with a committed baseline, browser interop on every build, container images with a Helm chart, and nightly live-provider tests. · Semua sudah selesai: matriks enam runtime, benchmark dengan baseline, interop browser tiap build, image container dan chart Helm, serta uji provider langsung tiap malam.

## Principles · Prinsip

1. Real protocols, real tests: every feature ships with a loopback or live test. · Setiap fitur disertai test.
2. Native for the media path, .NET for everything developers touch. · Native untuk media, .NET untuk developer.
3. Honest status: pass-through and planned items are labelled as such. · Status jujur.
