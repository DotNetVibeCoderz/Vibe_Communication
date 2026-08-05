# Performance — Catatan Pengukuran

Semua angka di dokumen ini diukur, bukan diperkirakan. Harness-nya ada di
[`tools/Telepati.Bench`](tools/Telepati.Bench) dan bisa dijalankan ulang:

```bash
dotnet run --project tools/Telepati.Bench -c Release
```

Optimisasi yang **tidak** memberi perbaikan terukur juga dicatat di sini, karena itu sama
pentingnya dengan yang berhasil.

---

## Lingkungan pengukuran

| | |
|---|---|
| Mesin | PC-CODER · 8 core logis |
| Runtime | .NET 10.0.10 |
| Database | SQLite in-memory (server: SQLite berkas) |
| Tanggal | 6 Agustus 2026 |
| Dataset | 40 pengguna · 60 percakapan · **15.000 pesan** |
| Metode | 3 iterasi pemanasan, lalu 20 iterasi terukur; dilaporkan mean, p50, p95 |

Angka absolut jelas bergantung pada mesin. Yang bermakna adalah **rasio sebelum/sesudah**,
karena keduanya diukur dalam proses yang sama pada data yang sama.

---

## 1. Split query — perbaikan terbesar

Membaca satu halaman pesan menyertakan empat relasi sekaligus: pengirim, lampiran, reaksi, dan
mention. Tiga di antaranya adalah koleksi. Dalam satu query, SQL menggabungkan ketiganya
sehingga barisnya **saling dikalikan** — satu pesan dengan 3 lampiran, 6 reaksi, dan 3 mention
menghasilkan 54 baris, bukan 12.

`AsSplitQuery()` menerbitkan satu SELECT per koleksi dan menyatukannya di memori.

### Percakapan biasa (koleksi jarang)

50 pesan · 7 lampiran · 10 reaksi · 4 mention

| | mean | p50 | p95 |
|---|---|---|---|
| Query tunggal | 4,55 ms | 3,78 ms | 13,27 ms |
| Split query | 3,94 ms | 3,84 ms | **5,05 ms** |
| Selisih | 13,4% lebih cepat | ~sama | **62% lebih cepat** |

Pada data jarang, rata-ratanya nyaris tidak bergerak — dan itu jujur untuk dikatakan. Yang
berubah nyata adalah **ekor latensi**: p95 turun dari 13,27 ms ke 5,05 ms, karena query tunggal
sesekali memulangkan jauh lebih banyak baris daripada yang dibutuhkan.

### Percakapan ramai (koleksi padat)

50 pesan · 157 lampiran · 310 reaksi · 154 mention — satu pesan bisa menghasilkan **96 baris**
dalam query tunggal.

| | mean | p50 | p95 |
|---|---|---|---|
| Query tunggal | 58,98 ms | 59,02 ms | 80,52 ms |
| Split query | 8,40 ms | 8,20 ms | 10,29 ms |
| Selisih | **85,8% lebih cepat** | 86% | 87% |

Di sinilah masalahnya sebenarnya. Grup aktif — yang pesannya banyak dibalas dengan emoji dan
lampiran — persis kasus di mana produk kartesius meledak. Perbaikan 7× ini bukan kasus buatan;
itu bentuk grup kerja yang sibuk.

**Diterapkan di:** `MessageService` (5 query) dan `ChatService.GetChatsAsync`.

---

## 2. Endpoint inti

Layar pertama aplikasi, diukur lewat lapisan layanan yang sesungguhnya (bukan query mentah):

| Operasi | mean | p50 | p95 |
|---|---|---|---|
| `GetChatsAsync` — 30 percakapan dengan pratinjau | 5,33 ms | 5,18 ms | 6,76 ms |
| `GetMessagesAsync` — halaman 50 pesan | 3,94 ms | 3,95 ms | 4,86 ms |

