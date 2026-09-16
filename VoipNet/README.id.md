# Voip.NET

**Platform VoIP + AI bertenaga Rust yang ramah .NET.** SIP, RTP, dan SRTP dari engine native; panggilan, percakapan, dan komponen contact center dari API .NET 10 yang modern; model suara dan bahasa langsung tersambung ke jalur audio.

> Dibuat oleh **Gravicode Studios**, dipimpin oleh **Kang Fadhil**.
> 🇬🇧 Read in English: [README.md](README.md)

![Sample softphone saat panggilan berlangsung](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/softphone-call.png)

```csharp
await using var client = new VoipClient(new VoipClientOptions
{
    Domain = "pbx.contoh.co.id", Username = "1001", Password = "rahasia", RegisterOnStart = true,
});
await client.StartAsync();

var call = await client.CallAsync("sip:1002@pbx.contoh.co.id");
call.SendAudio(pcm16k, sampleRate: 16000);
Console.WriteLine($"{call.Codec} · MOS {call.GetStatistics().Mos:0.0}");
await call.HangupAsync();
```

## Kenapa Voip.NET

- **Native di bagian yang penting.** User agent SIP, negosiasi SDP, RTP/RTCP, SRTP, jitter buffer adaptif, codec, dan ICE ditulis dengan Rust. Audio masuk ke .NET lewat callback tanpa salinan (zero-copy).
- **.NET di tempat Anda bekerja.** `async`/`await`, event, stream audio `IAsyncEnumerable`, dependency injection, `System.Diagnostics.Metrics`.
- **AI di dalam panggilan, bukan di sampingnya.** Speech-to-text, model `IChatClient` apa pun, text-to-speech, dan barge-in berjalan sebagai satu loop pada panggilan langsung. Model realtime speech-to-speech juga didukung.
- **Contact center sudah tersedia.** Alur IVR dengan hand-off ke AI, antrean dengan routing berbasis skill, bridging ke agen, perekaman, analitik, dan tools CRM.

## Paket

| Paket | Isinya |
| --- | --- |
| `VoipNet.Core` | `VoipClient`, `VoipCall`, konferensi, DTMF, SRTP, diagnostik (pcap, analisis RTP, metrik). Berisi engine native. |
| `VoipNet.Audio` | Mikrofon dan speaker (OpenAL, lintas platform), perekaman WAV/MP3, resampling, deteksi aktivitas suara (VAD), nada. |
| `VoipNet.AI` | Klien chat untuk OpenAI, Azure OpenAI, DeepSeek/kompatibel OpenAI, Anthropic Claude, dan Google Gemini; provider suara; `VoiceAgent`; `RealtimeVoiceAgent`. |
| `VoipNet.Enterprise` | Builder dan runner IVR, `CallCenterService` (antrean, agen, routing), `RecordingService`, tools CRM. |
| `VoipNet.Cli` | .NET tool `voipnet`: SIP ping/register/call/listen/message dan analisis RTP. |

## Fitur

