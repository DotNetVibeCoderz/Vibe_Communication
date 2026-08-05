# Penyimpanan di Sisi Klien

Setiap aplikasi klien menyimpan data yang sudah ditarik dari API supaya tidak perlu mengambilnya
dua kali. Yang belum ada di penyimpanan lokal tetap diambil langsung dari server.

Tiga platform, dua mekanisme berbeda — karena kendalanya memang berbeda.

| Aplikasi | Mekanisme | Alasan |
|----------|-----------|--------|
| **Desktop** & **Mobile** | Berkas basis data: **SQLite** (bawaan) atau **LiteDB** | Punya sistem berkas; akses langsung tanpa perantara |
| **Web** | **IndexedDB** di browser | Tidak punya sistem berkas; IndexedDB adalah satu-satunya penyimpanan browser yang cocok untuk thread percakapan |

![Pengaturan penyimpanan lokal](screenshots/09-pengaturan.png)

---

## Desktop dan mobile

### Memilih provider

Dari **Pengaturan → Penyimpanan lokal**, atau di `appsettings.json`:

```jsonc
"TelepatiClient": {
  "LocalDatabase": {
    "Provider": "Sqlite",        // Sqlite | LiteDb | None
    "Path": "",                  // kosong = folder app-data platform
    "Enabled": true,
    "FreshnessSeconds": 60,      // daftar yang lebih tua dari ini disegarkan dulu
    "KeepMessagesPerChat": 300,  // pesan terbaru yang tidak pernah dipangkas
    "RetentionDays": 30          // selebihnya dipangkas setelah sekian hari
  }
}
```

Perubahan provider berlaku setelah aplikasi dijalankan ulang — basis data dibuka sekali saat
start, dan menukarnya di tengah jalan berarti menarik berkas dari bawah pembacaan yang aktif.

### Mengapa SQLite yang jadi bawaan

Keduanya diukur pada beban yang sama ([Performance.md](../Performance.md) bagian 4):

| Provider | Baca 50 pesan | Tulis 50 pesan | Ukuran berkas |
|---|---|---|---|
| **SQLite** | **0,46 ms** | **0,58 ms** | **4 KB** |
| LiteDB | 6,04 ms | 26,26 ms | 224 KB |

LiteDB tetap disediakan karena ia satu berkas murni .NET tanpa dependensi native — berguna di
platform yang menyulitkan pengiriman pustaka native SQLite. Tetapi biayanya nyata, terutama
pada penulisan, dan pengguna sebaiknya tahu sebelum memilihnya.

### Cara kerjanya

Sebuah dekorator, `CachedTelepatiClient`, dipasang di depan transport yang sedang aktif. Karena
ia membungkus `ITelepatiClient`, **ketiga transport mendapatkannya tanpa tahu-menahu**, dan
berganti transport saat berjalan tetap memakai cache yang sama.

Aturannya cache-first dengan cadangan API:

| Situasi | Yang terjadi |
|---------|--------------|
| Ada di lokal dan masih segar | Dilayani dari disk, tanpa menyentuh jaringan |
| Ada di lokal tapi kedaluwarsa | Ditampilkan **sekarang** dari disk, lalu disegarkan di latar |
| Tidak ada di lokal | Diambil dari API, lalu disimpan |
| Halaman scrollback yang pernah dibaca | Selalu dari disk |
| Halaman yang tidak lengkap di lokal | Diambil dari server — thread yang berlubang lebih buruk daripada menunggu |

Push realtime ditulis langsung ke penyimpanan begitu tiba, jadi cache tetap hangat selama
aplikasi berjalan, bukan hanya terisi saat ada pembacaan.

Kegagalan penyimpanan **tidak pernah** menjatuhkan aplikasi: berkas rusak atau terkunci
diperlakukan sebagai "tidak ada cache", bukan sebagai error yang dilihat pengguna.

### Isolasi dan batas

- Satu berkas per akun (`telepati-{userId}.db`), jadi berganti pengguna tidak pernah mencampur
  percakapan dua orang.
- Keluar dari akun **mengosongkan** penyimpanan.
- Pemangkasan berjalan saat basis data dibuka: apa pun yang lebih tua dari `RetentionDays`
  dihapus, kecuali `KeepMessagesPerChat` pesan terbaru tiap percakapan — itulah yang membuat
  pembukaan berikutnya tetap instan.

