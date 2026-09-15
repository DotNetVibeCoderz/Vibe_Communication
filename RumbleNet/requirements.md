Name: Rumble.Net

Deskripsi:
.NET SDK untuk Mumble, buat dengan melakukan rewrite libmumble (https://github.com/mumble-voip/libmumble) dengan Rust lalu expose ke .NET, maka hasilnya bisa berupa SDK yang benar-benar *enterprise-grade*: performa tinggi, aman memory, dan tetap nyaman dipakai developer .NET. Mari saya jelaskan detail tiap fitur dan alasannya.

---

 🎙️ Core Voice & Audio
- Voice packet handling → Rust unggul dalam *low-level packet processing*, sehingga decoding Opus/CELT bisa dilakukan dengan latency rendah. Jitter buffer dan packet loss concealment bisa dioptimalkan dengan algoritma berbasis *lock-free queues*.  
- Positional audio → penting untuk game/VR. Rust bisa memproses koordinat 3D secara paralel, lalu expose ke .NET API yang mudah dipakai.  
- Audio device integration → Rust core bisa langsung interfacing dengan WASAPI (Windows), ALSA/PulseAudio (Linux), CoreAudio (macOS). Wrapper .NET cukup menyediakan abstraksi sederhana.  
- Codec support → Opus sebagai default, CELT untuk kompatibilitas lama, PCM fallback. Rust punya binding resmi ke libopus sehingga aman dan cepat.

---

 🔐 Security & Networking
- TLS/DTLS encryption → Rust punya crate *rustls* yang lebih aman dibanding OpenSSL. Bisa langsung dipakai untuk voice + control channel.  
- UDP tunneling → Rust async runtime (tokio) bisa handle ribuan stream dengan overhead minimal.  
- Authentication → sertifikat X.509, password, atau token-based login. Rust core handle crypto, .NET wrapper expose API sederhana.  
- Connection resilience → auto-reconnect, heartbeat, keep-alive. Rust concurrency model memudahkan implementasi *supervisor tasks*.

---

 🛠️ Protocol & Control
- Channel management → join/leave, hierarki channel, permission. Rust core handle tree structure, .NET wrapper expose LINQ-friendly API.  
- User management → mute/deafen, kick/ban, role-based access. Bisa dihubungkan dengan sistem auth eksternal.  
- Text messaging → private chat, channel chat, broadcast. Rust core handle protokol, .NET wrapper integrasi dengan event system.  
- Server queries → statistik, user list, channel tree. Bisa di-cache di Rust untuk performa, lalu expose ke .NET.

---

 ⚡ Performance & Concurrency
- Async runtime → tokio/async-std untuk concurrency tinggi. Cocok untuk voice streaming.  
- Zero-copy buffers → audio buffer bisa diproses tanpa copy berulang, mengurangi latency.  
- Thread-safe interop → Rust menjamin safety, .NET wrapper cukup memanggil FFI dengan marshaling minimal.  
- Benchmark suite → penting untuk validasi performa. Bisa ukur latency < 50ms, throughput, CPU usage.

---

 📦 Developer Experience (.NET Layer)
- Idiomatic C# API → API berbasis event dan async/await, sehingga natural untuk developer .NET.  
- NuGet package → distribusi mudah, integrasi CI/CD.  
- Logging & diagnostics → integrasi dengan Microsoft.Extensions.Logging.  
- Cross-platform support → Windows, Linux, macOS, bahkan bisa dipakai di Unity.  
- Interop helpers → marshaling otomatis untuk audio buffer & events.

---

 🌐 Extensibility
- Plugin system → developer bisa bikin filter audio, efek suara, atau bot.  
- Event hooks → onUserJoin, onChannelMessage, onAudioFrame.  
- Bot framework integration → integrasi dengan AI/automation.  
- Game engine bridge → Unity/Unreal interop via .NET wrapper.

---

 🌐 Sample Apps
- RumbleApp → aplikasi seperti Mumla (https://github.com/quite/mumla) dibuat dengan MAUI Hybrid Blazor (multi-platform) tapi lebih keren dan lengkap, UI UX buat yang keren dan responsif dibantu skill 'frontend-design'  
- RumbleGallery → aplikasi code gallery berisi berbagai use case berbagai contoh penggunaan fitur SDK + tampilkan sample code-nya. Buat UI UX yang user friendly dan keren, dibuat dengan Avalonia UI (multi-platform)

---

 📌 Kesimpulan
- Rust core → performa tinggi, aman memory, concurrency optimal.  
- .NET wrapper → API bersih, mudah dipakai, distribusi via NuGet.  
- Fitur lengkap mencakup audio, protokol, security, extensibility, dan developer tooling.  

Notes:
- gunakan .NET 10
- optimasi koding agar dapat performa terbaik dan memory efisien
- gunakan naming convention standard c#
- readme dalam bahasan Indonesia dan English
- dokumentasi lengkap di folder docs
- Progress.md untuk tracking development, PLAN.md untuk roadmap pengembangan
- jika ada hal-hal yang penting perlu ditambahkan, silakan ditambahkan langsung biar lengkap.
- di aplikasi dan dokumentasi tambahkan informasi dibuat oleh Gravicode Studios dipimpin Kang Fadhil
- untuk publish nuget, api key ada di 'C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt'
