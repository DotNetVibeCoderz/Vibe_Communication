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

### Beberapa encoding untuk satu gambar

Browser bisa mengirim kamera yang sama dua atau tiga kali sekaligus dengan ukuran berbeda, lalu
membiarkan sisi lain memilih (simulcast, RFC 8853). Ketika sebuah offer memintanya, answer menerima
semuanya dan mempertahankan header extension yang menyebut encoding tiap paket (RFC 8852):

```text
a=extmap:4 urn:ietf:params:rtp-hdrext:sdes:mid
a=extmap:10 urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id
a=rid:h recv
a=rid:m recv
a=rid:l recv
a=simulcast:recv h;m;l
```

Setiap encoding dirakit ulang sendiri-sendiri — nomor urut sendiri, frame sendiri — dan diukur, tetapi
hanya satu yang diteruskan ke aplikasi atau ke konferensi:

```csharp
foreach (var layer in call.VideoLayers)
{
    Console.WriteLine($"{layer.Name}: {layer.BitsPerSecond / 1000} kbit/s{(layer.Selected ? " ← diteruskan" : "")}");
}

client.MediaNotification += (_, e) =>
{
    if (e.Kind == "video-layer")
    {
        Console.WriteLine($"sekarang meneruskan {e.Detail}");
    }
};
```

Pilihannya mengikuti bitrate yang dilaporkan penerima (REMB): encoding terbesar yang masih menyisakan
sepersepuluh anggaran, atau yang terkecil bila tidak ada yang muat, karena gambar seadanya lebih baik
daripada tidak ada. Di konferensi, anggaran penonton terkecil yang menentukan, sebab semua orang
dikirimi frame yang sama, dan setiap pergantian meminta keyframe ke pengirim — decoder tidak bisa
mulai di tengah gambar. Encoding yang berhenti datang kehilangan pilihannya dalam dua detik.

Mengirim simulcast belum ada: engine memilih di antara encoding yang dikirim lawan bicara, bukan
membuatnya sendiri.

### Sinkronisasi bibir

Audio dan video berjalan sebagai stream terpisah dengan clock yang tidak berhubungan, jadi timestamp
sebuah frame tidak menyatakan apa pun tentang audio mana yang menyertainya. Sender report RTCP membawa
kedua clock pada instan yang sama (RFC 3550 §6.4.1), dan SDK mengubah timestamp apa pun menjadi waktu
bersama itu:

```csharp
call.VideoFrameReceived += (c, timestamp, keyframe, frame, content) =>
{
    var sentAt = c.PresentationTime(MediaStream.Video, timestamp);   // saat pengirim mengambilnya
    var hearing = c.AudioPlayoutTime;                                // audio yang sedang diputar
    if (sentAt is null || hearing is null)
    {
        decoder.Feed(frame, keyframe);                               // belum ada report: tampilkan
        return;
    }

    var ahead = sentAt.Value - hearing.Value;
    if (ahead > TimeSpan.Zero)
    {
        queue.HoldFor(ahead, frame);                                 // suaranya belum sampai
    }
    else
    {
        decoder.Feed(frame, keyframe);
    }
};
```

Keduanya `null` sampai lawan bicara mengirim report untuk stream itu, beberapa detik setelah panggilan
dimulai. Engine yang memutar audionya sendiri, jadi `AudioPlayoutTime` adalah momen yang sedang
terdengar di speaker; menahan frame sampai audionya menyusul — itulah sinkronisasi bibir.

### Berapa banyak yang boleh dikirim

Penerima video mengukur berapa yang sanggup diterimanya lalu memberi tahu pengirim lewat RTCP (REMB);
begitulah browser menentukan bitrate encodingnya. Engine ini melakukan keduanya: mengirim perkiraan
untuk setiap stream video yang diterimanya, dan melaporkan perkiraan dari lawan bicara supaya aplikasi
yang meng-encode bisa mengikutinya.

```csharp
client.MediaNotification += (_, e) =>
{
    if (e.Kind == "bandwidth-estimate")
    {
        Console.WriteLine(e.Detail);                     // "450 kbit/s", saat berubah sepersepuluh
    }
};

var allowed = call.GetStatistics().RemoteEstimateBps;    // 0 sampai lawan bicara memberitahu
encoder.SetBitrate((int)(allowed * 0.9));                // sisakan ruang untuk audio dan overhead
```

Perkiraan dimulai dari 600 kbit/s, naik 8 % selama frame tiba utuh, bertahan saat loss kecil, dan
dipotong sebanding dengan loss yang berat — tidak pernah lebih dari satu setengah kali yang benar-benar
tiba, dan tidak pernah di bawah 64 kbit/s. `a=rtcp-fb:<pt> goog-remb` ditawarkan di baris video dan
dipertahankan pada answer yang menawarkannya, bersama `nack`, `nack pli`, dan `ccm fir`; lawan bicara
yang tidak menegosiasikannya tidak pernah dikirimi umpan balik yang tidak dimintanya.