Keduanya termasuk resolusi judul per-penonton, pemetaan DTO, dan resolusi URL lampiran.

---

## 3. Cache pengaturan

`GetOptionsAsync()` dipanggil oleh hampir setiap permintaan — untuk memeriksa feature flag,
batas ukuran, dan saklar keamanan.

| | mean |
|---|---|
| Tanpa cache | 0,04 ms |
| Dengan cache | ~0,00 ms |
| Selisih | 99,1% lebih cepat |

Persentasenya besar tetapi **angka absolutnya kecil**. Ini bukan optimisasi yang akan terasa
oleh pengguna; nilainya adalah menghapus satu query database dari setiap permintaan, yang baru
terlihat saat beban tinggi.

---

## 4. Penyimpanan lokal — SQLite vs LiteDB

Keduanya menyimpan 200 pesan yang sama lalu membaca halaman 50 pesan.

| Provider | Baca 50 pesan | Tulis 50 pesan | Ukuran berkas |
|---|---|---|---|
| **SQLite** | **0,46 ms** | **0,58 ms** | **4 KB** |
| LiteDB | 6,04 ms | 26,26 ms | 224 KB |
| Selisih | SQLite 13× lebih cepat | SQLite **45× lebih cepat** | SQLite 56× lebih kecil |

Selisih ini yang membuat **SQLite menjadi bawaan**. LiteDB tetap disediakan karena ia satu berkas
murni .NET tanpa dependensi native — berguna di platform yang menyulitkan pengiriman pustaka
native SQLite — tetapi biayanya nyata dan pengguna sebaiknya tahu.

### Perbandingan dengan mengambil dari server

Membaca 50 pesan dari cache SQLite: **0,46 ms**. Permintaan HTTP yang setara ke server lokal
diukur di bagian 6 di bawah. Pada jaringan seluler nyata (RTT 80–300 ms), selisihnya menjadi dua
sampai tiga **orde besaran** — itulah alasan cache ini ada.

---

## 5. Format wire — JSON vs MessagePack

Satu halaman 50 pesan, diserialisasi dengan resolver yang sama dipakai protokol MessagePack
SignalR:

| Format | Ukuran |
|---|---|
| JSON | 33.395 byte |
| MessagePack | 26.703 byte |
| Selisih | **20,0% lebih kecil** |

Penghematan 20% itu nyata tetapi sederhana — muatan ini didominasi GUID dan teks bebas, yang
tidak bisa dipadatkan format apa pun. Nilai tambahnya ada di sisi lain: MessagePack tidak perlu
mem-parse string, sehingga biaya CPU per pesan lebih rendah di kedua ujung.

> Catatan koreksi: komentar kode sebelumnya menyebut "kira-kira setengah ukuran JSON". Itu tidak
> benar dan sudah diperbaiki menjadi angka terukur.

---

## 6. HTTP end-to-end

Diukur dengan [`tools/bench-http.sh`](tools/bench-http.sh) terhadap server yang berjalan
(SQLite berkas, data seed: 26 pengguna, 108 percakapan, 589 pesan), 20 permintaan per endpoint.

```bash
dotnet run --project src/Telepati.Server   # terminal 1
bash tools/bench-http.sh                   # terminal 2
```

### Latensi

| Endpoint | mean | p50 | p95 |
|---|---|---|---|
| `GET /api/health` | 11,34 ms | 11,21 ms | 12,68 ms |
| `GET /api/theme/active` (output cache) | 13,52 ms | 11,64 ms | 15,96 ms |
| `GET /api/chats?pageSize=30` | 18,03 ms | 14,41 ms | 37,61 ms |
| `GET /api/messages/{chat}?pageSize=50` | 18,55 ms | 13,43 ms | 51,73 ms |
| `GET /api/admin/dashboard` | 15,09 ms | 10,78 ms | 42,07 ms |

