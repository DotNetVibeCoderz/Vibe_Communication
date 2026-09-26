# Build dan packaging

🇬🇧 [English](../en/building.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

## Kebutuhan

| Tool | Versi | Untuk |
| --- | --- | --- |
| .NET SDK | 10.0 (lihat `global.json`) | SDK, sample, test |
| Rust | 1.80 atau lebih baru, dengan `cargo` | engine native |
| Toolchain C | MSVC build tools (Windows), clang/gcc (Linux), Xcode CLT (macOS) | linking library native |
| CMake | 3.x atau lebih baru | membangun libopus bawaan (codec Opus); `cargo build --no-default-features` tanpa Opus |

Workflow CI juga menjalankan benchmark dan uji interop browser: Chrome dan Firefox melakukan panggilan sungguhan lewat `samples/VoipNet.WebPhone`, dan build gagal bila audio terenkripsi tidak mengalir dua arah.

## Struktur repositori

```
native/voipnet-core     Engine Rust (cdylib + rlib), unit test dan test loopback
src/                    VoipNet.Core, .Audio, .Video, .AI, .Enterprise
tools/                  VoipNet.Cli (voipnet), VoipNet.DocShots
samples/                Softphone, Gallery (Avalonia) · CallCenter, IvrStudio, WebPhone, Meeting (Blazor) · RealtimeAgent
tests/VoipNet.Tests     Test end-to-end xUnit v3 lewat loopback
docs/en, docs/id        dokumentasi
build/                  build.ps1, build.sh
```

## Build semuanya

```powershell
./build/build.ps1                       # engine, solusi, semua test
./build/build.ps1 -SkipTests
./build/build.ps1 -Pack                 # paket NuGet di artifacts/packages
```

```bash
./build/build.sh --pack                 # Linux / macOS
```

## Langkah demi langkah

```bash
cd native
cargo build --release                   # menghasilkan target/release/voipnet_core.{dll,so,dylib}
cargo test --lib                        # 53 test engine termasuk panggilan nyata via loopback
cd ..
dotnet build Voip.Net.slnx -c Release
dotnet run --project tests/VoipNet.Tests -c Release    # host test xUnit v3
```

`VoipNet.Core` menyalin library hasil build ke output setiap proyek, sehingga sample dan test bisa langsung dijalankan dari repositori.

> Di .NET 10 SDK, jalankan test xUnit v3 dengan `dotnet run --project tests/VoipNet.Tests` (proyek test berupa executable yang meng-host dirinya sendiri).

### Test yang memakai layanan nyata

`LiveLlmTests` memanggil Azure OpenAI dan DeepSeek bila file kunci tersedia, dan dilewati bila tidak. Arahkan `VOIPNET_TEST_KEYS` ke file teks dengan bagian seperti:

```
Azure OpenAI
apikey: …
endpoint: https://resource-anda.openai.azure.com/
model: gpt-5-mini

DeepSeek
Deep Seek Api Key: …
Deep Seek Endpoint: https://api.deepseek.com
Model Id: deepseek-chat
```

Jangan pernah meng-commit file ini; `.gitignore` sudah mengecualikan `testkey.txt`.

## Library native lintas platform

Paket menyertakan library native apa pun yang ada saat packing:

| Runtime | Sumber |
| --- | --- |
| platform host | `native/target/release` |
| runtime lain | `artifacts/native/<rid>/native/` |

Build target tambahan dengan rustup dan biarkan skrip menyiapkannya:

```powershell
rustup target add aarch64-pc-windows-msvc
./build/build.ps1 -Targets x86_64-pc-windows-msvc,aarch64-pc-windows-msvc -Pack
```

Library Linux dan macOS paling baik dibangun di sistem tersebut (atau runner CI) dengan `build.sh`, yang menaruh library host di `artifacts/native/<rid>/native`; gabungkan folder `artifacts/native` sebelum `dotnet pack` terakhir.

Runtime .NET menemukan library lewat probing standar `runtimes/<rid>/native`; `NativeMethods` juga mencari di folder aplikasi dan di folder `native/target/release` repositori.

## Continuous integration dan publikasi ke NuGet

Workflow repositori `.github/workflows/voipnet-ci.yml` (di [Vibe_Communication](https://github.com/DotNetVibeCoderz/Vibe_Communication)) berjalan pada setiap perubahan di bawah `VoipNet/`:

1. **native** — membangun dan menguji engine untuk win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, dan osx-arm64;
2. **dotnet** — menaruh library yang sesuai di `src/VoipNet.Core/runtimes/<rid>/native`, membangun solusi, dan menjalankan test di Windows, Linux, dan macOS;
3. **pack** — menaruh keenam library, membuat enam paket, dan memeriksa bahwa `VoipNet.Core` berisi tepat satu library `runtimes/<rid>/native/` per runtime;
4. **publish** — push ke nuget.org memakai secret repositori `NUGET_API_KEY`.

Publikasi hanya terjadi untuk tag rilis atau run manual dengan opsi *publish* dicentang:

```bash
git tag voipnet-v1.0.0          # versi diambil dari tag
git push origin voipnet-v1.0.0
```

Untuk publikasi dari komputer lokal:

```bash
dotnet nuget push "artifacts/packages/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json --skip-duplicate
```

Simpan API key di luar repositori (secret CI atau file kredensial).

## Catatan performa

- Build release engine memakai `lto = "fat"`, `codegen-units = 1`, `panic = "abort"`.
- Callback audio memberikan pointer ke buffer engine: handler menerima `ReadOnlySpan<short>` tanpa alokasi. Salin hanya yang perlu disimpan.
- Jalur penerimaan memakai ulang buffer; jitter buffer mem-pool vektor payload; G.711 memakai tabel lookup; codec berjalan dalam loop yang divektorisasi compiler.
- `ReadAudioAsync` mengalokasikan satu array per frame demi kemudahan; gunakan `AudioReceived` di jalur panas.

### Benchmark

```bash
cd native && cargo bench --bench media
pwsh build/bench-report.ps1            # tabel, dibandingkan dengan benchmarks/baseline.json
pwsh build/bench-report.ps1 -Update    # jadikan angka saat ini sebagai baseline
```

CI menjalankan benchmark pada setiap build dan menampilkan perbandingannya di ringkasan job. Hasil yang
lebih dari 50% lebih lambat dari baseline hanya diberi peringatan, bukan menggagalkan build: runner
bersama cukup berisik sehingga ambang yang lebih ketat akan sering salah alarm. Perbarui baseline secara
sengaja, di mesin yang sedang senggang, ketika suatu perubahan memang dimaksudkan mengubah angkanya.

### Nightly: provider langsung

`voipnet-nightly.yml` menjalankan test yang benar-benar memanggil layanan nyata — LLM, pengenalan dan
sintesis suara, serta satu panggilan lewat keduanya — setiap malam pukul 18:00 UTC, dan sewaktu-waktu
dari tab Actions. Test itu butuh kunci, yang disimpan repositori sebagai satu secret bernama
`VOIPNET_TEST_KEYS` dengan bentuk persis seperti `testkey.txt` lokal: bagian dipisah baris kosong,
berisi `nama: nilai`. Job menuliskannya ke file lalu mengarahkan `VOIPNET_TEST_KEYS` ke situ.

Tanpa secret itu setiap test langsung melewati dirinya sendiri, jadi job tetap hijau dan ringkasannya
menyebutkan hal itu alih-alih berpura-pura provider sudah dihubungi. Lognya tetap dilampirkan ke run.

## Container dan Kubernetes

`docker/Dockerfile` membangun engine Rust dan satu aplikasi .NET menjadi satu image. Aplikasi mana
yang dibangun ditentukan lewat build argument, jadi file yang sama menghasilkan CLI, sebuah sample,
atau layanan buatan Anda sendiri:

```bash
cd VoipNet
docker build -f docker/Dockerfile -t voipnet/cli .
docker build -f docker/Dockerfile -t voipnet/meeting \
  --build-arg PROJECT=samples/VoipNet.Meeting --build-arg ENTRY=VoipNet.Meeting.dll .
```

Bagian yang merepotkan saat menjalankan SIP di container adalah medianya: RTP memakai rentang port
UDP, dan alamat yang ditulis engine ke SDP harus alamat yang bisa dijangkau lawan bicara. Jaringan
host adalah jawaban paling sederhana:

```bash
docker run --rm --network host voipnet/cli sip listen --echo
docker run --rm --network host -p 8080:8080 voipnet/meeting
```

Tanpa itu, publikasikan port signaling bersama rentang RTP, dan beri tahu engine alamat yang harus
dipakai penelepon (`PublicAddress`, atau `StunServer` / `TurnServer` di belakang NAT):

```bash
docker run --rm -p 5060:5060/udp -p 10000-10100:10000-10100/udp voipnet/cli sip listen --echo
```

Chart di `deploy/helm/voipnet` men-deploy satu aplikasi dengan cara yang sama:

```bash
helm install room deploy/helm/voipnet \
  --set image.repository=voipnet/meeting \
  --set rtp.portMin=10000 --set rtp.portMax=10100
```

Secara bawaan chart memakai `hostNetwork: true`, karena Service Kubernetes meneruskan satu port
sekaligus sedangkan media butuh rentang. Artinya satu replica per node — dua pod di satu node akan
berebut port yang sama — jadi skalakan dengan menambah node, atau pasang server TURN di depannya lalu
matikan jaringan host. Kredensial sebaiknya disimpan di secret: `--set envFromSecrets={voipnet-credentials}`.
