# Rumble.Net

**SDK voice chat Mumble untuk .NET, dengan inti berbasis Rust.**

🇬🇧 [Read in English](README.md)

Rumble.Net menulis ulang *stack* klien Mumble dengan Rust agar cepat dan aman memori, lalu mengeksposnya ke .NET 10 melalui API berbasis async/event yang idiomatis. Gunakan untuk membangun aplikasi voice chat, bot, voice chat dalam game, alat perekam, maupun dasbor moderasi.

```csharp
await using var client = new RumbleClient(new RumbleClientOptions
{
    Host = "voice.example.org",
    Username = "RumbleBot",
    Audio = { Mode = AudioMode.Devices, TransmitMode = TransmitMode.PushToTalk },
});

client.UserJoined += (_, user) => Console.WriteLine($"{user.Name} bergabung");
client.ChannelMessageReceived += (_, msg) => Console.WriteLine($"{msg.Sender?.Name}: {msg.PlainText}");

await client.ConnectAsync();
await client.JoinChannelAsync("Games/Minecraft");
client.SendChannelMessage("Halo dari Rumble.Net!");

var teramai = client.Channels.MaxBy(c => c.TotalUserCount);   // LINQ atas model server yang live
```

## Tangkapan layar

**RumbleApp** adalah aplikasi voice chat Mumble yang dibangun dengan .NET MAUI Blazor Hybrid. Setiap baris pengguna menampilkan jejak suara berwarna amber secara live, dan strip transmit ikut menyala amber saat Anda sedang bicara.

![Layar suara RumbleApp terhubung ke server demo](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/RumbleNet/docs/images/rumbleapp-voice.png)

| Server | Pengaturan | Mobile: channel | Mobile: chat |
|---|---|---|---|
| ![Server](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/RumbleNet/docs/images/rumbleapp-servers.png) | ![Pengaturan](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/RumbleNet/docs/images/rumbleapp-settings.png) | ![Channel mobile](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/RumbleNet/docs/images/rumbleapp-mobile-channels.png) | ![Chat mobile](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/RumbleNet/docs/images/rumbleapp-mobile-chat.png) |

**RumbleGallery** adalah galeri contoh SDK berbasis Avalonia yang bisa dijalankan langsung. Setiap contoh menampilkan kode sumbernya apa adanya beserta output live-nya.

![RumbleGallery menjalankan contoh suara](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_Communication/main/RumbleNet/docs/images/rumblegallery.png)

## Fitur

| Area | Yang Anda dapatkan |
|---|---|
| **Suara & audio** | Opus (default) 48 kHz dengan *packet loss concealment* dan FEC. *Jitter buffer* adaptif yang diisi melalui antrean SPSC tanpa kunci. Audio posisional dengan *stereo panning* dan atenuasi jarak yang tervektorisasi. Perangkat audio melalui WASAPI, ALSA/PulseAudio dan CoreAudio. Mode *headless* (PCM) untuk bot. |
| **Keamanan & jaringan** | TLS 1.2/1.3 melalui rustls, tanpa OpenSSL. UDP terenkripsi OCB2-AES128 yang kompatibel byte-per-byte dengan Mumble, termasuk mitigasi serangan XEX\*. Fallback otomatis ke *tunnel* TCP. Identitas sertifikat dan *pinning* fingerprint. Heartbeat serta auto-reconnect dengan backoff. |
| **Protokol & kontrol** | Protokol kontrol lengkap: pohon channel, pengguna, pesan teks (channel, pohon, privat), mute/deafen/pindah/kick/ban, ACL dan daftar ban, voice target (whisper/shout), *listening channel*, statistik pengguna, data plugin, dan *ping* info server. |
| **Performa** | Runtime tokio, framing `Bytes` *zero-copy*, jalur audio tanpa alokasi, P/Invoke *blittable* (`LibraryImport`), JSON *source-generated*, helper audio SIMD. |
| **Pengalaman developer .NET** | Operasi `async/await` yang menunggu konfirmasi server, event bertipe, stream event `IAsyncEnumerable`, integrasi `Microsoft.Extensions.Logging` dan DI, ramah AOT/trimming, paket NuGet dengan *native asset* per RID. |
| **Ekstensibilitas** | Plugin host, filter audio capture, perekam suara, `Rumble.Net.Bots` (router perintah, *hosted service*, chat AI via `Microsoft.Extensions.AI`), serta bridge Unity dan Unreal di atas C ABI yang stabil. |
| **Tooling** | **Mock server Mumble** tertanam untuk pengujian dan demo, benchmark Criterion dan BenchmarkDotNet, probe latensi *end-to-end*, CI untuk Windows, Linux dan macOS. |