### Bentuk penyimpanan

Kunci dan penyortiran disimpan sebagai kolom sungguhan; DTO-nya sendiri disimpan sebagai JSON.
Klien tidak pernah melakukan query ke dalam isi sebuah pesan, jadi model relasional penuh hanya
akan menambah beban migrasi skema tanpa manfaat pembacaan.

SQLite dijalankan dengan `journal_mode=WAL` dan `synchronous=NORMAL` — pertukaran standar di
sisi klien: penulisan berhenti memblokir pembacaan, dan risiko terburuk saat listrik mati adalah
kehilangan beberapa baris cache terakhir, yang toh diambil ulang pada sinkronisasi berikutnya.

---

## Web

### Kenapa IndexedDB, bukan localStorage

`localStorage` adalah pilihan pertama yang terlihat jelas, dan salah untuk kasus ini:

| | localStorage | IndexedDB |
|---|---|---|
| **Sifat** | Sinkron — memblokir main thread di setiap pembacaan | Asinkron |
| **Kapasitas** | ±5 MB | Ratusan MB |
| **Bentuk data** | String saja — satu thread harus diserialisasi utuh setiap berubah | Objek terstruktur |
| **Indeks** | Tidak ada — "50 pesan terbaru di chat ini" berarti memuat dan mengurutkan semuanya | Ada — pembacaan kursor yang terbatas |

Keempat perbedaan itu persis yang dibutuhkan sebuah thread percakapan. Implementasinya ada di
[`telepati-store.js`](../src/Telepati.UI/wwwroot/js/telepati-store.js), memakai indeks gabungan
`[chatId, createdAt]` sehingga satu kursor menjawab "N pesan terbaru sebelum waktu ini".

### Di mana ia dipakai

Perlu jujur soal ini: pada **Blazor Server**, setiap panggilan ke IndexedDB adalah perjalanan
bolak-balik lewat circuit SignalR. Jadi ia dipakai di tempat yang memang membayar — **melukis
thread segera setelah halaman dimuat ulang atau tersambung kembali**, ketika alternatifnya
adalah layar kosong sambil menunggu API.

Ia **tidak** dipakai sebagai cache baca di jalur panas: cache sisi server sudah menangani itu
tanpa perlu meninggalkan server sama sekali.

### Virtualisasi dan gulir tak berujung

Dua hal berbeda, untuk dua masalah berbeda.

**Daftar percakapan → `<Virtualize>`.** Tingginya seragam dan daftarnya bisa panjang, jadi hanya
jendela yang terlihat yang masuk DOM. `ItemSize` harus cocok dengan tinggi baris di CSS —
nilai yang salah membuat scrollbar meleset.

**Thread pesan → jendela terbatas + `IntersectionObserver`.** `<Virtualize>` sengaja *tidak*
dipakai di sini: ia merender dari atas ke bawah lewat `ItemsProvider`, sementara thread
percakapan tumbuh dari bawah dan harus mempertahankan posisi gulir ketika halaman lama
disisipkan di atas. Memaksakannya membuat viewport melompat setiap kali memuat.

Yang dipakai:

- Maksimal **120 bubble** yang benar-benar dirender. Di Blazor Server setiap node juga membebani
  diffing render-tree **di server**, bukan hanya DOM di browser.
- Sebuah sentinel di puncak thread diamati `IntersectionObserver`; begitu ia mendekat, halaman
  berikutnya dimuat sendiri. Observer dievaluasi browser di luar main thread dan tidak terpicu
  pada setiap piksel gulir.
- Offset gulir diukur sebelum penyisipan dan dipulihkan sesudahnya, supaya pembaca tidak
  terlempar ke bawah setiap kali halaman lama masuk.
- `@key` pada tiap bubble, agar Blazor mencocokkan elemen alih-alih membangun ulang seluruh
  runtun saat satu pesan disisipkan atau diubah.

### Membersihkan

Dari **Pengaturan → Penyimpanan lokal**: *Segarkan info*, *Rapikan data lama*, atau
*Kosongkan*. Panel yang sama muncul di ketiga aplikasi; yang berbeda hanya nama provider yang
ditampilkan — `SQLite`, `LiteDB`, atau `IndexedDB`.
