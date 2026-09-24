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

Codec pass-through (G.729, SILK, Speex) dipertukarkan sebagai payload ter-encode:

```csharp
call.EncodedReceived += (c, payloadType, timestamp, marker, payload) => decoder.Feed(payload);
call.SendEncoded(96, rtpTimestamp, marker: true, h264Nal);
```

## Video

Dengan `Video = true`, panggilan membawa stream `m=video` di samping audio, pada port RTP sendiri dan
dengan enkripsi serta ICE yang sama seperti audio. Engine memaket dan merakit frame utuh (H.264
FU-A/STAP-A sesuai RFC 6184, VP8 sesuai RFC 7741); encode dan decode dilakukan aplikasi, jadi kirimkan
access unit H.264 dalam bentuk Annex B atau frame VP8:

```csharp
var options = new VoipClientOptions { Video = true, VideoCodecs = ["H264"] };

call.VideoFrameReceived += (c, timestamp, keyframe, frame) => decoder.Feed(frame, keyframe);
call.SendVideoFrame(rtpTimestamp90kHz, encodedFrame);   // dipecah menjadi beberapa paket bila perlu
Console.WriteLine(call.VideoCodec);                     // "H264", atau null pada panggilan audio saja
```

Panggilan bisa membawa stream video kedua berisi tampilan layar (RFC 4796 `a=content:slides`), ditawarkan
lewat re-INVITE dan dihentikan dengan cara yang sama:

```csharp
call.ShareScreen();                                        // menawarkan m=video kedua, bertanda slides
call.SendVideoFrame(rtpTimestamp90kHz, encodedFrame, "slides");
Console.WriteLine(string.Join(", ", call.VideoStreams));   // "main, slides"
call.StopScreenShare();                                    // m-line tetap ada, ditawarkan dengan port 0
```

Frame yang diterima menyebutkan asal streamnya, sehingga kamera dan layar bisa ditampilkan terpisah:

```csharp
call.VideoFrameReceived += (c, timestamp, keyframe, frame, content) =>
{
    if (content == "slides") { screenDecoder.Feed(frame, keyframe); } else { cameraDecoder.Feed(frame, keyframe); }
};
```

Frame yang kehilangan paket dibuang, bukan diserahkan dalam keadaan rusak, sehingga decoder tidak
pernah menerima frame cacat; engine lalu meminta keyframe ke pengirim (RTCP PLI, RFC 4585) agar gambar
kembali. Minta sendiri dengan `call.RequestKeyframe()`, dan tanggapi permintaan lawan dengan meng-encode
satu keyframe:

```csharp
client.MediaNotification += (_, e) =>
{
    if (e.Kind == "keyframe-request")
    {
        encoder.ForceKeyframe();
    }
};
```

