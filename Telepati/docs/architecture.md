# Arsitektur Telepati

Dokumen ini menjelaskan keputusan yang tidak terlihat dari membaca satu berkas saja.

---

## Gambaran besar

```
                     ┌──────────────────────────────────────────┐
                     │            Telepati.Server               │
   Web ─────┐        │                                          │
   Desktop ─┼──?──►  │  Minimal API ──┐                         │
   Mobile ──┘        │  SignalR Hub ──┼──► ChatOrchestrator ──┐ │
                     │  gRPC Service ─┘                       │ │
                     │                                        ▼ │
   Admin ─────────►  │             Telepati.Infrastructure      │
   (langsung ke DB)  │   ┌──────────┬─────────┬──────────────┐  │
                     │   │ EF Core  │  Cache  │   Storage    │  │
                     │   └────┬─────┴────┬────┴──────┬───────┘  │
                     └────────┼──────────┼───────────┼──────────┘
                              ▼          ▼           ▼
                     SQLite/SQLServer  Memory   FileSystem
                     MySQL/PostgreSQL  /Redis   /Blob/S3/MinIO
```

Tanda `?` adalah inti desainnya: klien memilih jalurnya sendiri.

---

## Tiga transport, satu logika

`Telepati.Server` memaparkan permukaan yang sama tiga kali:

| Transport | Endpoint | Realtime | Cocok untuk |
|-----------|----------|----------|-------------|
| **SignalR** | `/hubs/telepati` | Push lewat koneksi yang sama | Bawaan. Paling responsif. |
| **gRPC** | port 7181 (HTTP/2) | Server-streaming `Subscribe` | Mobile dan desktop — framing biner paling hemat data |
| **REST** | `/api/*` | Polling | Jaringan ketat, proxy korporat, integrasi pihak ketiga |

Ketiganya memanggil layanan yang sama di `Telepati.Infrastructure`. `TelepatiHub`, `TelepatiGrpcService`, dan endpoint Minimal API tidak berisi logika bisnis — hanya penerjemahan bentuk data.

### Bagaimana push bekerja

Layanan domain tidak tahu siapa yang mendengarkan. Mereka memanggil `IRealtimeNotifier`:

```csharp
await notifier.MessageSentAsync(dto, memberIds, ct);
```

Server mendaftarkan `CompositeRealtimeNotifier`, yang mengirim ke **keduanya**:

- Grup SignalR `user:{id}` — satu grup per pengguna, bukan per chat, supaya perubahan keanggotaan tidak perlu pembukuan grup
- Channel gRPC di `GrpcSubscriberRegistry` — bounded channel dengan `DropOldest`, sehingga satu klien yang macet tidak menahan pengirim

Ini penting karena satu orang bisa memakai web (SignalR) dan desktop (gRPC) bersamaan. Keduanya harus menerima pesan yang sama.

Envelope gRPC dibangun **lazy** — kalau tidak ada klien gRPC yang terhubung (kasus umum untuk deployment web murni), konversi protobuf tidak pernah dijalankan.

### Di sisi klien

`ITelepatiClient` adalah satu-satunya permukaan yang dilihat UI. Tiga implementasi:

- `SignalRTelepatiClient` — hub invocation, dengan REST sebagai cadangan saat socket putus
- `GrpcTelepatiClient` — panggilan unary + loop `Subscribe` yang menyambung ulang sendiri
- `RestTelepatiClient` — HTTP murni; realtime disimulasikan dengan polling

`TransportSwitcher` menutup koneksi lama dan membuka yang baru saat pengguna berganti pilihan di halaman Pengaturan. Tidak perlu restart.

Login selalu lewat REST, bahkan pada transport lain: hub dan gRPC service butuh token, jadi token harus ada lebih dulu.

---

## Satu UI untuk empat host

Halaman messenger (`Login`, `Register`, `Messenger`) tinggal di `Telepati.UI/Pages`, bukan di aplikasi web. Setiap host menunjuk `Router`-nya ke assembly itu:

```razor
<Router AppAssembly="typeof(Program).Assembly"
        AdditionalAssemblies="new[] { typeof(Telepati.UI.Pages.Messenger).Assembly }">
```

Yang disediakan masing-masing host hanyalah dokumen HTML dan layout:

| Host | Cara merender |
|------|---------------|
| **Web** | Blazor Server biasa |
| **Desktop** | Avalonia membuka jendela berisi `NativeWebView` yang menunjuk ke Blazor Server **in-process** di loopback (port 0, dipilih OS) |
| **Mobile** | MAUI `BlazorWebView` |

