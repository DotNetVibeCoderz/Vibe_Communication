# Konfigurasi

Semua pengaturan hidup di bawah bagian `Telepati` pada `appsettings.json`. Setiap kunci juga bisa diubah saat aplikasi berjalan dari **Admin → Pengaturan**; nilai yang disimpan di sana menimpa berkas.

Alamatnya selalu `Telepati:Bagian:Properti`, misalnya `Telepati:Bot:Provider`.

---

## Branding

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `AppName` | `Telepati` | Muncul di judul halaman, Swagger, dan aplikasi |
| `Tagline` | `Ngobrol seru, tanpa jeda.` | Ditampilkan di layar masuk |
| `Company` | `Gravicode Studios` | Atribusi di aplikasi dan dokumentasi |
| `Leader` | `Kang Fadhil` | Atribusi pimpinan |
| `SupportEmail` | `support@telepati.app` | Kontak di Swagger |

## Database

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `Provider` | `Sqlite` | `Sqlite` · `SqlServer` · `MySql` · `PostgreSql` |
| `ConnectionString` | `Data Source=telepati.db` | — |
| `AutoMigrate` | `true` | Membuat skema saat start |
| `SeedSampleData` | `true` | Isi data contoh bila database kosong |
| `EnableSensitiveDataLogging` | `false` | Jangan diaktifkan di produksi |
| `CommandTimeoutSeconds` | `30` | — |
| `Sharding:Enabled` | `false` | — |
| `Sharding:Shards` | `[]` | Daftar connection string; indeksnya adalah nomor shard |
| `Sharding:Strategy` | `ChatId` | `ChatId` menjaga satu percakapan di satu node |

## Cache

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `Provider` | `Memory` | `Memory` untuk pengembangan, `Redis` untuk produksi |
| `ConnectionString` | `localhost:6379` | Dipakai saat provider Redis |
| `InstanceName` | `telepati:` | Prefiks kunci Redis |
| `DefaultTtlSeconds` | `300` | — |
| `PresenceTtlSeconds` | `60` | — |

## Storage

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `Provider` | `FileSystem` | `FileSystem` · `AzureBlob` · `S3` · `MinIO` |
| `RootPath` | `storage` | Hanya untuk FileSystem |
| `PublicBaseUrl` | `/files` | Prefiks URL statis untuk FileSystem |
| `ContainerName` | `telepati` | Nama container / bucket |
| `ConnectionString` | — | Azure Blob |
| `Endpoint` | — | MinIO, atau layanan S3-compatible |
| `AccessKey` / `SecretKey` | — | S3 dan MinIO |
| `Region` | `us-east-1` | S3 |
| `UseSsl` | `true` | MinIO |
| `SignedUrlTtlMinutes` | `60` | Masa berlaku URL bertanda tangan |

## Security

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `JwtSecret` | *(nilai pengembangan)* | **Wajib diganti di produksi.** Minimal 32 karakter |
| `JwtIssuer` / `JwtAudience` | `Telepati` / `TelepatiClients` | — |
| `AccessTokenMinutes` | `60` | — |
| `RefreshTokenDays` | `30` | — |
| `EnableEndToEndEncryption` | `false` | Menyalakan enkripsi untuk chat yang **baru dibuat** |
| `EnableTwoFactor` | `true` | Saat `false`, pendaftaran 2FA ditolak apa pun isi profil pengguna |
| `RequireTwoFactorForAdmins` | `false` | — |
| `MaxActiveSessions` | `10` | Sesi terlama dicabut saat batas terlampaui |
| `MaxFailedLoginAttempts` | `5` | — |
| `LockoutMinutes` | `15` | — |
| `AllowedCorsOrigins` | `[]` | Daftar kosong berarti mode pengembangan: semua origin diterima |

## Features

Saklar per fitur. Semua bawaannya `true` kecuali disebut lain.

`EnableVoiceCall` · `EnableVideoCall` · `EnableStatus` · `EnableChannels` · `EnableBroadcast` · `EnableBots` · `EnableNearbySearch` · `EnableStickers` · `EnableGrpcTransport` · `EnableRestTransport` · `EnableSignalRTransport`

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `IceServers` | dua STUN Google | Untuk produksi tambahkan TURN — STUN saja gagal di NAT simetris |
| `MinNearbyRadiusKm` | `5` | Batas bawah slider di klien |
| `MaxNearbyRadiusKm` | `100` | Batas atas |

