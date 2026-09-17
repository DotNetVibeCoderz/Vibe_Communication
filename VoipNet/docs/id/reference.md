# Referensi konfigurasi dan API

🇬🇧 [English](../en/reference.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

## `VoipClientOptions`

| Properti | Default | Keterangan |
| --- | --- | --- |
| `BindAddress` | `0.0.0.0` | Alamat lokal untuk SIP dan RTP. |
| `SipPort` | `5060` | Port SIP lokal; `0` memilih port bebas. |
| `Transport` | `Udp` | `Udp`, `Tcp`, `Tls` (port remote bawaan 5061), `Ws` atau `Wss` (SIP over WebSocket). |
| `PublicAddress` | — | Alamat yang diiklankan di Via, Contact, dan SDP (NAT statis). |
| `DisplayName` | — | Nama tampilan di From. |
| `Username` | `voipnet` | Bagian user dari address of record. |
| `AuthUsername` | — | User digest bila berbeda dari `Username`. |
| `Password` | — | Password digest. |
| `Domain` | — | Domain SIP; ekstensi diselesaikan terhadap domain ini. |
| `Registrar` | — | Host registrar bila berbeda dari domain. |
| `OutboundProxy` | — | Semua request dikirim ke sini. |
| `RegisterOnStart` | `false` | Registrasi saat `StartAsync` dan tunggu hasilnya. |
| `RegisterExpires` | `600` | Masa berlaku yang diminta; di-refresh pada 85%. |
| `UserAgent` | `Voip.NET/1.0 (Gravicode Studios)` | Header User-Agent / Server. |
| `AudioCodecs` | `G722, PCMU, PCMA` | Urutan preferensi. |
| `Srtp` | `Disabled` | `Disabled`, `Optional`, `Mandatory`. |
| `SrtpKeying` | `Sdes` | `Sdes` (`a=crypto`) atau `Dtls` (DTLS-SRTP dengan ICE, seperti WebRTC). Offer masuk boleh memakai keduanya. |
| `DtmfMode` | `Rfc4733` | `Rfc4733`, `InBand`, `SipInfo`. |
| `Ice` | `false` | Jalankan agen ICE penuh (kandidat, check, nominasi, trickle, restart). Otomatis aktif dengan kunci DTLS atau offer ICE. |
| `StunServer` | — | `host:port` untuk alamat media server-reflexive. |
| `TurnServer`, `TurnUsername`, `TurnPassword` | — | Relay TURN. |
| `RtpPortMin`, `RtpPortMax` | `10000`–`20000` | Rentang port RTP. |
| `PtimeMs` | `20` | Interval paketisasi. |
| `JitterMinMs`, `JitterMaxMs` | `40`, `300` | Batas jitter buffer. |
| `DetectInbandDtmf` | `false` | Jalankan detektor DTMF in-band pada audio masuk. |
| `AutoRinging` | `true` | Kirim 180 Ringing untuk panggilan masuk. |
| `AcceptTransfers` | `true` | Tindak lanjuti REFER yang masuk. |
| `TraceSip` | `false` | Picu `SipTrace` untuk setiap pesan. |
| `KeepaliveSecs` | `25` | Keep-alive CRLF ke registrar melalui UDP. |
| `RtpTimeoutMs` | `0` | Picu media event `rtp-timeout` bila tidak ada RTP selama durasi ini. |
| `TlsVerifyServer` | `true` | Validasi rantai sertifikat server dan nama host (root Mozilla ditambah `TlsCaFile`). |
| `TlsCaFile` | — | File PEM berisi trust anchor tambahan, misalnya CA PBX privat. |
| `TlsPinnedFingerprints` | — | Fingerprint SHA-256 yang diterima; hanya sertifikat ini yang lolos, termasuk self-signed. |
| `TlsCertificateFile`, `TlsPrivateKeyFile` | — | Rantai sertifikat dan kunci PEM yang disajikan ke peer; bila kosong dibuat sertifikat self-signed. |
| `EventSynchronizationContext` | — | Kirim event ke thread UI. |

## `VoipClient`

| Member | Keterangan |
| --- | --- |
| `StartAsync()` | Memulai engine; menunggu registrasi bila `RegisterOnStart` aktif. |
| `RegisterAsync()` / `Unregister()` | Mengelola registrasi. |
| `CallAsync(target)` | Menelepon dan menunggu dijawab. |
| `Call(target)` | Menelepon dan langsung kembali. |
| `PingAsync(target)` | OPTIONS; mengembalikan status dan latensi. |
| `SendMessageAsync(target, body, contentType)` | SIP MESSAGE. |
| `CreateConference()` | `VoipConference` baru. |
| `Calls`, `FindCall(id)` | Panggilan aktif dan yang baru selesai. |
| `LocalAddress`, `RegistrationState`, `EngineVersion`, `TlsFingerprint` | State. |
| Event | `IncomingCall`, `CallStateChanged`, `MediaStarted`, `RegistrationChanged`, `DtmfReceived`, `MessageReceived`, `TransferRequested`, `TransferProgress`, `MediaNotification` (`ice-candidates`, `ice-connected`, `ice-disconnected`, `ice-failed`, `dtls-connected`, `dtls-failed`, `rtp-timeout`), `SipTrace`. |

## `VoipCall`

| Member | Keterangan |
| --- | --- |
| `AnswerAsync()`, `Reject(code)`, `Hangup()`, `HangupAsync()` | Kontrol. |
| `SetHold(bool)`, `SetMute(bool)`, `SendDtmf(digits, ms)`, `RestartIce()` | Kontrol. |
| `Transfer(target)`, `TransferTo(call)` | Transfer blind dan attended. |
| `SendAudio(samples, rate)`, `SendAudio(bytes, rate)`, `SendAudioStreamAsync(...)`, `ClearAudio()`, `QueuedAudioMs` | Audio keluar. |
| `AudioReceived`, `ReadAudioAsync(direction)` | Audio dari panggilan. |
| `SendEncoded(...)`, `EncodedReceived` | Payload pass-through. |
| `GetStatistics()`, `FinalStatistics` | Kualitas. |
| `JoinConference(c)`, `LeaveConference()` | Konferensi. |
| `Connected`, `Completion` | Task untuk kode async yang linear. |
| `Id`, `IsOutgoing`, `RemoteUri`, `RemoteDisplayName`, `State`, `Codec`, `SampleRate`, `Duration`, `Items` | Informasi. |

## Dependency injection

| Extension | Mendaftarkan |
| --- | --- |
| `AddVoipClient(configure)` | singleton `VoipClient` beserta opsinya |
| `AddVoipClientHostedService()` | memulai dan menghentikan client bersama host |
| `AddOpenAiChatClient`, `AddAnthropicChatClient`, `AddGeminiChatClient` | `IChatClient` dengan logging dan function invocation |
| `AddDeepgramSpeechToText`, `AddOpenAiSpeech`, `AddGoogleCloudSpeech`, `AddElevenLabsTextToSpeech`, `AddAmazonPollyTextToSpeech`, `AddElBrunoRealtimeSpeech` | provider suara |
| `AddVoiceAgent(configure)` | `VoiceAgentOptions`, `IConversationStore` (in memory), `VoiceAgentFactory` |
| `AddRealtimeVoiceAgent(configure)` | `RealtimeVoiceAgent` |
| `AddVoipNetEnterprise(configureRecording)` | `IvrRunner`, `CallCenterService`, `RecordingService`, `ICrmConnector` (in memory) |

## C API native

Untuk binding di bahasa lain, `voipnet_core` mengekspor fungsi berikut (semua mengembalikan `0` bila berhasil atau error negatif). Daftar lengkap tanda tangan fungsi ada di [versi English](../en/reference.md#native-c-api).

Kode error: `-1` argumen tidak valid, `-2` konfigurasi tidak valid, `-3` I/O, `-4` panggilan tidak ditemukan, `-5` state tidak valid. JSON konfigurasi memakai nama camelCase dari `VoipClientOptions`. Event berupa objek JSON dengan `type` `registrationChanged`, `incomingCall`, `callState`, `mediaStarted`, `transferRequested`, `transferProgress`, `messageReceived`, `requestResult`, `mediaEvent`, `sipTrace`, atau `log`.
