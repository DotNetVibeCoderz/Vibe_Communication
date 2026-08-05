# API

Server memaparkan permukaan yang sama lewat tiga transport. Pilih salah satu — perilakunya identik.

Swagger interaktif: **https://localhost:7180/swagger**

![Swagger UI](screenshots/15-swagger.png)

---

## Autentikasi

Semua endpoint kecuali yang ditandai butuh header:

```
Authorization: Bearer <access_token>
```

```bash
curl -k -X POST https://localhost:7180/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"usernameOrEmail":"kangfadhil","password":"Telepati123!","deviceName":"cli","deviceType":"test"}'
```

Respons berisi `accessToken` (berlaku 60 menit) dan `refreshToken` (30 hari). Refresh token **dirotasi setiap dipakai** — token lama langsung tidak berlaku.

Jika akun mengaktifkan 2FA, respons pertama berisi `twoFactorRequired: true` tanpa token; kirim ulang permintaan dengan `twoFactorCode`.

---

## REST

### Auth · `/api/auth`

| Metode | Rute | Keterangan |
|--------|------|------------|
| POST | `/register` | Daftar akun baru — anonim |
| POST | `/login` | Masuk — anonim |
| POST | `/refresh` | Perbarui access token — anonim |
| POST | `/logout` | Keluar dari perangkat ini |
| GET | `/sessions` | Perangkat yang sedang login |
| DELETE | `/sessions/{id}` | Keluarkan satu perangkat |
| POST | `/sessions/revoke-others` | Keluarkan semua perangkat lain |
| POST | `/2fa/setup` | Mulai pendaftaran 2FA — mengembalikan QR |
| POST | `/2fa/confirm` | Aktifkan 2FA |
| POST | `/2fa/disable` | Matikan 2FA |

### Users · `/api/users`

| Metode | Rute | Keterangan |
|--------|------|------------|
| GET | `/me` | Profil saya |
| GET | `/{userId}` | Profil pengguna lain |
| GET | `/by-username/{username}` | Cari berdasarkan username |
| PUT | `/me` | Perbarui profil |
| POST | `/me/password` | Ganti password — mencabut semua sesi |
| POST | `/me/presence` | Ubah status kehadiran |
| POST | `/me/location` | Kirim lokasi untuk pencarian sekitar |
| POST | `/search` | Cari via email / telepon / username / sekitar |

### Contacts · `/api/contacts`

| Metode | Rute | Keterangan |
|--------|------|------------|
| GET | `/` | Daftar kontak |
| POST | `/{userId}` | Tambah kontak |
| DELETE | `/{userId}` | Hapus kontak |
| POST | `/{userId}/favorite` | Tandai favorit |
| GET | `/qr` | Kartu kontak saya sebagai QR |
| POST | `/qr/resolve` | Baca hasil pindai QR |
| POST | `/import-phones` | Cocokkan buku telepon dengan pengguna terdaftar |
| POST | `/{userId}/block` · DELETE | Blokir / buka blokir |
| GET | `/blocked` | Daftar yang diblokir |
| POST | `/{userId}/report` | Laporkan pengguna |

### Chats · `/api/chats`

| Metode | Rute | Keterangan |
|--------|------|------------|
| GET | `/` | Daftar percakapan — `?page=&pageSize=&includeArchived=` |
| GET | `/{chatId}` | Detail satu percakapan |
| POST | `/` | Buat grup, channel, atau chat langsung |
| POST | `/direct/{userId}` | Buka (atau buat) chat langsung |
| GET | `/{chatId}/members` | Daftar anggota |
| POST · DELETE | `/{chatId}/members` | Tambah / keluarkan anggota |
| PUT | `/members/role` | Ubah peran anggota |
| POST | `/{chatId}/leave` | Keluar dari percakapan |
| PUT | `/{chatId}` | Ubah judul, deskripsi, avatar |
| POST | `/{chatId}/mute` · `/pin` · `/archive` | Preferensi per pengguna |
| GET | `/channels/search` | Cari channel publik |
| POST | `/channels/{id}/subscribe` | Berlangganan channel |

### Messages · `/api/messages`

| Metode | Rute | Keterangan |
|--------|------|------------|
| GET | `/{chatId}` | Ambil pesan — `?page=&pageSize=&before=` |
| POST | `/` | Kirim pesan |
| PUT | `/` | Ubah pesan |
| DELETE | `/{messageId}` | Hapus pesan |
| POST | `/{messageId}/pin` | Sematkan / lepas |
| GET | `/{chatId}/pinned` | Pesan tersemat |
| POST | `/react` | Beri / batalkan reaksi |
| POST | `/{messageId}/forward/{targetChatId}` | Teruskan |
| POST | `/read` | Tandai sudah dibaca |
| POST | `/typing` | Indikator mengetik |
| GET | `/search` | Cari pesan — `?q=&chatId=` |
| GET | `/{messageId}/delivery` | Laporan pengiriman |
| GET | `/mentions` | Pesan yang menyebut saya |