## Bot — Kang Bacot

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `Enabled` | `true` | — |
| `Handle` | `bacot` | Yang di-mention di grup: `@bacot` |
| `DisplayName` | `Kang Bacot` | — |
| `Provider` | `Ollama` | `OpenAI` · `Anthropic` · `Gemini` · `Ollama` |
| `Model` | `llama3.2` | Nama model sesuai provider |
| `ApiKey` | — | Kosong untuk Ollama |
| `Endpoint` | `http://localhost:11434` | Hanya untuk Ollama |
| `Temperature` | `0.8` | — |
| `TopP` | `0.95` | — |
| `MaxTokens` | `2048` | — |
| `SystemPrompt` | *(persona Indonesia)* | Persona bawaan |
| `ContextWindowTokens` | `32000` | Sesuaikan dengan model |
| `AutoCompactThreshold` | `0.75` | Ringkas saat pemakaian melewati porsi ini |
| `MaxTurnsKeptVerbatim` | `20` | Giliran terbaru yang tidak diringkas |
| `WorkspacePath` | `workspace` | Sandbox untuk berkas dan skrip |
| `EnableCodeExecution` | `true` | — |
| `AllowPackageInstall` | `false` | Sengaja mati secara bawaan |
| `ExecutionTimeoutSeconds` | `120` | Skrip yang lewat batas ini dibunuh |
| `AllowedExecutors` | `powershell, python, dotnet, cmd, node` | Daftar izin, bukan daftar larangan. `bash` sengaja tidak ikut — tambahkan sendiri bila server memang menyediakannya |
| `TavilyApiKey` | — | Tanpa ini pencarian internet tidak aktif |
| `EnableWebSearch` | `true` | — |
| `MaxFunctionCallsPerTurn` | `8` | — |

## Theme

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `DefaultMode` | `system` | `light` · `dark` · `system` |
| `DefaultThemeName` | `Telepati Classic` | — |
| `EnableSeasonalThemes` | `true` | Saat `true`, tema musiman dalam rentang tanggalnya mengalahkan tema aktif |
| `AllowUserThemeOverride` | `true` | — |
| `EnableMicroInteractions` | `true` | — |

## Limits

| Kunci | Bawaan |
|-------|--------|
| `MaxUploadBytes` | `104857600` (100 MB) |
| `MaxGroupMembers` | `500` |
| `MaxBroadcastRecipients` | `1000` |
| `MaxPinnedMessages` | `5` |
| `MessagePageSize` | `50` |
| `MaxMessageLength` | `8000` |
| `StatusRetentionHours` | `24` |

---

## Konfigurasi klien

Aplikasi klien membaca bagian terpisah, `TelepatiClient`:

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `ServerUrl` | `https://localhost:7180` | Untuk emulator Android pakai `https://10.0.2.2:7180` |
| `Transport` | `SignalR` | `SignalR` · `Grpc` · `Rest` — bisa diganti dari halaman Pengaturan |
| `AutoReconnect` | `true` | — |
| `ReconnectDelaySeconds` | `3` | — |
| `PollIntervalSeconds` | `5` | Hanya dipakai transport REST |
| `ShareLocation` | `false` | Izin pencarian sekitar |
| `AllowContactImport` | `false` | Izin impor buku telepon |
| `EnableNotificationSound` | `true` | — |
| `ThemeMode` | `system` | — |

Bawaan per aplikasi berbeda dengan sengaja: web memakai **SignalR** (paling responsif di browser), desktop dan mobile memakai **gRPC** (framing biner, lebih hemat pada koneksi berkuota).

### Penyimpanan lokal — `TelepatiClient:LocalDatabase`

Hanya dipakai desktop dan mobile; web menyimpan di IndexedDB browser dan mengabaikan bagian ini.

| Kunci | Bawaan | Keterangan |
|-------|--------|------------|
| `Provider` | `Sqlite` | `Sqlite` · `LiteDb` · `None` — lihat [docs/client-storage.md](client-storage.md) |
| `Path` | *(kosong)* | Folder berkas basis data; kosong berarti folder app-data platform |
| `Enabled` | `true` | Mematikannya membuat setiap pembacaan langsung ke API |
| `FreshnessSeconds` | `60` | Daftar yang lebih tua dari ini disegarkan sebelum ditampilkan |
| `KeepMessagesPerChat` | `300` | Pesan terbaru per percakapan yang tidak pernah dipangkas |
| `RetentionDays` | `30` | Selebihnya dipangkas setelah sekian hari |

SQLite adalah bawaan karena terukur **13× lebih cepat membaca dan 45× lebih cepat menulis**
daripada LiteDB pada beban yang sama ([Performance.md](../Performance.md)).

---

## Rahasia

Jangan pernah menaruh kredensial produksi di `appsettings.json` yang ter-commit. Pilihannya:

```bash
# Pengembangan
dotnet user-secrets set "Telepati:Security:JwtSecret" "..." --project src/Telepati.Server

# Produksi — variabel lingkungan, garis bawah ganda menggantikan titik dua
export Telepati__Security__JwtSecret="..."
export Telepati__Bot__ApiKey="..."
```

Kunci yang mengandung `Secret`, `ApiKey`, `Password`, atau `ConnectionString` otomatis ditandai rahasia: nilainya tidak pernah dikirim balik ke form admin, dan mengosongkan kolomnya berarti "biarkan seperti semula".
