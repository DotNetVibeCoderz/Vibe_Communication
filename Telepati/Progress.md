# Progress — Catatan Pengembangan Telepati

Status terkini dari pekerjaan yang sudah dilakukan. Rencana ke depan ada di [Plan.md](Plan.md).

**Terakhir diperbarui:** 6 Agustus 2026 (putaran keempat: galeri Skills & MCP, pemutar status)

---

## Ringkasan

| Ukuran | Nilai |
|--------|-------|
| Proyek dalam solusi | 13 (12 `src`, 1 `tests`) + 2 tool |
| Status build | ✅ Seluruh solusi ter-*build* tanpa kesalahan |
| Tes | ✅ 58 lolos, 0 gagal |
| Target framework | .NET 10 |
| Provider database | 4 (SQLite, SQL Server, MySQL, PostgreSQL) |
| Transport klien | 3 (SignalR, gRPC, REST) |
| Aplikasi klien | 4 (web, admin, desktop, mobile) |

---

## Yang sudah berjalan dan terverifikasi

Verifikasi berikut dilakukan terhadap server yang benar-benar dijalankan, bukan hanya kompilasi.

| Yang diperiksa | Cara | Hasil |
|----------------|------|-------|
| Pembuatan skema & seeding | Jalankan server dengan database kosong | ✅ 26 pengguna, 108 chat, 5 grup, 3 channel, 589 pesan |
| Login REST | `POST /api/auth/login` | ✅ 200, mengembalikan JWT |
| Daftar percakapan | `GET /api/chats` | ✅ 200, 9 percakapan dengan pratinjau pesan terakhir |
| Kirim pesan | `POST /api/messages` | ✅ 200, pesan tersimpan |
| Laporan pengiriman | `GET /api/messages/{id}/delivery` | ✅ 200, satu penerima berstatus "terkirim" |
| Dashboard admin | `GET /api/admin/dashboard` | ✅ 200, statistik lengkap |
| Tema aktif | `GET /api/theme/active` | ✅ 200, mengembalikan "Telepati Classic" |
| Health check | `GET /api/health` | ✅ 200 |

---

## Bug yang ditemukan pengujian dan sudah diperbaiki

Tiga cacat nyata muncul saat rangkaian tes pertama dijalankan. Semuanya cacat produksi, bukan tes yang salah.

### 1. Tulisan ke database diam-diam tidak berpengaruh

`DbProviderConfigurator` menyetel `QueryTrackingBehavior.NoTracking` sebagai bawaan global. Akibatnya setiap pola "muat entitas → ubah → `SaveChangesAsync`" di seluruh lapisan layanan tidak menulis apa pun. Ganti password, rotasi refresh token, ubah peran, perbarui lokasi — semuanya gagal tanpa suara.

**Perbaikan:** tracking kembali menyala secara bawaan; jalur baca sudah memakai `AsNoTracking()` secara eksplisit di seluruh kode, jadi manfaat performanya tetap ada tanpa risikonya.

### 2. Daftar percakapan melempar kesalahan saat dijalankan

`ChatService.GetChatsAsync` memanggil `Include` setelah `GroupBy(...).Select(...)`. EF Core menolak `Include` yang mengikuti proyeksi, jadi endpoint daftar chat — layar pertama aplikasi — selalu gagal.

**Perbaikan:** pratinjau diambil berdasarkan `Chat.LastMessageId` yang memang sudah disimpan, tanpa pengelompokan di SQL.

### 3. Hitungan belum dibaca tidak pernah kembali ke nol

`SendAsync` menaikkan `UnreadCount` lewat `ExecuteUpdateAsync` (langsung ke SQL, melewati change tracker), sementara `MarkReadAsync` mengubahnya lewat entitas ter-track. Instance ter-track yang basi menimpa nilai yang baru saja dinaikkan, sehingga lencana belum dibaca tidak pernah hilang.

**Perbaikan:** kedua sisi memakai `ExecuteUpdateAsync`. Satu mekanisme, tidak ada selisih dengan tracker.

Selain itu, endpoint daftar mengembalikan **400** ketika parameter paginasi tidak disertakan, karena Minimal API memperlakukan `int page` sebagai wajib. Sekarang semuanya `int?` dengan nilai bawaan lewat `QueryDefaults`.

