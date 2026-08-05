# Plan — Roadmap Pengembangan Telepati

Dibuat oleh **Gravicode Studios**, dipimpin oleh **Kang Fadhil**.

Dokumen ini adalah peta jalan. Status yang sudah berjalan dicatat di [Progress.md](Progress.md).

---

## Fase 1 — Fondasi ✅

Membangun kerangka yang membuat semua fase berikutnya mungkin.

- [x] Solusi .NET 10 multi-proyek dengan lapisan yang jelas (Domain → Infrastructure → Server → Clients)
- [x] Model domain lengkap: user, chat, pesan, kontak, status, panggilan, bot, admin
- [x] EF Core dengan empat provider (SQLite, SQL Server, MySQL, PostgreSQL) dari satu model
- [x] Sharding deterministik berbasis hash pada `ChatId`
- [x] Abstraksi cache (MemoryCache / Redis) dan storage (FileSystem / Azure Blob / S3 / MinIO)
- [x] Konfigurasi berlapis: appsettings sebagai default, tabel `AppSettings` sebagai override runtime
- [x] Seed data realistis: 26 akun, 5 grup, 3 channel, ratusan pesan

## Fase 2 — Server & Tiga Transport ✅

Inti produk: satu backend, tiga cara berbicara dengannya.

- [x] Autentikasi JWT dengan rotasi refresh token
- [x] 2FA TOTP opsional (QR + verifikasi), bisa dimatikan global dari admin
- [x] Manajemen sesi perangkat (multi-device, cabut per perangkat)
- [x] Minimal API + Swagger untuk seluruh permukaan REST
- [x] SignalR hub sebagai transport default
- [x] gRPC service dengan server-streaming `Subscribe` untuk realtime
- [x] Fan-out realtime yang mengirim ke SignalR dan gRPC sekaligus
- [x] Grup dengan role owner/admin/moderator/member
- [x] Channel broadcasting satu-ke-banyak
- [x] Reply, forward, pin, reaksi, mention
- [x] Laporan pengiriman per penerima (terkirim / sampai / dibaca / gagal)
- [x] Upload lampiran dengan batas ukuran dan pembersihan berkas yatim
- [x] Status 24 jam, sinyal WebRTC untuk panggilan suara & video
- [x] Broadcast ke banyak kontak sekaligus
- [x] Blokir, laporkan, dan penegakannya di jalur pengiriman

## Fase 3 — Kang Bacot ✅

Bot temen ngobrol berbasis Semantic Kernel.

- [x] Multi-provider: OpenAI, Anthropic, Gemini, Ollama — dipilih dari konfigurasi
- [x] Sesi terisolasi per user (DM) dan per grup
- [x] Di grup hanya menjawab saat di-mention
- [x] `#resetbot` — kosongkan memori sesi dan kembalikan persona bawaan
- [x] `#newpersona [teks]` — ganti persona untuk sesi itu saja
- [x] Auto-compact: ringkas percakapan lama saat mendekati batas context window
- [x] Kernel functions: waktu, matematika, statistik, pencarian Tavily, scraping halaman, baca file dari URL
- [x] Eksekusi skrip (PowerShell / Python / .NET / cmd) di dalam workspace tersandbox
- [x] Kirim hasil kerja ke user lewat storage (folder otomatis di-zip)
- [x] Render Markdown ke HTML dengan sanitasi (tabel, kode, gambar, video, audio)

## Fase 4 — Aplikasi Klien ✅

- [x] Pustaka UI Blazor bersama dipakai web, desktop, dan mobile
- [x] Sistem tema: terang/gelap + tema musiman otomatis (Lebaran, Tahun Baru, 17 Agustus, Natal)
- [x] Micro-interaction dengan motif "Gelombang" (riak kirim, cincin sonar, bilah sinyal)
- [x] Web messenger — Blazor Server
- [x] Web admin — Blazor Server dengan dashboard realtime
- [x] Desktop — Avalonia + host Blazor in-process (Windows, macOS, Linux)
- [x] Mobile — MAUI Blazor (Android, iOS, Mac Catalyst, Windows)
- [x] Transport bisa diganti dari halaman pengaturan tiap klien, tanpa restart

## Fase 5 — Admin & Operasional ✅

- [x] Dashboard realtime: pengguna, pesan, penyimpanan, panggilan, laporan
- [x] Tren 14 hari untuk pesan dan pendaftaran
- [x] Manajemen pengguna: peran, aktif/nonaktif, cabut sesi
- [x] Group insights: kontributor teratas dan tren aktivitas
- [x] Antrean laporan pengguna dengan tindak lanjut
- [x] Log aktivitas yang bisa disaring
- [x] Editor pengaturan untuk seluruh kunci konfigurasi
- [x] Master data tema, termasuk pembuatan tema musiman baru
- [x] Backup database ke `.sql` lintas-provider
- [x] Perawatan: bersihkan status kedaluwarsa, berkas yatim, log lama

---

## Fase 6 — Berikutnya 🔜

Yang sengaja belum dikerjakan, beserta alasannya.

### Prioritas tinggi

| Item | Kenapa penting | Catatan |
|------|----------------|---------|
| **E2E encryption penuh** | Saat ini server menyimpan ciphertext yang dikirim klien; pertukaran kunci belum ada | Perlu implementasi X3DH + Double Ratchet di `Telepati.Client.Core`; kolom `User.PublicKey` sudah disiapkan |
| **Push notification** | Mobile tidak menerima pesan saat aplikasi tertutup | FCM untuk Android, APNs untuk iOS |
| **Migrasi EF Core** | Sekarang memakai `EnsureCreated`; belum ada jalur upgrade skema | `dotnet ef migrations add Initial` per provider |
| **TURN server** | Panggilan gagal di balik NAT simetris | STUN saja tidak cukup untuk jaringan seluler Indonesia |
| **Rate limiting** | Belum ada perlindungan dari penyalahgunaan API | `AddRateLimiter` bawaan ASP.NET Core |

### Prioritas menengah

- Panggilan grup (SFU) — WebRTC mesh sekarang hanya nyaman untuk 2–4 peserta
- Pencarian full-text (PostgreSQL `tsvector` / SQL Server FTS) menggantikan `LIKE`
- Voice note dengan waveform
- Pesan terjadwal dan pesan yang menghilang sendiri
- Sticker buatan pengguna dan paket sticker
- Terjemahan pesan inline lewat Kang Bacot
- Ekspor riwayat chat per pengguna (kepatuhan data)

### Prioritas rendah

- Bot pihak ketiga dengan webhook API
- Tema buatan pengguna (bukan hanya admin)
- Mode hemat data untuk media
- Widget desktop dan integrasi tray

---

## Prinsip yang dipegang

1. **Transport adalah pilihan pengguna, bukan keputusan build.** Semua logika bisnis buta terhadap transport.
2. **Setiap dependensi infrastruktur bisa ditukar dari konfigurasi.** Database, cache, storage, model AI.
3. **Satu UI untuk empat host.** Komponen Razor ditulis sekali di `Telepati.UI`.
4. **Konfigurasi bisa diubah tanpa deploy.** appsettings sebagai default, database sebagai override.
5. **Bot tidak pernah dipercaya.** Output-nya disanitasi, skripnya tersandbox, path-nya divalidasi.
