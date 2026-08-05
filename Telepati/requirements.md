Nama: Telepati

Deskripsi:
aplikasi messenger modern (mirip WhatsApp dan Telegram) yang mendukung web, desktop dan mobile apps, berikut adalah rangkuman fitur yang bisa jadi acuan:

 🔑 Core Features
- Real-time Messaging: Chat instan berbasis WebSocket/SignalR, Grpc, Rest Api (bisa dikonfigurasi dari client app)
- Group Chat: Grup dengan admin, moderator, dan kontrol anggota.
- File Sharing: Kirim dokumen, gambar, video, audio.  
- Voice & Video Call: Panggilan langsung via WebRTC.  
- Emoji & Stickers: Ekspresi visual untuk percakapan.  
- User Status (text, image, video)
- User Profile 

 💬 Communication & Engagement
- Broadcast Message: Kirim pesan ke banyak kontak sekaligus.  
- Pinned Messages: Tandai pesan penting di chat/grup.  
- Reply & Forward: Balas pesan tertentu atau teruskan ke chat lain.  
- Mentions & Tags: Notifikasi khusus saat nama disebut.  

 🛡️ Privacy & Security
- End-to-End Encryption: Jaminan keamanan pesan. (opsional - bisa di konfigurasi dari appsetting/UI admin) 
- Two-Factor Authentication: Login lebih aman. (opsional) 
- Block & Report: Proteksi dari spam atau abuse.  
- Session Management: Kontrol perangkat login aktif.  

 🌟 Advanced Features
- Bot Integration: Tambahkan bot untuk otomatisasi (polling, reminder, dll).  
- Channel Broadcasting: Publikasi konten ke banyak subscriber.  
- Cloud Sync: Semua pesan tersimpan di server, bisa diakses dari perangkat manapun.  
- Multi-Device Support: Login di beberapa device sekaligus.  

 📊 Analytics & Admin
- User Activity Logs: Catatan login, pesan, interaksi.  
- Group Insights: Statistik aktivitas grup.  
- Message Delivery Reports: Status terkirim, dibaca, gagal.  
- Role Management: Admin, moderator, member dengan hak akses berbeda.  
- Backup data to SQL file
- Advance Dashboard untuk realtime monitoring bagi admin
- Setting Page (pengaturan aplikasi)

Contacts
- search and add user by email / phone number / username / nearby (adjustable 5 - 100km radius) - jika diberikan permission di setting client app
- share contact with QR code
- add contact from QRCode (scan with camera / select image)
- add from phone contacts (if user give permission)

---

Chat Bot Temen Ngobrol
  - Nama 'Kang Bacot' (@bacot)
  - Chat bot yang bisa di ajak ngobrol langsung atau di masukan ke grup
  - Jika di grup harus di mention untuk ngobrol
  - System Prompt (persona), temperature, model dan setting lainnya di simpan di appsetting
  - Menggunakan Semantic Kernel Library dengan dukungan model: Open AI, Anthropic, Gemini, Ollama (bisa pilih)
  - Tambahkan beberapa common functions (kernel functions) yang diperlukan termasuk query ke tavily (search internet), scrap page url, baca file dari url, cek tanggal, Waktu, math calculation, dan beberapa function yang diperlukan lainnya
  - Tambahkan functions untuk baca lampiran dokumen/gambar/dsb yang dikirim oleh user, bisa download file dari url, bisa memanfaatkan command line / powershell / python / .NET untuk menyelesaikan instruksi user tertentu yang membutuhkan code/script execution, misalnya: membuat aplikasi, menghitung, memprediksi, membuat/mengedit dokumen, membuat gambar, mengedit dokumen, analisa data, membuat chart, dsb. Semua dilakukan di folder khusus (workspace) yang bisa di konfigurasi dari appsetting atau UI admin. mampu menginstall library yang dibutuhkan jika diperlukan. Jika ingin mengirim file/dokumen hasil olahan ke user bisa upload ke storage (jika bentuknya folder di zip dulu), dan kirim url untuk download-nya ke user. 
  - Bisa render chat thread dengan mark down dengan baik ke html (baik table, media (image, video, audio), code, dan lainnya dengan baik)
  - Sesi chat di isolasi per user (direct message), dan per grup (jika bot dimasukan dalam grup). 
  - Sesi/History chat bisa di reset dengan mengetik (system prompt bot juga akan kembali ke default): #resetbot
  - Ada fitur auto compact jika sudah mau habis context window
  - Persona/System prompt bot per sesi bisa di override dengan mengetik di chat: #newpersona [persona/system prompt baru] 

---

Notes:
- dibuat dengan .NET 10, aplikasi admin di buat dengan blazor server, Web Api dibuat dengan ASP.NET Core (support akses via: Rest Api (Minimal API), SignalR dan Grpc), aplikasi web messengger dibuat dengan blazor server, dan aplikasi mobile messengger dibuat dengan Maui Blazor target semua platform mobile, aplikasi desktop messengger dengan Avalonia + Blazor target multi-platform desktop. 
- Desain UI dan UX yang modern, fresh, fun dibantu skill frontend-design dengan dukungan dark theme/light, micro-interaction. Ada tema tampilan khusus untuk beberapa hari khusus misal lebaran, tahun baru, dsb (bisa di aktifkan dari web admin) - bisa ditambahkan juga lewat master data, dengan memilih kombinasi warna theme dan icon.
- Semua konfigurasi disimpan di appsetting dan bisa diubah dari aplikasi admin
- Tambahkan readme.md (English dan Bahasa Indonesia), sertakan screenshot
- Database support SQLite (dev), SQLServer, MySQL, Postgre (support sharding untuk skalabilitas)
- Cache: MemoryCache (dev), Redis (production) 
- Storage Support: FileSystem (dev), AzureBlob, S3, MinIO
- Tambahkan dokumentasi lengkap di folder docs (sertakan screenshot)
- Buatkan banyak sample data, dan user
- optimasi kode agar aplikasi cepat dan ringan
- REST API untuk Web Admin: Integrasi dengan aplikasi eksternal dengan Min API dan swagger
- Untuk komunikasi antara client app (web, desktop, mobile) dan server (web api) bisa beberapa opsi: signalR (default), Grpc, RestApi. ini bisa dikonfigurasi dari masing-masing client app (web, desktop, mobile)
- Tambahkan info di dokumentasi dan app: dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil
- Plan.md untuk roadmap pengembangan, Progress.md untuk tracking development