Penangkapan kamera dan encoding tetap urusan aplikasi — lihat
[PLAN 1.3](../../PLAN.md#13---video--fitur-video).

## Kanal data

Setel `DataChannels = true` dan panggilan menawarkan stream `m=application` di samping audio: SCTP di
dalam terowongan DTLS-nya sendiri (RFC 8831/8832), persis yang dipakai `RTCDataChannel` di browser.
Fitur ini butuh `SrtpKeying.Dtls`, karena terowongan DTLS itulah yang membawanya.

```csharp
var options = new VoipClientOptions
{
    DataChannels = true,
    Srtp = SrtpMode.Mandatory,
    SrtpKeying = SrtpKeying.Dtls,
};

call.OpenDataChannel("chat");                  // diantrikan sampai asosiasi siap, lalu dibuka
client.MediaNotification += (_, e) =>
{
    if (e.Kind == "data-channel-open")
    {
        Console.WriteLine(e.Detail);           // "0 chat" — nomor stream dan label
    }
};

call.DataMessageReceived += (c, stream, text, data) =>
{
    Console.WriteLine(text ? Encoding.UTF8.GetString(data) : $"{data.Length} byte");
};

var channel = call.DataChannels[0];            // nomor stream dan label, setelah terbuka
call.SendData(channel.Stream, "halo dunia");   // teks
call.SendData(channel.Stream, fileBytes);      // biner, dipecah dan disusun ulang untuk lawan bicara
```

Kanal bersifat reliable dan berurutan; pesan sampai 256 KB dipecah ke beberapa paket SCTP lalu disusun
kembali di sisi penerima. Sisi yang membuka DTLS memakai nomor stream genap dan sisi lain ganjil,
sehingga kedua ujung bisa membuka kanal tanpa menyepakati nomor lebih dulu. `samples/VoipNet.WebPhone`
mengirim satu baris teks dari browser ke gateway dan kembali lagi, berdampingan dengan audio dan video.

Reliabilitas parsial (`maxRetransmits`, `maxPacketLifeTime`) dan pengiriman tak berurutan belum ada:
setiap kanal reliable dan berurutan.

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

Video dalam konferensi diteruskan, bukan dicampur — mencampurnya berarti men-decode dan meng-encode
ulang setiap stream, dan codec untuk itu tidak ada di engine ini. Jadi setiap peserta melihat satu
peserta lain pada satu waktu:

```csharp
conference.FollowSpeaker();                    // bawaan: semua melihat yang sedang bicara
conference.Pin(conference.Participants[0]);    // atau semua melihat satu peserta tertentu
Console.WriteLine(conference.ActiveSpeaker?.RemoteUri);
```

Pembicara dipilih dari audio yang memang sudah dicampur bridge: peserta paling keras mengambil alih
layar, menahannya sejenak setelah berhenti bicara, dan hanya kalah oleh suara yang jelas lebih keras —
sehingga gambar tidak berkedip saat dua orang bicara bersamaan. Ketika sumbernya berganti, bridge
meminta keyframe ke pembicara baru dan menahan perpindahan sampai keyframe itu tiba, karena decoder
tidak bisa mulai di tengah gambar. Tampilan grid memerlukan satu stream per peserta dan belum
disediakan.

## Encode video

Sampai sekarang aplikasi harus menyiapkan frame terenkode sendiri. `VoipNet.Video` melakukan encode
dan decode H.264 memakai codec yang sudah ada di platform — Media Foundation di Windows — sehingga
kamera, layar, atau apa pun yang bisa menghasilkan piksel dapat dikirim ke panggilan:

```csharp
using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
{
    Width = 640, Height = 360, FramesPerSecond = 30, BitsPerSecond = 800_000,
});

var nv12 = new byte[VideoPicture.Nv12Length(640, 360)];
VideoPictures.FromBgra(pixels, 640, 360, nv12);

foreach (var frame in encoder.Encode(new VideoPicture(640, 360, nv12, elapsed)))
{
    call.SendVideoFrame((uint)(elapsed.TotalSeconds * 90000), frame.Data.Span);
}

call.KeyframeRequested += (_, _) => encoder.RequestKeyframe();
```

Encodernya disetel untuk panggilan, bukan untuk file: latensi rendah, bitrate konstan, tanpa B-frame,
sehingga gambar langsung dikirim begitu selesai dan tiba dalam urutan pengambilannya. `RequestKeyframe`
dipanggil ketika lawan bicara memintanya — itulah arti PLI — dan gambar berikutnya menjadi titik awal
yang bisa didekode sendiri.

Decode berjalan sebaliknya, dan ukuran gambar datang dari stream, bukan dari Anda:

```csharp
using var decoder = VideoCodecs.CreateH264Decoder();

call.VideoFrameReceived += (_, _, _, frame, _) =>
{
    foreach (var picture in decoder.Decode(frame, elapsed))
    {
        VideoPictures.ToBgra(picture.Data.Span, picture.Width, picture.Height, bgra);
    }
};
```

Keduanya mengembalikan daftar, karena codec bisa menahan satu gambar atau mengeluarkan beberapa
sekaligus. Gambar berformat NV12 — kecerahan resolusi penuh, warna setengah resolusi — yang diterima
semua codec perangkat keras tanpa konversi tambahan; `VideoPictures` mengubahnya ke dan dari BGRA yang
dipakai layar.

Encoder milik kartu grafis dipakai lebih dulu bila mau menerima gambar dari memori biasa; kebanyakan
hanya menyediakan diri sebagai transform asinkron atau meminta surface Direct3D, dan itu belum
disentuh — pekerjaannya diambil encoder software bawaan Windows. `encoder.Implementation` menyebut yang
mana yang menjawab, berguna dicatat ketika sebuah panggilan memakan CPU lebih dari perkiraan.

Baru Windows yang codecnya tersambung. `VideoCodecs.IsH264Available` memberi tahu apakah mesin ini
punya — ia bertanya ke platform, bukan menebak, karena Windows Server terpasang tanpa Media Foundation
— dan membuat encoder di platform lain melempar `PlatformNotSupportedException` alih-alih berpura-pura.
VideoToolbox, VA-API, dan jalur asinkron ke encoder kartu grafis ada di
[PLAN 1.3](../../PLAN.md#13---video--fitur-video).

### Kamera

Kamera dibaca dengan cara meminta gambar berikutnya, bukan menerima aliran gambar: perangkatnya yang
mengatur tempo, dan pemanggil yang tertinggal membaca gambar berikutnya, bukan antrean gambar basi.

```csharp
foreach (var camera in VideoCapture.Cameras())
{
    Console.WriteLine(camera.Name);
}

using var camera = VideoCapture.OpenCamera(width: 640, height: 360, framesPerSecond: 30);
using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
{
    Width = camera.Width, Height = camera.Height, FramesPerSecond = 30, BitsPerSecond = 800_000,
});

while (camera.Read() is { } picture)
{
    foreach (var frame in encoder.Encode(picture))
    {
        call.SendVideoFrame((uint)(picture.Timestamp.TotalSeconds * 90000), frame.Data.Span);
    }
}
```

Ukuran yang diminta hanyalah preferensi: perangkat yang tidak sanggup memberi yang bisa, dan
`camera.Width` serta `camera.Height` menyebut hasilnya — jadi bacalah keduanya sebelum menyiapkan
encoder, seperti di atas. Gambar selalu kembali dalam NV12 apa pun format asli kameranya, karena
readernya yang mengonversi dan menskalakan.

Layar dibaca dengan cara yang sama, untuk stream video kedua tempat berbagi layar dikirim:

```csharp
call.ShareScreen();
using var screen = VideoCapture.OpenScreen(wholeDesktop: false, width: 1280, height: 720, framesPerSecond: 10);
using var encoder = VideoCodecs.CreateH264Encoder(new VideoEncoderOptions
{
    Width = screen.Width, Height = screen.Height, FramesPerSecond = 10, BitsPerSecond = 1_500_000,
    Content = VideoContent.Detail,   // layar, bukan wajah
});

while (screen.Read() is { } picture)
{
    foreach (var frame in encoder.Encode(picture))
    {
        call.SendVideoFrame((uint)(picture.Timestamp.TotalSeconds * 90000), frame.Data.Span, "slides");
    }
}
```

`VideoContent.Detail` memberi tahu encoder bahwa ini bukan wajah: kualitasnya dipertahankan dan
lajunya dibiarkan turun ketika tidak ada yang bergerak, alih-alih menghabiskan bitrate tetap untuk
mengaburkan teks diam, serta jauh lebih jarang meminta keyframe. Kamera memakai default,
`VideoContent.Motion`.

Desktop tidak punya laju frame sendiri, jadi angka yang diberikan adalah seberapa sering layar disalin,
dan salinannya diskalakan sekalian — desktop 4K utuh lebih mahal untuk di-encode daripada bandwidth
siapa pun. Biaya satu salinan sepenuhnya bergantung pada driver layar: layar lokal biasa menjawab dalam
beberapa milidetik, sementara sebagian layar virtual dan remote butuh sepertiga detik berapa pun
ukurannya, jadi lajunya adalah batas atas, bukan janji. Desktop Duplication yang bebas dari masalah itu
ada di [PLAN 1.3](../../PLAN.md#13---video--fitur-video).

### Grid berisi semua orang

Engine meneruskan video satu peserta, bukan mencampurnya, karena meneruskan nyaris tanpa biaya dan
mempertahankan kualitas yang dipilih pengirim. Grid adalah pilihan sebaliknya: decode semua orang,
susun, lalu encode sekali. `VideoCompositor` mengerjakan bagian menyusunnya, di atas gambar NV12 yang
sama dengan yang dipakai codec:

```csharp
var compositor = new VideoCompositor(640, 360);
var composed = compositor.Compose(pictures, CompositorLayout.Grid, elapsed);

foreach (var frame in encoder.Encode(composed))
{
    call.SendVideoFrame((uint)(elapsed.TotalSeconds * 90000), frame.Data.Span);
}
```

`Grid` memberi petak yang sama besar, sepersegi mungkin sesuai jumlahnya. `PictureInPicture` memberi
gambar pertama seluruh bingkai dan menaruh yang kedua di pojok. `Spotlight` memberi gambar pertama
bingkainya dan menderetkan sisanya di bawah. Setiap gambar mempertahankan bentuknya di dalam petak —
ponsel yang dipegang tegak dipaskan dan ditengahkan, bukan dilebarkan — dan sisa ruangnya hitam.

Hasilnya meminjam buffer milik compositor, jadi encode atau salin dulu sebelum menyusun yang berikutnya.

## Perekaman

```csharp
using var recorder = CallRecorder.Start(call, "call.mp3", RecordingFormat.Mp3, RecordingLayout.Stereo);
```

Stereo menaruh pihak lawan di kanal kiri dan endpoint ini di kanal kanan. Encoding MP3 memakai encoder LAME bawaan yang hanya tersedia di Windows; di Linux dan macOS recorder menulis WAV dan melaporkan path barunya di `recorder.Path`.

Panggilan video direkam ke MP4 atau AVI: video disimpan persis seperti yang dikirim lawan,
berdampingan dengan audio PCM — tidak ada proses encode ulang, sehingga perekaman nyaris tidak memakai
CPU:

```csharp
using var recorder = CallRecorder.Start(call, "call.mp4", RecordingFormat.Mp4);
```

Untuk panggilan, MP4 lebih tepat karena setiap frame punya durasinya sendiri: timestamp RTP menjadi
durasi sample, jadi frame yang datang terlambat tetap terlambat, bukan dirata-rata menjadi satu laju
frame seperti di AVI. Perekaman dimulai dari frame pertama yang membawa sequence parameter set, sebab
sebelum itu tidak ada yang bisa didekode sendiri, dan parameter set-nya dipindahkan ke header file
sesuai aturan MP4.

Ada dua batasan yang perlu diketahui. Audionya PCM, bukan AAC — SDK ini belum punya encoder AAC —
sehingga ukurannya sebesar WAV panggilan yang sama; dan MP4 di sini hanya memuat H.264, jadi panggilan
VP8 ditulis ke AVI dan `recorder.Path` menyebutkannya. AVI menyimpan satu laju frame untuk seluruh
file, diukur dari panggilan dan ditulis ke header saat file ditutup. Panggilan tanpa video otomatis
direkam sebagai WAV.

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

### ZRTP

Dengan `SrtpKeying.Zrtp`, kedua ujung menyepakati kuncinya sendiri lewat jalur media (RFC 6189), dan
signaling tidak pernah membawa kunci apa pun. Panggilan dimulai tanpa enkripsi lalu berubah terenkripsi
begitu pertukaran selesai; kedua orang kemudian saling membacakan empat karakter, yang hanya cocok bila
tidak ada yang menyadap di tengah:

```csharp
var options = new VoipClientOptions { Srtp = SrtpMode.Optional, SrtpKeying = SrtpKeying.Zrtp };

client.MediaNotification += (_, e) =>
{
    if (e.Kind == "zrtp-connected")
    {
        Console.WriteLine($"Bacakan ini: {e.Detail}");        // "a7f3"
    }
};

Console.WriteLine(call.AuthenticationString);                 // string yang sama, null sebelum itu
```

Offer membawa `a=zrtp-hash:1.10 <hash>` supaya lawan bicara bisa membedakan pertukaran asli dari yang
disusupkan, dan offer yang membawanya menyalakan ZRTP di sisi ini meski konfigurasinya SDES.

Yang diimplementasikan adalah pertukaran Diffie-Hellman biasa — Hello, Commit, DHPart1, DHPart2,
Confirm1, Confirm2 — dengan EC25 (P-256), AES-128, HMAC-SHA256, dan SAS base32. Tidak ada secret
tersimpan antar panggilan, tidak ada mode multistream atau preshared, tanda tangan, maupun pendaftaran
PBX, jadi stringnya harus dibacakan setiap panggilan. Diuji antar endpoint Voip.NET; interoperabilitas
dengan implementasi ZRTP lain belum dicoba di sini.

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
