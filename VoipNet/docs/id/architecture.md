# Arsitektur

🇬🇧 [English](../en/architecture.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

```
┌──────────────────────────── .NET 10 ─────────────────────────────┐
│ VoipNet.Enterprise  IvrRunner · CallCenterService · Recording     │
│ VoipNet.AI          konektor IChatClient · STT/TTS · VoiceAgent   │
│ VoipNet.Audio       perangkat OpenAL · WAV/MP3 · VAD · resampler  │
│ VoipNet.Core        VoipClient · VoipCall · VoipConference        │
│                     Diagnostik: PcapWriter · RtpStreamAnalyzer    │
│    │  P/Invoke (LibraryImport), callback function pointer         │
├────┼──────────────────────── C ABI ───────────────────────────────┤
│    ▼                        Rust (voipnet-core)                    │
│ ffi.rs        handle, konfigurasi/event JSON, audio zero-copy      │
│ sip/          transport (UDP/TCP/TLS/WS) · tls · message · UA      │
│ sdp.rs        offer/answer · negosiasi codec                       │
│ media/        session (thread, ICE, SRTP) · dtls · conference      │
│ rtp/          packet · jitter buffer adaptif                       │
│ codec/        G.711 · G.722 · L16 · DTMF (RFC 4733, Goertzel) · PLC │
│ srtp.rs       AES-CM-128-HMAC-SHA1-80 · AES-GCM · replay window    │
│ stun.rs       STUN · kandidat ICE · alokasi TURN                   │
└───────────────────────────────────────────────────────────────────┘
```

## Engine native

**Endpoint (`sip/endpoint.rs`).** Satu user agent SIP per `VoipClient`. Endpoint memegang transport, transaksi client dan server (dikunci berdasarkan branch + method, sehingga CANCEL tidak pernah bentrok dengan INVITE-nya), dialog, registrasi dengan digest authentication, dan semua panggilan. Thread timer 50 ms menjalankan retransmisi RFC 3261 (T1/T2), retransmisi 2xx sampai ACK, refresh registrasi pada 85% masa berlaku, NAT keep-alive, dan pembersihan panggilan yang sudah selesai.

**Thread dan lock.** Thread transport mem-parse dan mendistribusikan pesan; state disimpan di balik satu mutex. Event tidak pernah dipicu selama mutex itu dipegang: event dikirim lewat channel ke thread dispatcher khusus, sehingga handler aplikasi boleh memanggil kembali engine tanpa deadlock. Callback audio berasal dari thread media dan juga dipanggil di luar lock media.

**Media session (`media/session.rs`).** Setiap panggilan punya satu socket UDP (RTP dan RTCP digabung) dan dua thread:

- *receive*: STUN (ke agen ICE di `media/ice.rs`), record DTLS, unprotect SRTP, parse RTP, telephone-event, memasukkan ke jitter buffer, payload pass-through ke aplikasi;
- *playout*: setiap interval paket menjalankan timer ICE dan DTLS, mengambil frame dari jitter buffer, men-decode atau menyamarkan paket hilang, mendeteksi DTMF in-band, mengirim PCM ke .NET, mencampur konferensi, lalu mengirim tepat satu frame dari antrean keluar.

Audio keluar **diantrekan dan diberi tempo**: `SendAudio` boleh dipanggil dengan data sekaligus (misalnya keluaran TTS) dan engine mengirim satu frame per ptime. `ClearAudio` membuang antrean untuk barge-in. Input pada sample rate apa pun di-resample ke rate codec.

**Jitter buffer (`rtp/jitter.rs`).** Mengurutkan berdasarkan nomor urut yang diperluas, menghitung jitter antar-kedatangan RFC 3550, dan menetapkan kedalaman target sekitar tiga kali jitter ditambah satu frame, di antara batas minimum dan maksimum. Paket hilang dilaporkan agar decoder bisa menyamarkannya; kelebihan kedalaman yang berlangsung lama dipangkas agar latensi tetap rendah.

**Keamanan.** Konteks SRTP memakai AES-CM-128-HMAC-SHA1-80 (diperiksa terhadap test vector derivasi kunci RFC 3711) atau AEAD-AES-128/256-GCM (vector RFC 7714); roll-over counter diperkirakan sesuai §3.3.1 dan replay window 64 paket menolak duplikat. Kunci datang lewat SDES (`a=crypto`) atau DTLS-SRTP: `media/dtls.rs` menjalankan engine DTLS 1.2 dimpl (pure Rust) di socket media (paket dibedakan dari STUN dan RTP lewat byte pertamanya), memeriksa sertifikat peer terhadap `a=fingerprint`, lalu memasang kunci hasil ekspor; sebelum itu tidak ada RTP yang dikirim atau diterima. `SrtpMode.Mandatory` menawarkan RTP/SAVP (atau UDP/TLS/RTP/SAVP dengan DTLS); `Optional` menawarkan kunci SDES di RTP/AVP; `Disabled` menolak offer aman dengan 488.

**Kanal data (`media/sctp.rs`).** Dengan `DataChannels` aktif, panggilan membawa stream `m=application` yang terowongan DTLS-nya berisi asosiasi SCTP (RFC 8261/8831). Modulnya sans-IO seperti agen ICE: paket masuk, event keluar, lengkap dengan handshake asosiasi, DATA reliable berurutan dengan SACK dan retransmisi, pemecahan pesan, serta handshake DCEP yang memberi nama kanal (RFC 8832). Tidak ada multi-homing, reliabilitas parsial, maupun congestion control di luar batas data yang belum di-ACK — chat dan file kecil tidak membutuhkannya. Pesan sampai ke aplikasi lewat `DataMessageReceived`.

**Transport signaling.** `sip/transport.rs` memperlakukan TCP, TLS, WS, dan WSS sebagai satu model koneksi: byte stream (polos, atau rustls dengan provider ring) yang bisa membawa frame WebSocket, dengan SIP dibingkai oleh Content-Length. Setiap koneksi punya thread pembaca; pengirim berbagi state rustls di balik lock yang tidak pernah ditahan saat read yang memblokir. Koneksi yang gagal handshake TLS langsung menggagalkan transaksi yang menunggu dengan 503. Sertifikat server diverifikasi terhadap root Mozilla ditambah file CA opsional, atau di-pin dengan fingerprint SHA-256.

## Batas native ↔ .NET

C ABI (`ffi.rs`) sengaja dibuat kecil:

- konfigurasi dan event berupa JSON (jarang, kaya, mudah dikembangkan);
- audio berupa pointer `int16*` dan panjangnya (jalur panas, tanpa salinan dan marshalling);
- statistik berupa struct blittable.

.NET mengikat dengan source generation `LibraryImport` dan callback `delegate* unmanaged` bertanda `[UnmanagedCallersOnly]`; client di-pin dengan `GCHandle` selama endpoint hidup. Exception tidak pernah menyeberang ke Rust.

## Lapisan .NET

`VoipClient` mengubah event engine menjadi event bertipe dan objek `VoipCall`. Setiap panggilan menyediakan:

- `AudioReceived` — delegate `ReadOnlySpan<short>` untuk pemrosesan tanpa alokasi;
- `ReadAudioAsync()` — `IAsyncEnumerable<AudioSegment>` untuk pipeline (pengenalan suara, model realtime);
- task `Connected` dan `Completion` untuk kode `async` yang linear.

Atur `VoipClientOptions.EventSynchronizationContext` di aplikasi UI agar event tiba di thread UI.

## Loop AI

```
audio panggilan ──► ISpeechToText ──► IChatClient (+tools) ──► pemotong kalimat ──► ITextToSpeech ──► SendAudio
      ▲                 │ hasil sementara saat agen berbicara                                           │
      └─────────────────┴──────────── barge-in: batalkan giliran + ClearAudio ◄─────────────────────────┘
```

`VoiceAgent` tetap mendengarkan saat berbicara: loop pengenalan suara dan giliran agen berjalan bersamaan, sehingga transkrip sementara langsung membatalkan giliran dan membersihkan audio yang antre. Jawaban diucapkan per kalimat saat model melakukan streaming. `RealtimeVoiceAgent` mengganti seluruh rantai dengan satu web socket ke model speech-to-speech.

## Contact center

`CallCenterService` menaruh penelepon di antrean berurut prioritas, memesan agen berdasarkan strategi (round robin, paling lama idle, panggilan paling sedikit, berbasis skill), menelepon agen, lalu menjembatani kedua leg dalam konferensi. Karena jembatan tetap di aplikasi, perekaman, supervisor listen-in (`Monitor`), dan metrik tetap berfungsi. `IvrRunner` menjalankan graf `IvrFlow` dan dapat menyerahkan panggilan ke handler async apa pun — biasanya `VoiceAgent`.