Estimasi bandwidth dan penangkapan kamera belum ada — lihat
[PLAN 1.3](../../PLAN.md#13---video--fitur-video).

## Session timer dan provisional reliable

Panggilan memakai session timer (RFC 4028) secara bawaan: `Session-Expires: 1800`, diperbarui lewat
re-INVITE di tengah interval. Panggilan yang tidak diperbarui dalam satu interval akan ditutup,
sehingga dialog tidak hidup lebih lama daripada lawan yang sudah mati di balik proxy. Setel
`SessionExpires = 0` bila header itu tidak diinginkan; `MinSessionExpires` adalah interval terpendek
yang diterima, dan tawaran yang lebih pendek dijawab 422 beserta nilai minimumnya.

Respons provisional dikirim reliable (RFC 3262) ke pemanggil yang mengiklankan `100rel`: respons 180
membawa `RSeq` dan dikirim ulang sampai pemanggil mengirim PRACK. Pemanggil yang menaruh `100rel` di
`Require` selalu dilayani reliable; setel `ReliableProvisional = false` untuk melayani sisanya secara
biasa.

## Codec

| Codec | Payload | Rate | Implementasi |
| --- | --- | --- | --- |
| Opus | 111 | 48 kHz (`opus/48000/2`, suara mono) | native (libopus): 32 kbit/s, FEC in-band, pemulihan paket hilang dari paket berikutnya |
| G.722 | 9 | audio 16 kHz (clock RTP 8 kHz) | native, SB-ADPCM fixed-point |
| PCMU / PCMA | 0 / 8 | 8 kHz | native, tabel lookup |
| L16 | 97 | 16 kHz | native |
| telephone-event | 101, 110 | 8 kHz, 48 kHz | RFC 4733, pada clock rate codec audio |
| G.729, SILK, Speex | 18, 112, 113 | — | dinegosiasikan, pass-through |
| H.264, VP8 | 96, 98 | 90 kHz | stream video terpaket (lihat bagian Video); frame di-encode oleh aplikasi |
| VP9 | 100 | 90 kHz | dinegosiasikan, pass-through |

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

Panggilan video direkam ke AVI: video disimpan persis seperti yang dikirim lawan, berdampingan dengan
audio PCM — tidak ada proses encode ulang, sehingga perekaman nyaris tidak memakai CPU:

```csharp
using var recorder = CallRecorder.Start(call, "call.avi", RecordingFormat.Avi);
```

Laju frame diukur dari panggilan dan ditulis ke header saat file ditutup. Panggilan tanpa video
otomatis direkam sebagai WAV. MP4 dengan audio AAC memerlukan encoder AAC yang belum ada di SDK ini
(lihat [PLAN 1.3](../../PLAN.md#13---video--fitur-video)).

## Keamanan

```csharp
new VoipClientOptions
{
    Transport = SipTransport.Tls,          // SIP melalui TLS 1.2/1.3 (rustls), port 5061
    TlsPinnedFingerprints = ["3F:A2:…"],   // opsional: hanya terima sertifikat ini
    Srtp = SrtpMode.Mandatory,             // kunci SDES kini dikirim di dalam TLS
};
call.GetStatistics().SecureRtp;   // true bila kedua arah terlindungi
```

Sertifikat yang diperbarui dimuat dengan `client.ReloadTls()`, yang hanya berlaku untuk koneksi baru, jadi pembaruan tidak pernah memutus panggilan. Setel `TlsRequireClientCertificate` agar penelepon juga membuktikan identitasnya (TLS dua arah): aturan sertifikat dan pinning yang sama berlaku untuk kedua arah. Sertifikat server diperiksa terhadap root store Mozilla ditambah `TlsCaFile`, termasuk nama host dari URI tujuan, registrar, atau proxy. Pinning menggantikan validasi rantai, cocok untuk PBX dengan sertifikat self-signed. Setiap endpoint menyajikan `TlsCertificateFile`/`TlsPrivateKeyFile`, atau sertifikat self-signed yang dibuat otomatis dengan fingerprint `client.TlsFingerprint`. Handshake yang gagal langsung mengakhiri request dengan `503`.

## Browser WebRTC

Browser dapat menelepon `VoipClient` secara langsung: SIP over WebSocket (RFC 7118) untuk signaling, ICE untuk jalur media, dan DTLS-SRTP untuk kunci.

```csharp
var gateway = new VoipClient(new VoipClientOptions
{
    Transport = SipTransport.Ws,           // atau Wss dengan sertifikat TLS
    SipPort = 5090,
    Srtp = SrtpMode.Mandatory,
    SrtpKeying = SrtpKeying.Dtls,          // a=fingerprint + a=setup, UDP/TLS/RTP/SAVP
    Ice = true,
});
gateway.MediaNotification += (_, e) => Console.WriteLine($"{e.Kind} {e.Detail}"); // ice-connected, dtls-connected AES_CM_128_HMAC_SHA1_80
```

Engine menerima offer `UDP/TLS/RTP/SAVPF`, menjawab dengan kandidat ICE, `a=mid`, dan BUNDLE, memverifikasi sertifikat peer terhadap fingerprint dari signaling, dan tidak mengirim media sebelum kunci DTLS siap. SRTP berjalan dengan AES-CM-128-HMAC-SHA1-80 atau AEAD-AES-128/256-GCM, sesuai hasil handshake. Offer masuk diterima baik dengan kunci SDES maupun DTLS apa pun nilai `SrtpKeying`; pengaturan itu menentukan apa yang ditawarkan client ini. Sisi browser bisa berupa client SIP over WebSocket apa saja; `samples/VoipNet.WebPhone` menyertakan versi kecil dan menjembatani browser ke telepon SIP biasa.

## NAT dan konektivitas

| Pengaturan | Gunakan bila |
| --- | --- |
| pembelajaran `rport` otomatis | di balik NAT biasa dengan registrar (aktif secara default) |
| `PublicAddress` | NAT statis 1:1 |
| `StunServer` | media harus mengiklankan alamat publik |
| `TurnServer`, `TurnUsername`, `TurnPassword` | NAT simetris atau firewall ketat |
| `Ice = true` | berkomunikasi dengan peer yang mendukung ICE, atau ada beberapa jalur jaringan (lihat ICE di bawah) |
| `KeepaliveSecs` | menjaga lubang NAT UDP tetap terbuka di antara registrasi |

### ICE

Dengan `Ice = true` (selalu aktif untuk DTLS-SRTP dan setiap kali peer menawarkan ICE), setiap panggilan menjalankan agen ICE penuh (RFC 8445): memasangkan kandidat host, server-reflexive, dan relay lokal dengan milik peer, mengatur tempo connectivity check, menjawab check dengan triggered check, mempelajari kandidat peer-reflexive (menangani browser yang menyembunyikan alamat di balik nama mDNS), menyelesaikan konflik peran, dan menominasikan satu pasangan. Pihak yang mengirim offer menjadi agen controlling. Kandidat yang dikirim peer belakangan lewat SIP INFO (`application/trickle-ice-sdpfrag`, RFC 8840) ikut diperiksa, dan kandidat kita dikirim dengan cara yang sama: panggilan ICE mengirim probe STUN tanpa menunggu jawabannya, sehingga server STUN yang lambat atau tidak terjangkau tidak pernah menunda panggilan, dan kandidat reflexive dikirim ke peer begitu tiba. (Panggilan tanpa ICE tetap menunggu: di sana alamat reflexive itulah yang diumumkan di SDP.) Setelah pasangan terpilih, consent diperbarui setiap beberapa detik (RFC 7675).

```csharp
client.MediaNotification += (_, e) => Console.WriteLine($"{e.Kind}: {e.Detail}");
// ice-candidates: host 192.168.1.20:50901     (dikirim peer secara trickle)
// ice-connected: host → prflx 192.168.1.20:50901
// ice-disconnected: consent expired            ice-failed: no candidate pair worked

call.RestartIce();   // setelah jaringan berubah: re-INVITE dengan kredensial baru; media tetap jalan sampai pasangan baru terpilih
```

## Statistik kualitas

`GetStatistics()` mengembalikan jumlah paket, byte, paket hilang, paket terlambat, jitter, kedalaman jitter buffer, sample rate, estimasi MOS model-E, status SRTP dan ICE, serta antrean keluar. Setelah panggilan berakhir, `FinalStatistics` menyimpan nilai terakhir.

Engine juga bertukar sender dan receiver report RTCP (RFC 3550), sehingga statistik yang sama memberi tahu apa yang diterima **lawan bicara**: `RemoteLossPercent`, `RemoteJitterMs`, dan `RoundTripMs`. Report pertama dikirim satu detik setelah panggilan tersambung lalu berulang tiap beberapa detik, jadi nilainya masih nol di awal. Opus memakainya langsung: saat loss yang dilaporkan naik, bitrate diturunkan dan porsi koreksi kesalahan ditambah.

Bersama setiap report, engine juga mengirim metrik VoIP RTCP XR (RFC 3611) — loss dan discard rate, delay, R factor, MOS — dan membaca milik lawan, yang muncul sebagai `RemoteMos`: kualitas yang benar-benar didengar sisi sana.
