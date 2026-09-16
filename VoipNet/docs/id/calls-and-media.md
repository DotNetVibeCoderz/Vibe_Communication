# Panggilan dan media

🇬🇧 [English](../en/calls-and-media.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

## Siklus hidup panggilan

| State | Arti |
| --- | --- |
| `Calling` | INVITE terkirim, belum ada respons |
| `Ringing` / `EarlyMedia` | 180, atau 183 dengan SDP (early media diputar) |
| `Incoming` | panggilan menunggu `AnswerAsync` atau `Reject` |
| `Connected` | media mengalir dua arah |
| `OnHold` / `RemoteHold` | sisi ini atau sisi lawan melakukan hold |
| `Terminated` | selesai; `Completion` selesai, `FinalStatistics` terisi |

```csharp
var call = client.Call("sip:1002@pbx");               // langsung kembali
call.StateChanged += (_, e) => Console.WriteLine($"{e.State} {e.StatusCode} {e.Reason}");
try { await call.Connected; }                          // melempar VoipException pada 486, 603, 408…
catch (VoipException ex) { Console.WriteLine(ex.ErrorCode); }
var ended = await call.Completion;
```

Panggilan masuk:

```csharp
client.IncomingCall += async (_, e) =>
{
    if (e.From.Contains("spam")) { e.Call.Reject(603); return; }
    await e.Call.AnswerAsync();
};
```

## Audio masuk dan keluar

```csharp
// Zero-copy, di thread media (jaga agar cepat)
call.AudioReceived += (c, direction, rate, samples) => meter.Push(samples);

// Stream async, frame dibuang bila konsumen tertinggal jauh
await foreach (var segment in call.ReadAudioAsync(AudioDirection.Inbound, ct)) { … }

// Kirim pada rate apa pun; engine mengatur temponya
int queuedMs = call.SendAudio(pcm, sampleRate: 24000);
await call.SendAudioStreamAsync(ttsChunks, 24000, maxQueuedMs: 2000);
call.ClearAudio();                                     // barge-in
```

Codec pass-through (video, G.729, Opus) dipertukarkan sebagai payload ter-encode:

```csharp
call.EncodedReceived += (c, payloadType, timestamp, marker, payload) => decoder.Feed(payload);
call.SendEncoded(96, rtpTimestamp, marker: true, h264Nal);
```

## Codec

| Codec | Payload | Rate | Implementasi |
| --- | --- | --- | --- |
| G.722 | 9 | audio 16 kHz (clock RTP 8 kHz) | native, SB-ADPCM fixed-point |
| PCMU / PCMA | 0 / 8 | 8 kHz | native, tabel lookup |
| L16 | 97 | 16 kHz | native |
| telephone-event | 101 | — | RFC 4733 |
| G.729, Opus, SILK, Speex | 18, 111–113 | — | dinegosiasikan, pass-through |
| H.264, VP8, VP9 | 96, 98, 100 | 90 kHz | dinegosiasikan, pass-through |

Urutkan `AudioCodecs` sesuai preferensi. Jawaban mengikuti urutan pihak penawar (RFC 3264).

## DTMF

```csharp
var options = new VoipClientOptions { DtmfMode = DtmfMode.Rfc4733, DetectInbandDtmf = true };
call.SendDtmf("1234#", durationMs: 120);
client.DtmfReceived += (_, e) => Console.WriteLine($"{e.Digit} via {e.Source}");
```

`Rfc4733` otomatis beralih ke nada in-band bila lawan tidak menegosiasikan telephone-event.

## Hold, mute, transfer

```csharp
call.SetHold(true);          // re-INVITE a=sendonly; lawan melihat RemoteHold
call.SetMute(true);          // audio yang antre dikirim sebagai hening; stream tetap berjalan
call.Transfer("sip:2002@pbx");               // blind: REFER
consult = await client.CallAsync("sip:2002@pbx");
call.TransferTo(consult);                    // attended: REFER dengan Replaces
client.TransferProgress += (_, e) => …;      // progres NOTIFY sipfrag
```

Saat endpoint ini menerima REFER dan `AcceptTransfers` bernilai true, endpoint membuat panggilan baru sendiri, melaporkan progres lewat NOTIFY, dan memicu `TransferRequested`.

## Konferensi

```csharp
using var conference = client.CreateConference();
conference.Add(call1);
conference.Add(call2);       // setiap peserta mendengar peserta lain
conference.Add(supervisor); supervisor.SetHold(true);   // hanya mendengar: jembatan mengirim, audionya diabaikan
```

Pencampuran berlangsung pada 16 kHz di dalam engine, per peserta (mix-minus), dengan saturasi.

## Perekaman

```csharp
using var recorder = CallRecorder.Start(call, "call.mp3", RecordingFormat.Mp3, RecordingLayout.Stereo);
```

Stereo menaruh pihak lawan di kanal kiri dan endpoint ini di kanal kanan. Encoding MP3 memakai encoder LAME bawaan yang hanya tersedia di Windows; di Linux dan macOS recorder menulis WAV dan melaporkan path barunya di `recorder.Path`.

## Keamanan

```csharp
new VoipClientOptions { Srtp = SrtpMode.Mandatory, Transport = SipTransport.Tcp };
call.GetStatistics().SecureRtp;   // true bila kedua arah terlindungi
```

## NAT dan konektivitas

| Pengaturan | Gunakan bila |
| --- | --- |
| pembelajaran `rport` otomatis | di balik NAT biasa dengan registrar (aktif secara default) |
| `PublicAddress` | NAT statis 1:1 |
| `StunServer` | media harus mengiklankan alamat publik |
| `TurnServer`, `TurnUsername`, `TurnPassword` | NAT simetris atau firewall ketat |
| `Ice = true` | berkomunikasi dengan peer yang mendukung ICE |
| `KeepaliveSecs` | menjaga lubang NAT UDP tetap terbuka di antara registrasi |

## Statistik kualitas

`GetStatistics()` mengembalikan jumlah paket, byte, paket hilang, paket terlambat, jitter, kedalaman jitter buffer, sample rate, estimasi MOS model-E, status SRTP dan ICE, serta antrean keluar. Setelah panggilan berakhir, `FinalStatistics` menyimpan nilai terakhir.