**Cara membaca angka ini.** `/api/health` tidak melakukan apa pun selain mengembalikan sebuah
objek, namun tetap tercatat 11,34 ms. Itu bukan waktu server — itu biaya menjalankan proses
`curl` baru dan melakukan jabat tangan TLS untuk setiap permintaan. Jadi ~11 ms adalah
**lantai pengukuran**, dan kerja server yang sebenarnya adalah selisih di atasnya:

| Endpoint | Kerja server (dikurangi lantai) |
|---|---|
| `/api/theme/active` | ~2,2 ms |
| `/api/chats` | ~6,7 ms |
| `/api/messages` | ~7,2 ms |
| `/api/admin/dashboard` | ~3,8 ms |

Angka-angka itu sejalan dengan pengukuran lapisan layanan di bagian 2 (5,33 ms dan 3,94 ms pada
dataset yang lebih besar), yang memberi keyakinan bahwa keduanya mengukur hal yang sama.

### Kompresi respons

| Endpoint | Tanpa kompresi | Dengan kompresi | Penghematan |
|---|---|---|---|
| `GET /api/chats?pageSize=30` | 6.551 B | 2.093 B | **68,1%** |
| `GET /api/messages/{chat}?pageSize=50` | 1.733 B | 542 B | **68,7%** |
| `GET /swagger/v1/swagger.json` | 69.152 B | 11.562 B | **83,3%** |

Ini penghematan terbesar kedua setelah split query, dan yang paling langsung terasa pengguna di
jaringan lambat. Muatan JSON penuh nama properti dan GUID yang berulang — persis yang paling
disukai algoritma kompresi. Bandingkan dengan MessagePack di bagian 5 yang hanya menghemat 20%:
kompresi HTTP bekerja jauh lebih baik di sini karena ia melihat pengulangan lintas seluruh
dokumen, bukan hanya mengganti pembungkus tiap nilai.

---

## Optimisasi lain yang diterapkan

Yang berikut ini benar secara arsitektur tetapi tidak diberi angka tersendiri, karena efeknya
baru terlihat pada kondisi yang tidak bisa direproduksi di satu mesin.

| Optimisasi | Di mana | Alasan |
|---|---|---|
| **Indeks `(ChatId, IsDeleted, CreatedAt)`** | `TelepatiDbContext` | Filter global `IsDeleted` ikut di setiap pembacaan; memasukkannya ke kunci indeks membuat mesin tidak perlu mengambil baris hanya untuk tahu ia sudah dihapus |
| **Indeks `IX_ChatMembers_Inbox`** | `TelepatiDbContext` | Daftar chat menyaring `UserId + LeftAt` lalu mengurutkan `IsPinned`; satu indeks gabungan menghilangkan langkah sort |
| **Kompresi respons** | `Program.cs` | Muatan JSON sangat mudah dikompres; lihat bagian 6 |
| **Output cache 60 detik** | `/api/theme/active`, `/api/config/client` | Dibaca setiap aplikasi dibuka, berubah sangat jarang |
| **`AsNoTracking()` per query** | Seluruh lapisan layanan | Jalur baca tidak perlu change tracker. Sengaja **tidak** dijadikan bawaan global — itu pernah membuat semua operasi tulis diam-diam gagal (lihat Progress.md) |
| **Cursor paging** | `GetMessagesAsync` | Paginasi berbasis offset bergeser setiap ada pesan baru; kursor stabil dan tidak perlu `OFFSET` yang mahal |
| **Virtualisasi daftar** | `ConversationList.razor` | Hanya baris terlihat yang masuk DOM |
| **Batas 120 bubble** | `ChatThread.razor` | Di Blazor Server setiap node juga membebani diffing render-tree di server |
| **`@key` pada bubble** | `ChatThread.razor` | Blazor mencocokkan elemen alih-alih membangun ulang seluruh runtun saat pesan disisipkan |
| **IntersectionObserver** | `telepati.js` | Dievaluasi browser di luar main thread; tidak memicu handler pada setiap piksel gulir |

