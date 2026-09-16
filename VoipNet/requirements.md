Name: Voip.NET

Description:
Voip.Net SDK — a Rust‑powered, .NET‑friendly VoIP + AI platform that merges SIP/RTP/WebRTC with LLM call center intelligence, STT/TTS, and enterprise integrations. Features:

---

 🏛 Core Architecture
- Rust SIP/RTP engine for performance and safety.  
- .NET bindings with async/await and DI support.  
- Cross‑platform runtime (Windows, Linux, macOS, containers).  
- NuGet distribution for easy adoption.  

---

 📡 Telephony Features
- Full SIP stack (REGISTER, INVITE, ACK, BYE, REFER, OPTIONS).  
- Advanced call control: transfer, hold, conferencing, recording.  
- RTP media handling with adaptive jitter buffer.  
- Codec support: G.711, G.722, G.729, Opus, SILK, Speex, H.264, VP8/VP9.  
- DTMF signaling: in‑band, RFC2833, SIP INFO.  
- Hardware acceleration via SIMD/GPU.  

---

 🌐 WebRTC Integration
- ICE/STUN/TURN for NAT traversal.  
- DTLS‑SRTP for secure media.  
- Data channels for messaging.  
- Browser interop with JS WebRTC clients.  

---

 🤖 AI Call Center Features
- Multi‑LLM connectors: OpenAI, Anthropic Claude, Google Gemini, OpenAI‑compatible endpoints.  
- Realtime model support for fast, interactive responses.  
- Kernel/AI Functions with Semantic Kernel and Microsoft.Extensions.AI.  
- Conversation orchestration: audio → STT → LLM → TTS → RTP loop.  
- Context persistence for multi‑turn dialog and agent handoff.  

---

 🎙️ STT/TTS Providers
- ElevenLabs for expressive voices.  
- Deepgram for real‑time transcription.  
- OpenAI Whisper/TTS.  
- Google Cloud Speech/TTS.  
- Amazon Polly/Transcribe.  
- ElBruno.Realtime open‑source STT/TTS pipeline.  

---

 🛠 Developer Experience
- High‑level API: `VoipClient.CallAsync("sip:user@domain")`.  
- Event‑driven model: call state, media events.  
- Sample apps: AI softphone, call center dashboard, IVR with AI, realtime agent demo, Voip.Net Gallery - expose all SDK functionalities and sample code.  
- CLI tools: SIP tester, RTP analyzer.  
- Diagnostics: logging, packet capture, performance counters.  

---

 🏢 Enterprise Features
- IVR builder with AI dialog.  
- Call center toolkit: queue management, agent monitoring.  
- Recording & analytics: WAV/MP3 storage, dashboards.  
- Security: TLS, SRTP, ZRTP, end‑to‑end encryption.  
- CRM integration via AI Functions.  

---

 📊 Master Feature Table

| Category | Features |
|----------|----------|
| Core | Rust engine, .NET bindings, cross‑platform, NuGet |
| Telephony | SIP stack, RTP, codecs, DTMF, hardware acceleration |
| WebRTC | ICE/STUN/TURN, DTLS‑SRTP, browser interop |
| AI Call Center | Multi‑LLM, realtime models, semantic kernel, orchestration |
| STT/TTS | ElevenLabs, Deepgram, OpenAI, Google, Amazon, ElBruno |
| Developer | High‑level API, events, sample apps, CLI, diagnostics |
| Enterprise | IVR, call center toolkit, recording, analytics, security, CRM |

---

This complete feature set makes Voip.Net a telephony + AI orchestration SDK: high‑performance Rust core, developer‑friendly .NET APIs, and deep integration with LLMs and speech services for intelligent, real‑time call centers.  

Notes:
- gunakan .NET 10
- Semua UI UX aplikasi buat yang keren dan user friendly dengan bantuan skill 'frontend-design'
- Untuk sample apps tipe desktop buat dengan Avalonia UI Multiplatform, untuk tipe web gunakan blazor server
- optimasi koding agar dapat performa terbaik dan memory efisien
- gunakan naming convention standard c#
- readme dan docs dalam bahasa Indonesia dan English
- dokumentasi lengkap di folder docs
- Progress.md untuk tracking development, PLAN.md untuk roadmap pengembangan
- jika ada hal-hal yang penting perlu ditambahkan, silakan ditambahkan langsung biar lengkap.
- di aplikasi dan dokumentasi tambahkan informasi dibuat oleh Gravicode Studios dipimpin Kang Fadhil
- untuk publish nuget, api key ada di 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt'
- ujicoba dengan LLM real bisa gunakan api dari 'C:\Users\mifma\Documents\CodeSandbox\testkey.txt'
- screenshot-screenshot tambahkan pada dokumentasi dan readme
---