---

## Putaran kedua — penyimpanan klien, performa, tangkapan layar

### Yang ditambahkan

| Bagian | Isi |
|--------|-----|
| **Telepati.Client.Data** | `ILocalStore` dengan dua provider: **SQLite** (bawaan) dan **LiteDB**, dipilih dari Pengaturan. `CachedTelepatiClient` membungkus transport yang aktif sehingga ketiganya mendapat cache tanpa tahu-menahu |
| **IndexedDB untuk web** | `telepati-store.js` — dipilih menggantikan `localStorage`; alasannya ada di [docs/client-storage.md](docs/client-storage.md) |
| **Virtualisasi** | `<Virtualize>` untuk daftar percakapan; jendela 120 bubble + `IntersectionObserver` untuk thread |
| **Optimisasi server** | Split query, dua indeks baru, MessagePack, kompresi respons, output cache |
| **tools/Telepati.Bench** | Harness pengukuran yang menghasilkan angka di [Performance.md](Performance.md) |
| **tools/Telepati.Shots** | Playwright headless yang mengambil 16 tangkapan layar dari aplikasi yang berjalan |

### Bug nyata yang ditemukan saat pengambilan tangkapan layar

Mengambil tangkapan layar dari aplikasi yang benar-benar berjalan menemukan lima cacat yang
tidak terlihat dari kompilasi maupun tes.

1. **Rute dari pustaka bersama mengembalikan 404.** `Router.AdditionalAssemblies` hanya mengatur
   navigasi sisi klien; penemuan endpoint butuh `AddAdditionalAssemblies` pada
   `MapRazorComponents`. Tanpa itu **setiap URL** di aplikasi web 404.

2. **Aset statis 500/404 di luar Development.** `_framework/blazor.web.js` dan seluruh
   `_content/*` milik RCL tidak tersaji, jadi Blazor tidak pernah menjadi interaktif — form
   login terkirim sebagai POST HTML biasa dan selalu gagal. Diperbaiki dengan `MapStaticAssets()`
   plus `UseStaticWebAssets()`.

3. **Parameter string tanpa `@` diperlakukan sebagai literal.** `TransportName="State.TransportName"`
   menampilkan teks `State.TransportName` di layar.

4. **Interop JS di `OnInitializedAsync` menabrak teardown circuit.** Memuat statistik cache saat
   inisialisasi memicu `ObjectDisposedException` dan membekukan halaman. Statistik sekarang
   dimuat saat panel Pengaturan dibuka.

5. **Mode gelap tidak mengubah palet.** Dua sebab: `ThemeState` hanya menukar ke palet gelap bila
   tema aktif tidak punya Id, dan `body { color: var(--tp-text) }` diselesaikan di `:root`
   sementara variabel tema dipasang di elemen dalam — sehingga seluruh teks mewarisi tinta terang
   di atas latar gelap.

### Perbaikan performa terukur

Rinciannya di [Performance.md](Performance.md). Ringkasnya:

| Perubahan | Hasil |
|---|---|
| Split query pada percakapan ramai | **85,8% lebih cepat** (58,98 ms → 8,40 ms) |
| Split query pada percakapan biasa | p95 **62% lebih cepat**; rata-rata nyaris tidak berubah |
| Kompresi respons | **68%** lebih kecil untuk daftar chat dan halaman pesan |
| Cache lokal SQLite vs LiteDB | SQLite **13× lebih cepat baca**, **45× lebih cepat tulis** |
| MessagePack vs JSON | **20%** lebih kecil di wire |

Yang **tidak** memberi perbaikan berarti juga dicatat, karena sama pentingnya: split query
nyaris tidak menggerakkan rata-rata pada percakapan dengan sedikit reaksi dan lampiran.

### Tangkapan layar

16 gambar di [docs/screenshots/](docs/screenshots/), semuanya dari aplikasi yang berjalan.
Dirujuk dari README, panduan UI, dokumen bot, dan dokumen API.

---

## Putaran ketiga — uji LLM sungguhan dan perbaikan sesi admin

### Kang Bacot diuji dengan model sungguhan