---

## Yang dicoba dan tidak dipakai

**`<Virtualize>` untuk thread pesan.** Komponen ini merender dari atas ke bawah lewat
`ItemsProvider`, sementara thread percakapan tumbuh dari bawah dan harus mempertahankan posisi
gulir saat halaman lama disisipkan di atas. Memaksakannya membuat viewport melompat setiap kali
memuat. Yang dipakai: batas jendela render 120 bubble + `IntersectionObserver`, yang memberi
plafon DOM yang sama tanpa melawan arah gulir.

**`localStorage` untuk thread chat.** Ditolak dan diganti IndexedDB. `localStorage` sinkron
(memblokir main thread), dibatasi ~5 MB, hanya menyimpan string (satu thread harus
diserialisasi utuh setiap berubah), dan tidak punya indeks — sehingga "50 pesan terbaru di chat
ini" berarti memuat dan mengurutkan semuanya. IndexedDB asinkron, berukuran ratusan MB,
menyimpan objek terstruktur, dan mengindeksnya. Empat perbedaan itu persis yang dibutuhkan
sebuah thread percakapan.

---

## Rekomendasi berikutnya

Belum dikerjakan, diurutkan berdasarkan dampak yang diperkirakan.

### Berdampak besar

1. **Compiled queries** (`EF.CompileAsyncQuery`) untuk `GetMessagesAsync` dan `GetChatsAsync`.
   Menghilangkan biaya penerjemahan ekspresi pada setiap panggilan. Biasanya 10–30% pada query
   yang sering dipanggil.

2. **Backplane Redis untuk SignalR.** Bukan optimisasi kecepatan melainkan syarat skala: tanpa
   itu, menjalankan lebih dari satu instance server membuat push tidak sampai ke pengguna yang
   terhubung ke instance lain.

3. **Pencarian full-text.** `SearchAsync` memakai `LIKE '%kata%'`, yang tidak bisa memakai indeks
   dan memindai seluruh tabel. Pindah ke `tsvector` (PostgreSQL) atau Full-Text Search
   (SQL Server) mengubahnya dari O(n) menjadi lookup indeks.

4. **Paginasi keyset untuk log aktivitas dan daftar admin.** Halaman ke-500 dengan
   `Skip(499 * 50)` memaksa database memindai 24.950 baris untuk membuangnya.

### Berdampak menengah

5. **Thumbnail gambar saat unggah.** Sekarang klien menerima gambar ukuran penuh untuk
   pratinjau. Membuat thumbnail sekali jauh lebih murah daripada mengirim berkas penuh berulang.

6. **`IAsyncEnumerable` untuk backup.** `BackupService` memuat seluruh tabel ke memori sebelum
   menulis. Pada database besar ini berisiko kehabisan memori.

7. **Batching notifikasi realtime.** Grup dengan 500 anggota memicu 500 pengiriman untuk satu
   pesan. Mengelompokkannya per instance mengurangi overhead.

8. **Rate limiting** (`AddRateLimiter`). Bukan kecepatan, tetapi melindungi angka-angka di atas
   dari satu klien yang berulah.

### Berdampak kecil

9. **`ValueTask` pada jalur cache** yang sering hit sinkron.
10. **Source-generated JSON serializer** untuk DTO — menghilangkan refleksi saat runtime.
11. **Gabungkan `CountAsync` dan halaman data** dalam satu round trip database.

---

## Cara mengukur ulang

```bash
# Benchmark internal (query, penyimpanan lokal, format wire)
dotnet run --project tools/Telepati.Bench -c Release

# HTTP end-to-end
dotnet run --project src/Telepati.Server
# lalu jalankan skrip di bagian 6
```

Jika angka Anda berbeda jauh, yang paling sering jadi sebabnya: build Debug alih-alih Release,
antivirus yang memindai berkas database, atau mesin yang sedang sibuk.
