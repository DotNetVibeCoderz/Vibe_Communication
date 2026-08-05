# Tangkapan Layar

Semua gambar di folder ini diambil dari aplikasi yang benar-benar berjalan dengan data contoh
bawaan. Tidak ada mockup.

## Daftar

| Berkas | Isi |
|--------|-----|
| `01-login.png` | Layar masuk dengan animasi gelombang |
| `02-chat-terang.png` | Percakapan aktif, mode terang |
| `03-chat-gelap.png` | Percakapan yang sama, mode gelap |
| `04-grup.png` | Grup dengan beberapa pengirim |
| `05-bot.png` | Jawaban Kang Bacot dalam Markdown |
| `06-kontak.png` | Daftar kontak |
| `07-kontak-sekitar.png` | Pencarian orang di sekitar dengan slider radius |
| `08-status.png` | Umpan status, dikelompokkan per orang |
| `09-status-viewer.png` | Status teks terbuka: bilah segmen, navigasi, kanvas warna |
| `09b-status-foto.png` | Status gambar dalam pemutar yang sama |
| `10-pengaturan.png` | Pengaturan klien: transport, tampilan, privasi |
| `11-admin-dashboard.png` | Dashboard admin dengan kartu statistik dan grafik tren |
| `12-admin-pengguna.png` | Tabel pengguna dengan pengubah peran |
| `13-admin-tema.png` | Galeri tema dan editor tema musiman |
| `14-admin-pengaturan.png` | Editor pengaturan aplikasi |
| `15-admin-percakapan.png` | Daftar percakapan dan statistik grup |
| `16-admin-skills.png` | Galeri skill: yang terpasang, izin skrip, sumber |
| `17-admin-mcp.png` | Galeri MCP: katalog per kategori dengan uji koneksi |
| `18-admin-skills-browse.png` | Menelusuri repositori skill sebelum memasang |
| `19-swagger.png` | Swagger UI |
| `20-mobile.png` | Tata letak satu panel pada lebar ponsel (412 px) |

## Mengambil ulang

Tangkapan layar dihasilkan oleh [`tools/Telepati.Shots`](../../tools/Telepati.Shots), yang
mengendalikan Chromium headless lewat Playwright. Jalankan ketiga aplikasi lebih dulu:

```bash
dotnet run --project src/Telepati.Server     # terminal 1
dotnet run --project src/Telepati.Web        # terminal 2
dotnet run --project src/Telepati.Admin      # terminal 3
dotnet run --project tools/Telepati.Shots    # terminal 4
```

Aplikasi yang tidak berjalan akan **dilewati dan dilaporkan**, bukan dipalsukan.

Alamat bisa diarahkan lewat variabel lingkungan:

```bash
TELEPATI_WEB=https://localhost:7200 \
TELEPATI_ADMIN=https://localhost:7210 \
TELEPATI_API=https://localhost:7180 \
dotnet run --project tools/Telepati.Shots
```

## Catatan

- Viewport desktop dikunci di **1440 × 900** dengan `deviceScaleFactor: 2`, dan viewport ponsel
  di **412 × 900** dengan `deviceScaleFactor: 3`, supaya semua gambar konsisten.
- Data contoh memakai seed acak yang tetap, jadi percakapan yang muncul sama di setiap mesin —
  tangkapan layar bisa direproduksi.
- Alur web dijalankan dalam **satu sesi tanpa navigasi ulang**. Berpindah URL akan me-restart
  circuit Blazor Server dan menutup percakapan yang sedang dibuka.
- Untuk memotret tema musiman di luar tanggalnya, buka **Admin → Tema** dan aktifkan manual.
- Status bergambar memakai berkas yang benar-benar diunggah lewat provider storage saat seeding,
  jadi `09b` juga membuktikan jalur media dari server sampai ke browser masih utuh.
- Jangan menampilkan token, kunci API, atau alamat email asli dalam tangkapan layar.