Paginasi memakai `before=<messageId>` sebagai kursor. Ini penting untuk scrollback panjang: paginasi berbasis offset akan bergeser setiap ada pesan baru masuk.

### Files · `/api/files`

| Metode | Rute | Keterangan |
|--------|------|------------|
| POST | `/upload` | Unggah, mengembalikan `attachmentId` |
| GET | `/{attachmentId}` | Unduh |
| DELETE | `/{attachmentId}` | Hapus |

Alurnya dua langkah: unggah dulu untuk mendapat id, lalu sertakan id itu di `attachmentIds` saat mengirim pesan. Ini yang memungkinkan beberapa lampiran dalam satu pesan.

### Status, Calls, Broadcasts

`/api/status` · `/api/calls` · `/api/broadcasts` — lihat Swagger untuk rinciannya.

`/api/calls/ice-servers` mengembalikan daftar STUN/TURN yang harus dipakai klien untuk WebRTC.

### Admin · `/api/admin`

Butuh peran `Admin` atau `SuperAdmin`. Ini juga titik integrasi untuk sistem luar — REST biasa dengan Swagger, tanpa perlu menyentuh SignalR atau gRPC.

Dashboard · pengguna · percakapan · group insights · laporan · log aktivitas · pengaturan · tema · backup · perawatan.

### Publik

| Rute | Keterangan |
|------|------------|
| `GET /api/health` | Health check |
| `GET /api/theme/active` | Tema yang sedang berlaku, termasuk tema musiman |
| `GET /api/config/client` | Konfigurasi publik untuk klien — tidak pernah memuat rahasia |

---

## SignalR

Hub: `/hubs/telepati`

Token dikirim lewat query string `access_token`, karena browser tidak bisa menyetel header pada handshake WebSocket. Server hanya menerimanya di path `/hubs`.

### Metode yang dipanggil klien

`GetChats` · `GetMessages` · `SendMessage` · `EditMessage` · `DeleteMessage` · `React` · `MarkRead` · `SetTyping` · `SetPresence` · `StartCall` · `AnswerCall` · `EndCall` · `SendCallSignal`

### Peristiwa yang dikirim server

| Nama | Muatan |
|------|--------|
| `MessageReceived` | `MessageDto` |
| `MessageEdited` | `MessageDto` |
| `MessageDeleted` | `{ chatId, messageId }` |
| `ReceiptUpdated` | `{ chatId, messageId, userId, state }` |
| `TypingChanged` | `TypingNotification` |
| `PresenceChanged` | `PresenceNotification` |
| `CallSignal` | `CallSignalDto` |
| `ChatUpdated` | `ChatDto` |

Setiap pengguna masuk ke grup `user:{id}` — satu grup per pengguna, bukan per chat, sehingga perubahan keanggotaan tidak butuh pembukuan grup.

---

## gRPC

Kontrak: [`src/Telepati.Shared/Protos/telepati.proto`](../src/Telepati.Shared/Protos/telepati.proto)

Endpoint: `http://localhost:7181` (HTTP/2 tanpa TLS untuk pengembangan)

Berkas proto menghasilkan **stub klien dan kelas dasar servis sekaligus** dari satu tempat, jadi server dan setiap klien bicara dari kontrak yang sama.

```protobuf
service TelepatiService {
  rpc Login (GrpcLoginRequest) returns (GrpcLoginResponse);
  rpc GetChats (GetChatsRequest) returns (GetChatsResponse);
  rpc SendMessage (GrpcSendMessageRequest) returns (GrpcMessage);
  // …
  rpc Subscribe (SubscribeRequest) returns (stream RealtimeEvent);
}
```

`Subscribe` adalah jalur realtime. Satu envelope `RealtimeEvent` membawa semua jenis push, dibedakan oleh field `kind`, sehingga klien cukup punya satu loop dispatch.

Protobuf tidak punya skalar nullable, jadi konvensinya: GUID yang kosong dikirim sebagai string kosong, angka yang kosong sebagai nol.

---

## Bentuk kesalahan

Endpoint yang bisa gagal karena aturan bisnis mengembalikan `ApiResult`:

```json
{ "success": false, "error": "Bukan anggota chat ini." }
```

HTTP 400 untuk penolakan aturan bisnis, 401 untuk token tidak sah, 404 untuk yang tidak ditemukan. Pesan kesalahan berbahasa Indonesia karena langsung ditampilkan ke pengguna.