[`tools/Telepati.BotTest`](tools/Telepati.BotTest) menjalankan percakapan nyata lewat
`BotService` — tanpa mock. Kredensial dibaca dari variabel lingkungan, jadi tidak pernah masuk
ke berkas yang ter-commit.

**16 dari 16 pemeriksaan lolos pada dua penyedia**, Azure OpenAI (`gpt-5-mini`) dan DeepSeek
(`deepseek-v4-flash`): ngobrol biasa, tiga kernel function, render Markdown, pencarian Tavily,
memori sesi, `#newpersona`, `#resetbot`, aturan mention di grup, dan isolasi sesi. Rinciannya di
[docs/bot.md](docs/bot.md).

Uji waktu adalah yang paling menentukan: model tidak punya cara mengetahui tanggal hari itu, jadi
jawaban yang benar hanya mungkin kalau `Time.GetCurrentDateTime` benar-benar dipanggil.

Latensi rata-rata: **Azure `gpt-5-mini` 10.230 ms**, **DeepSeek `deepseek-v4-flash` 4.039 ms**.
DeepSeek 2–3× lebih cepat karena `gpt-5-mini` adalah model reasoning yang menghabiskan token
untuk berpikir sebelum menjawab.

### Tiga bug nyata yang ditemukan putaran ini

1. **Model reasoning menolak parameter sampling.** `gpt-5-mini` mengembalikan HTTP 400 untuk
   `max_tokens` — dan juga menolak `temperature` serta `top_p`. Setiap giliran bot gagal.
   `KernelFactory` sekarang mengenali keluarga `o1`/`o3`/`o4`/`gpt-5` dan tidak mengirim
   ketiganya sama sekali.

2. **Bot bisa mengirim balasan kosong.** Model reasoning sesekali mengembalikan konten kosong
   setelah perjalanan tool. `BotService` sekarang mencoba ulang sekali, lalu memberi pesan yang
   jelas — dan giliran yang gagal sengaja tidak disimpan, supaya model tidak melihat
   keheningannya sendiri sebagai konteks.

3. **Konsol admin kehilangan sesi pada setiap navigasi halaman penuh.** `AdminSession` disimpan
   sebagai state per-circuit, jadi berpindah halaman — dan menekan F5 — mengembalikan pengguna
   ke form login. Ini yang membuat empat tangkapan layar admin sebelumnya berisi form login.
   Diganti dengan autentikasi cookie: sebuah endpoint form menerbitkan cookie, dan identitasnya
   dibaca dari klaim, bukan dari circuit.

   Perannya **diperiksa ulang ke database** setiap kali dimuat, jadi cookie yang masih berlaku
   tidak bisa dipakai setelah akunnya diturunkan atau dinonaktifkan.

Tangkapan layar admin sudah diambil ulang dan kini menampilkan isi yang sebenarnya.

### Penyedia model yang didukung

Bertambah dua: **Azure OpenAI** (`Model` berisi nama deployment) dan **DeepSeek**. Konektor
OpenAI juga menerima `Endpoint` khusus, sehingga layanan lain yang kompatibel — Groq,
OpenRouter, vLLM lokal — bisa dipakai tanpa konektor baru.

---

## Putaran keempat — galeri Skills & MCP, pemutar status

### Skills Gallery

Admin bisa mencari, memasang, dan mencabut skill dari repositori yang terdaftar, serta
mendaftarkan repositori baru. Sebuah skill bukan sekadar instruksi: ia membawa `SKILL.md`,
dokumen referensi, dan skrip yang bisa dijalankan. Lima kernel function baru
(`ListSkills`, `LoadSkill`, `ReadReference`, `ListSkillFiles`, `RunSkillScript`) membuat ketiga
lapisan itu terpakai, dengan pengungkapan bertahap: model hanya melihat nama dan deskripsi
sampai ia benar-benar memilih satu skill.

Pengindeksan repositori memakai **satu** panggilan GitHub tree API (`?recursive=1`), bukan
penelusuran folder per folder yang akan menghabiskan rate limit.

Yang dijaga saat memasang:

- Entri zip yang menyebut `../` ditolak sebelum ada yang ditulis ke disk.
- Batas **12 MB** dan **400 berkas** per skill.
- Terpasang dengan `AllowScriptExecution = false` — memasang dan mengizinkan menjalankan skrip
  adalah dua keputusan terpisah.

Eksekusi skrip melewati **tiga** gerbang: saklar global `Bot:EnableCodeExecution`, izin per
skill, dan daftar izin `Bot:AllowedExecutors`. Bawaan daftar itu
`powershell, python, dotnet, cmd, node`, jadi skrip `.py`, `.ps1`, dan `.js` langsung jalan;
`bash` sengaja tidak ikut, sehingga `.sh` ditolak sampai admin menambahkannya sendiri.

Menambahkan nama ke daftar izin saja tidak cukup: `ScriptPlugin.BuildCommandAsync` dan
`ScriptFileFor` harus sama-sama mengenali executor itu, kalau tidak gerbangnya lolos lalu
eksekusinya gagal dengan "tidak dikenali". Untuk `node`, ekstensi berkasnya dipilih dari isi
skrip — `.mjs` bila memakai `import`/`export`, `.cjs` bila tidak — supaya Node tidak menulis
peringatan reparse ke stderr, yang akan terbaca model sebagai kegagalan pada skrip yang berhasil.

### MCP Gallery

28 server terkurasi ikut ter-*seed* — Coding (5), Web (4), Document (4), Design (3), ThreeD (3),
Data (3), Productivity (3), Presentation (2), General (1) — semuanya **dalam keadaan nonaktif**.
Dua transport didukung: stdio (proses anak) dan HTTP. Tombol uji memanggil `tools/list` dan
menyimpan hasilnya, jadi server yang perintahnya belum terpasang gagal di halaman admin, bukan
diam-diam saat pengguna sedang mengobrol.

`McpToolProvider` menyimpan klien per proses dan memberi **cooldown 5 menit** pada server yang
gagal, supaya satu server rusak tidak memperlambat setiap balasan.

Rinciannya di [docs/skills-and-mcp.md](docs/skills-and-mcp.md).

### Tiga bug nyata yang ditemukan putaran ini

1. **Menyimpan form MCP menimpa kunci API dengan topengnya.** Rahasia ditampilkan sebagai
   `••••••`, dan menyimpan form mengirim topeng itu kembali. Mengubah kategori saja sudah cukup
   untuk merusak server yang tadinya berfungsi. `MergeEnvironment` sekarang memulihkan nilai asli
   untuk setiap kunci yang kembali dalam keadaan tertutup.

2. **Status tidak bisa diklik.** Panel kanan dipakai bersama dengan thread chat, jadi membuka
   status justru memperlihatkan percakapan terakhir — yang kebetulan percakapan dengan Kang
   Bacot. Dibuat `StatusViewer`: daftar dikelompokkan per orang, bilah segmen seperti messenger
   lain, dan panel kanan sekarang bercabang antara thread dan pemutar.

   Teks, gambar, dan video masing-masing punya perlakuannya sendiri. Teks dan gambar maju sendiri
   setelah 6 detik; **video tidak** — memotong klip di tengah lebih buruk daripada menunggu.
   Setiap status dilaporkan tepat sekali lewat `_lastReported`, supaya render ulang tidak
   menggelembungkan hitungan penonton.

3. **Setiap gambar, video, dan lampiran gagal dimuat di aplikasi web.** Provider storage
   FileSystem mengembalikan path relatif-server (`/files/…`), yang di browser diselesaikan
   terhadap host yang merender — aplikasi messenger di port 7200, bukan server di 7180. Bug ini
   baru terlihat setelah seeding mulai membuat status bergambar; sebelum itu tidak ada media
   untuk dimuat. `MediaResolver` sekarang menyelesaikannya terhadap `ClientOptions.ServerUrl`,
   dan URL absolut dari provider cloud lewat tanpa disentuh.

   Perbaikannya berlaku untuk avatar, lampiran pesan, dan status sekaligus — dan juga menutup
   masalah yang sama di desktop dan mobile, yang bahkan tidak punya origin web untuk dijadikan
   acuan.

### Verifikasi

