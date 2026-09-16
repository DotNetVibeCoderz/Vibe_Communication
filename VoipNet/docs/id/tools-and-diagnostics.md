# Tools, diagnostik, dan sample

🇬🇧 [English](../en/tools-and-diagnostics.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

## CLI `voipnet`

```bash
dotnet tool install -g VoipNet.Cli
voipnet --help
```

| Perintah | Kegunaan |
| --- | --- |
| `voipnet version` | versi SDK, engine, dan runtime |
| `voipnet sip ping <uri> [--count 4]` | waktu tempuh OPTIONS dan User-Agent penjawab |
| `voipnet sip register -d pbx -u 1001 -p rahasia` | menguji kredensial ke registrar |
| `voipnet sip call <uri> [--duration 10] [--tone 440] [--dtmf 123] [--record out.wav] [--register] [--srtp]` | panggilan uji dengan pemantauan MOS, loss, jitter secara langsung |
| `voipnet sip listen [--sip-port 5060] [--echo]` | menjawab panggilan; `--echo` menjadikannya layanan uji gema |
| `voipnet sip message <uri> "teks"` | mengirim SIP MESSAGE |
| `voipnet rtp analyze capture.pcap [--json]` | loss, jitter, urutan, dan MOS setiap stream RTP dalam capture |
| `voipnet rtp listen --port 40000 [--seconds 30]` | menerima RTP pada port dan menganalisisnya secara langsung |

Opsi umum untuk perintah `sip`: `--domain`, `--user`, `--password`, `--proxy`, `--transport udp|tcp`, `--bind`, `--port`, `--trace` (cetak SIP), `--pcap file` (tulis SIP ke capture).

Contoh — uji gema di dua terminal:

```bash
voipnet sip listen --sip-port 5090 --echo
voipnet sip call sip:echo@127.0.0.1:5090 --duration 5 --dtmf 123 --pcap call.pcap
```

```
Connected codec G722 @ 16000 Hz
╭─────────────────────────┬─────────────────╮
│ MOS (estimated)         │            4.38 │
│ Packets sent / received │       194 / 174 │
│ Lost / late             │    0 / 0 (0.0%) │
│ Jitter                  │          0.2 ms │
╰─────────────────────────┴─────────────────╯
```

## Diagnostik di kode

### Capture SIP

```csharp
var client = new VoipClient(new VoipClientOptions { TraceSip = true, … });
using var capture = new PcapWriter("sip.pcap");
capture.Attach(client);         // buka di Wireshark: SIP ter-decode
client.SipTrace += (_, e) => logger.LogDebug("{Dir} {Remote}\n{Msg}", e.Outgoing ? "→" : "←", e.RemoteEndPoint, e.Message);
```

### Analisis RTP

```csharp
foreach (var s in RtpStreamAnalyzer.AnalyzeFile("trunk.pcap"))
    Console.WriteLine($"0x{s.Ssrc:X8} {s.Codec} {s.Source}→{s.Destination} loss {s.LossPercent:0.0}% jitter {s.JitterMs:0.0} ms MOS {s.Mos:0.00}");

var live = new RtpStreamAnalyzer();
live.Add(new UdpDatagram(DateTimeOffset.UtcNow, remote, local, packet));
```

File libpcap dengan link type Ethernet, raw IP, Linux cooked, atau loopback didukung (konversi pcapng dengan `editcap -F pcap`).

### Metrik

Meter `VoipNet`:

| Instrumen | Jenis |
| --- | --- |
| `voipnet.calls.started` (tag `direction`) | counter |
| `voipnet.calls.answered` | counter |
| `voipnet.calls.failed` (tag `code`) | counter |
| `voipnet.calls.active` | up-down counter |
| `voipnet.call.duration` (s) | histogram |
| `voipnet.call.mos` | histogram |
| `voipnet.call.packet_loss` (%) | histogram |
| `voipnet.registrations` (tag `state`) | counter |

```bash
dotnet-counters monitor --counters VoipNet -n VoipNet.CallCenter
```

Atau ekspor dengan OpenTelemetry: `builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("VoipNet"))`.

### Logging

Berikan `ILogger<VoipClient>`; event log engine (deteksi NAT, kegagalan kirim, error media) diteruskan dengan level yang sesuai.

## Sample

### Softphone (Avalonia)

![Softphone](../images/softphone-idle.png)

- Mode demo menjalankan dua jalur dalam proses: **echo** (mendengar suara sendiri) dan **music** (melodi yang menjawab tombol keypad dengan nada).
- Trace audio langsung untuk kedua arah, codec dan MOS, mute/hold/keypad/rekam/transfer.
- Panel catatan dengan **Summarise with AI** (endpoint kompatibel OpenAI apa pun).
- `dotnet run --project samples/VoipNet.Softphone -- --screenshot docs/images` merender screenshot dokumentasi tanpa tampilan.

### Gallery (Avalonia)

![Gallery](../images/gallery-call-center.png)

Tiga belas halaman langsung: overview, melakukan panggilan, codec, DTMF, hold dan transfer, konferensi, perekaman, SRTP, model bahasa (dengan tool call nyata), voice agent, builder IVR, antrean dan agen, diagnostik. Setiap halaman menampilkan kode C# yang melakukan hal yang sama. Atur model dengan `VOIPNET_AI_ENDPOINT`, `VOIPNET_AI_KEY`, `VOIPNET_AI_MODEL`.

### Call Centre (Blazor Server)

Sebuah PBX, lima softphone agen, dan generator trafik Poisson — semuanya SIP/RTP nyata di loopback. Wallboard dengan service level, penelepon yang menunggu digambar terhadap targetnya, papan agen, kualitas panggilan langsung, event, kontrol trafik, dan **supervisor AI**; halaman Recordings dengan pemutaran di browser.

### IVR Studio (Blazor Server)

Sunting menu, opsi, dan instruksi AI; lihat jalur panggilan; tekan tombol pada telepon di browser yang benar-benar memanggil alur; ketik sebagai penelepon setelah IVR menyerahkan ke agen AI. Alur disimpan ke `App_Data/flow.json` dan diekspor di `/flow.json`.

### Realtime Agent (console)

```bash
dotnet run --project samples/VoipNet.RealtimeAgent             # menjawab panggilan SIP di :5070
dotnet run --project samples/VoipNet.RealtimeAgent -- --demo   # penelepon skrip + model nyata
```

Atur provider di `appsettings.json` (`AI:Chat`, `AI:SpeechToText`, `AI:TextToSpeech`, `AI:Realtime`) dan pilih `Agent:Mode` `pipeline` atau `realtime`.

### Screenshot dokumentasi

`tools/VoipNet.DocShots` mengendalikan Edge/Chrome headless melalui DevTools protocol untuk menangkap sample Blazor:

```bash
dotnet run --project tools/VoipNet.DocShots -- ivrstudio http://127.0.0.1:5190 docs/images
dotnet run --project tools/VoipNet.DocShots -- callcenter http://127.0.0.1:5184 docs/images
```