Avalonia tidak punya kontrol `BlazorWebView`, jadi pendekatan host in-process inilah yang membuat desktop tetap memakai komponen Razor yang sama — bukan menyalin UI ke XAML.

---

## Model data

Satu tabel `Chats` menampung **semua** jenis percakapan — langsung, grup, channel, bot, pesan tersimpan — dibedakan oleh `ChatType`. Konsekuensinya: penyimpanan pesan, pengiriman, dan pencarian hanya punya satu jalur kode, dengan aturan tambahan di atasnya.

Beberapa detail yang mudah terlewat:

**Chat langsung tidak menyimpan judul.** Setiap pihak melihat nama lawan bicaranya, jadi judul diselesaikan per-penonton di `ChatService.ResolveTitleAsync`.

**Setiap pesan menghasilkan satu baris `MessageReceipt` per penerima.** Itulah yang membuat laporan pengiriman (terkirim/sampai/dibaca/gagal) mungkin.

**Hitungan belum dibaca ditulis lewat `ExecuteUpdate`, bukan entitas ter-track.** Mencampur keduanya membuat instance ter-track yang basi menimpa nilai yang baru saja dinaikkan lewat SQL di context yang sama.

**Chat terenkripsi menyimpan ciphertext saja.** Server tidak pernah memegang plaintext-nya, jadi pencarian sengaja melewati chat tersebut daripada mengembalikan hasil menyesatkan.

### Sharding

`ShardResolver` memetakan percakapan ke node dengan hash stabil atas teks kanonik GUID (bukan `GetHashCode`, yang tidak stabil antar-proses). Kuncinya `ChatId`, jadi seluruh pesan satu percakapan selalu berada di node yang sama dan pembacaan tidak pernah menyentuh lebih dari satu shard.

---

## Konfigurasi berlapis

```
appsettings.json  →  TelepatiOptions  →  overlay dari tabel AppSettings  →  nilai efektif
```

`SettingsService.GetOptionsAsync()` membaca default dari berkas, lalu menimpanya dengan baris database. Aplikasi admin menulis ke tabel itu. Hasilnya: setiap kunci bisa diubah saat runtime tanpa deploy ulang.

Katalog pengaturan **diturunkan lewat refleksi** dari pohon `TelepatiOptions`, jadi properti baru langsung muncul di UI admin tanpa kode tambahan.

Override yang rusak tidak pernah menjatuhkan aplikasi — `ApplyOverride` menelan kesalahan konversi dan membiarkan nilai berkas yang berlaku.

---

## Alur pesan lengkap

1. Klien memanggil `SendMessage` (transport apa pun)
2. `ChatOrchestrator.SendAsync` meneruskan ke `MessageService.SendAsync`
3. Layanan memvalidasi keanggotaan, hak posting, panjang, dan blokir
4. Pesan disimpan; satu `MessageReceipt` dibuat per penerima
5. Hitungan belum dibaca dinaikkan lewat `ExecuteUpdate` setelah commit
6. `IRealtimeNotifier` menyiarkan ke SignalR **dan** gRPC
7. Kalau `ShouldRespondAsync` benar, `ChatOrchestrator` menjalankan giliran bot **di latar** — pengirim tidak menunggu model

Langkah 7 memakai scope DI baru: task lepas tidak boleh meminjam layanan scoped milik request yang sudah selesai.

---

## Keamanan

| Lapisan | Cara kerja |
|---------|------------|
| **Autentikasi** | JWT HS256; refresh token dirotasi setiap kali dipakai, sehingga token curian hanya berguna sekali |
| **Sesi** | Satu baris per perangkat; ganti password mencabut semuanya |
| **2FA** | TOTP dengan jendela ±1 langkah untuk menyerap selisih jam |
| **Otorisasi** | Peran chat (`ChatMemberRole`) dan peran global (`UserRole`) diperiksa terpisah |
| **Blokir** | Simetris — diperiksa di kedua arah pada jalur pengiriman, pencarian, dan status |
| **Output bot** | Markdown dirender dengan HTML mentah dimatikan, lalu disanitasi lagi (script, handler event, `javascript:`) |
| **Sandbox bot** | Setiap path lewat `Workspace.Resolve`, yang menolak path absolut dan traversal |
| **Storage** | `FileSystemStorageService` juga menolak kunci yang keluar dari root |

---

## Yang belum dikerjakan

Lihat [Plan.md](../Plan.md) Fase 6. Yang paling penting: enkripsi ujung-ke-ujung baru menyimpan ciphertext yang dikirim klien — pertukaran kunci (X3DH + Double Ratchet) belum diimplementasikan, dan kolom `User.PublicKey` menunggu itu.