| Area | Status |
| --- | --- |
| SIP: REGISTER (digest auth, refresh, NAT keep-alive), INVITE/ACK/BYE/CANCEL, hold via re-INVITE, REFER blind dan attended (Replaces), OPTIONS, INFO, MESSAGE, NOTIFY | ✅ |
| Transport: UDP, TCP | ✅ · TLS direncanakan |
| RTP/RTCP dengan jitter buffer adaptif, packet-loss concealment, symmetric RTP | ✅ |
| Codec: G.722, G.711 μ-law/A-law, L16 (native) · G.729, Opus, SILK, Speex, H.264, VP8, VP9 (dinegosiasikan sebagai pass-through) | ✅ / pass-through |
| DTMF: RFC 4733, SIP INFO, pembangkitan dan deteksi in-band | ✅ |
| SRTP (AES-CM-128, HMAC-SHA1-80, kunci SDES) | ✅ · DTLS-SRTP direncanakan |
| ICE (kandidat host, server-reflexive, relay), STUN, alokasi TURN | ✅ dasar |
| Konferensi dengan mix-minus | ✅ |
| Rekaman WAV/MP3, stereo atau mono | ✅ |
| LLM: OpenAI, Azure OpenAI, DeepSeek & server kompatibel, Anthropic, Gemini — streaming dan tool calling | ✅ |
| STT: Deepgram (streaming), OpenAI, Google Cloud, ElevenLabs, ElBruno.Realtime · TTS: ElevenLabs, OpenAI, Google Cloud, Amazon Polly, ElBruno.Realtime | ✅ · Amazon Transcribe direncanakan |
| Voice agent: barge-in, streaming per kalimat, tools kontrol panggilan, memori percakapan, hand-off · Agen realtime speech-to-speech | ✅ |
| Builder IVR, antrean & routing skill, supervisor listen-in, layanan rekaman, metrik, tools CRM | ✅ |
| Diagnostik: SIP ke pcap, analisis stream RTP, metrik `dotnet-counters` | ✅ |
| Platform: Windows x64 dibangun dan diuji; Linux/macOS dibangun dari source | ✅ / lihat [build](docs/id/building.md) |

Daftar lengkap, termasuk yang masih direncanakan, ada di [PLAN.md](PLAN.md) dan [Progress.md](Progress.md).

## Contoh Aplikasi

| Sample | Jenis | Menampilkan |
| --- | --- | --- |
| [VoipNet.Softphone](samples/VoipNet.Softphone) | Desktop Avalonia | Dial pad, panggilan dengan trace audio langsung, hold/mute/transfer/rekam, ringkasan panggilan dengan AI, jalur demo bawaan. |
| [VoipNet.Gallery](samples/VoipNet.Gallery) | Desktop Avalonia | Setiap fitur SDK sebagai demo langsung beserta kode C#-nya. |
| [VoipNet.CallCenter](samples/VoipNet.CallCenter) | Blazor Server | Wallboard: antrean, agen, kualitas panggilan langsung, rekaman, supervisor AI. |
| [VoipNet.IvrStudio](samples/VoipNet.IvrStudio) | Blazor Server | Rancang IVR, telepon dari browser, serahkan penelepon ke agen AI. |
| [VoipNet.RealtimeAgent](samples/VoipNet.RealtimeAgent) | Console | Agen AI yang menjawab panggilan SIP (pipeline atau model realtime). |

| | |
| --- | --- |
| ![Gallery: voice agent](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/gallery-voice-agent.png) | ![Wallboard call center](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/callcenter-ai-supervisor.png) |
| ![Panggilan uji IVR Studio](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/ivrstudio-test-call.png) | ![Gallery: model bahasa](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/VoipNet/docs/images/gallery-ai-models.png) |

## Mulai Cepat

```bash
# Kebutuhan: .NET 10 SDK, Rust 1.80+ (hanya untuk membangun engine dari source)
git clone https://github.com/DotNetVibeCoderz/Vibe_Communication && cd Vibe_Communication/VoipNet
./build/build.ps1            # atau ./build/build.sh di Linux/macOS
dotnet run --project samples/VoipNet.Softphone
```

Lanjutkan dengan:

- [Memulai](docs/id/getting-started.md)
- [Arsitektur](docs/id/architecture.md)
- [Panggilan dan media](docs/id/calls-and-media.md)
- [AI: model, suara, dan agen](docs/id/ai.md)
- [Contact center: IVR, antrean, rekaman, CRM](docs/id/contact-centre.md)
- [Tools, diagnostik, dan sample](docs/id/tools-and-diagnostics.md)
- [Referensi konfigurasi](docs/id/reference.md)
- [Build dan packaging](docs/id/building.md)

## Kredit

Voip.NET dibuat oleh **Gravicode Studios**, dipimpin oleh **Kang Fadhil**. Berlisensi [MIT](LICENSE). Aplikasi sample memakai font IBM Plex (SIL OFL) dan Bricolage Grotesque (SIL OFL).