| Yang diperiksa | Hasil |
|----------------|-------|
| Build seluruh solusi | ✅ tanpa kesalahan, 0 peringatan |
| Rangkaian tes | ✅ 58 lolos, 0 gagal |
| Tangkapan layar | ✅ 21 diambil, 0 dilewati |
| Status teks terbuka | ✅ `09-status-viewer.png` |
| Status gambar terbuka | ✅ `09b-status-foto.png` |

---

## Rincian per komponen

### Telepati.Domain ✅
23 entitas, 17 enum. Satu tabel `Chats` menampung semua jenis percakapan.

### Telepati.Shared ✅
DTO, pohon konfigurasi `TelepatiOptions`, dan `telepati.proto` yang menghasilkan stub klien serta kelas dasar servis sekaligus.

### Telepati.Infrastructure ✅
EF Core empat provider, sharding deterministik, cache Memory/Redis, storage FileSystem/Blob/S3/MinIO, dan seluruh layanan domain: auth (JWT + TOTP + sesi), pengguna, kontak, chat, pesan, lampiran, status, panggilan, broadcast, admin, tema, backup, pengaturan.

### Telepati.Bot ✅
Semantic Kernel dengan enam penyedia (OpenAI, Azure OpenAI, DeepSeek, Anthropic, Gemini, Ollama). Sesi terisolasi, `#resetbot`, `#newpersona`, auto-compact. Enam plugin: waktu, matematika, web, berkas, skrip, dan skill — semuanya di dalam sandbox workspace. Tool dari server MCP yang aktif ikut masuk ke kernel lewat `McpToolProvider`.

### Telepati.Server ✅
Minimal API + Swagger, SignalR hub, gRPC service dengan server-streaming, fan-out realtime ke kedua transport, autentikasi JWT, kebijakan admin, penyajian berkas statis.

### Telepati.Client.Core ✅
`ITelepatiClient` dengan tiga implementasi dan `TransportSwitcher` untuk berpindah saat berjalan.

### Telepati.UI ✅
Sistem desain "Gelombang", komponen chat, `StatusViewer` untuk status teks/gambar/video, renderer Markdown dengan sanitasi, `ChatState` dan `ThemeState`, serta halaman messenger yang dipakai bersama tiga host. `MediaResolver` menyelesaikan URL media terhadap alamat server, jadi gambar dan lampiran termuat di keempat host.

### Telepati.Web ✅ · Telepati.Admin ✅
Messenger Blazor Server dan konsol admin dengan dashboard realtime, grafik tren, pengelola tema, serta galeri Skills dan MCP.

### Telepati.Desktop ✅
Avalonia + `NativeWebView` yang menampilkan host Blazor Server in-process pada loopback. Avalonia tidak punya kontrol BlazorWebView, dan pendekatan ini menjaga desktop tetap memakai komponen Razor yang sama.

### Telepati.Mobile ✅
MAUI Blazor. Ter-*build* untuk `net10.0-android`; iOS dan Mac Catalyst perlu host macOS untuk dikompilasi.

---

## Yang perlu diketahui sebelum melanjutkan

**Belum ada migrasi EF Core.** Skema dibuat dengan `EnsureCreated`. Sebelum produksi, buat migrasi — lihat [docs/deployment.md](docs/deployment.md).

**Enkripsi ujung-ke-ujung belum lengkap.** Server menyimpan ciphertext yang dikirim klien dan tidak pernah menyentuh plaintext, tetapi pertukaran kunci belum ada. Kolom `User.PublicKey` sudah disiapkan.

**Backplane SignalR belum dikonfigurasi.** Menjalankan lebih dari satu instance server membutuhkan `AddStackExchangeRedis` pada `AddSignalR`, kalau tidak push tidak sampai ke pengguna di instance lain.

**Klien menerima sertifikat apa pun.** `Telepati.Client.Core` memakai `DangerousAcceptAnyServerCertificateValidator` agar sertifikat pengembangan bisa dipakai. Harus dihapus sebelum merilis aplikasi klien.

**Bot butuh model.** Bawaannya Ollama di `localhost:11434`. Tanpa model yang berjalan, Kang Bacot akan menjawab dengan pesan kesalahan yang sopan, dan sisa aplikasi tetap berfungsi normal.