## Struktur repositori

```
native/                   Workspace Rust (inti)
  crates/rumble-protocol    protokol wire, varint, kripto OCB2-AES (tanpa I/O)
  crates/rumble-audio       Opus, jitter buffer, mixer, audio posisional, DSP, perangkat
  crates/rumble-client      klien tokio/rustls: sesi, reconnect, state, event
  crates/rumble-ffi         C ABI → rumble_native.{dll,so,dylib}  (include/rumble.h)
  crates/rumble-mock-server server Mumble in-process untuk tes dan contoh
src/Rumble.Net            SDK .NET 10
src/Rumble.Net.Bots       framework bot dan chat AI
tests/Rumble.Net.Tests    tes xUnit (unit dan end-to-end)
benchmarks/               BenchmarkDotNet dan probe latensi
samples/RumbleApp         aplikasi voice chat .NET MAUI Blazor Hybrid
samples/RumbleGallery     galeri contoh SDK yang bisa dijalankan (Avalonia)
bridges/unity, unreal     integrasi game engine
docs/                     dokumentasi lengkap
```

## Memulai

**Prasyarat:** .NET SDK 10.0.400+ dan Rust 1.85+ (`rustup`). Workload MAUI hanya diperlukan untuk RumbleApp.

```powershell
# Build dan tes semuanya (build .NET otomatis mengompilasi inti Rust)
cd native; cargo test --workspace; cd ..
dotnet test --project tests/Rumble.Net.Tests/Rumble.Net.Tests.csproj

# Jelajahi SDK lewat galeri (berjalan dengan mock server bawaan)
dotnet run --project samples/RumbleGallery

# Jalankan aplikasi voice chat (Windows)
dotnet build samples/RumbleApp -f net10.0-windows10.0.19041.0
```

Coba tanpa server dengan menjalankan server tertanam:

```csharp
using var server = MockMumbleServer.Start();                        // TLS + UDP di port bebas
await using var client = new RumbleClient(server.CreateClientOptions("penguji"));
await client.ConnectAsync();                                        // EchoBot menunggu di Lobby
```

## Performa terukur

Hasil berikut diukur dalam mode Release di mesin pengembang Windows x64. Ulangi dengan `dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- native|latency`.

| Pengukuran | Hasil |
|---|---|
| Latensi suara *end-to-end*: capture → encode → enkripsi → UDP → relay → jitter buffer → decode → mix (localhost) | **p50 38,4 ms · p95 42,7 ms** (target < 50 ms) |
| Enkripsi + dekripsi OCB2-AES128, paket 96 B | 0,71 µs (≈1,4 juta paket/detik) |
| Decode Opus, frame 10 ms | 28 µs |
| Encode Opus, frame 10 ms pada 48 kbit/s | 234 µs |
| Mixer 8 pembicara (jitter, decode, spasial, mix) per 10 ms | 262 µs (2,6 % dari anggaran frame) |

Detail ada di [docs/testing-and-benchmarks.md](docs/testing-and-benchmarks.md).

## Dokumentasi

Mulai dari **[docs/README.md](docs/README.md)**. Dokumentasi teknis ditulis dalam bahasa Inggris. Pelacakan proyek ada di [PLAN.md](PLAN.md) (roadmap) dan [Progress.md](Progress.md) (status).

## Status

[![NuGet](https://img.shields.io/nuget/vpre/Rumble.Net)](https://www.nuget.org/packages/Rumble.Net) `dotnet add package Rumble.Net --prerelease`

**0.1.0-preview.1** di NuGet sudah menyertakan inti native untuk **win-x64**. Untuk platform lain, build dari source (`build/build-native.*`) sampai preview berikutnya menyertakan binary hasil CI.

Versi 0.1.0 sudah lengkap untuk protokol klien dan terverifikasi *end-to-end* terhadap mock server bawaan di Windows x64. Validasi terhadap server Murmur produksi, build native untuk mobile, dan bridge game engine masih dalam proses. Semua dicatat secara jujur di Progress.md.

## Lisensi

MIT. Lihat [LICENSE](LICENSE) dan [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

Dibuat oleh **Gravicode Studios**, dipimpin oleh **Kang Fadhil**.
