# Deployment

Bawaan pengembangan — SQLite, MemoryCache, berkas lokal — sengaja dipilih supaya `dotnet run` langsung jalan tanpa layanan apa pun. Untuk produksi, tukar tiga hal.

---

## 1. Database

```jsonc
"Database": {
  "Provider": "PostgreSql",
  "ConnectionString": "Host=db;Database=telepati;Username=telepati;Password=...",
  "SeedSampleData": false,          // matikan di produksi
  "EnableSensitiveDataLogging": false
}
```

Provider yang didukung: `Sqlite` · `SqlServer` · `MySql` · `PostgreSql`. Satu model EF Core melayani keempatnya; perbedaan per-engine ditangani di `TelepatiDbContext.ApplyProviderQuirks`.

### Skema

Sekarang `AutoMigrate` memakai `EnsureCreated` karena belum ada migrasi yang di-generate. Untuk produksi buat migrasi dulu:

```bash
dotnet ef migrations add Initial --project src/Telepati.Infrastructure --startup-project src/Telepati.Server
dotnet ef database update --project src/Telepati.Infrastructure --startup-project src/Telepati.Server
```

Setelah ada migrasi, `InitializeTelepatiDatabaseAsync` otomatis memakai `MigrateAsync` dan bukan `EnsureCreatedAsync`.

### Sharding

Untuk volume pesan yang besar:

```jsonc
"Sharding": {
  "Enabled": true,
  "Strategy": "ChatId",
  "Shards": [
    "Host=db-0;Database=telepati;...",
    "Host=db-1;Database=telepati;...",
    "Host=db-2;Database=telepati;..."
  ]
}
```

Pemilihan shard adalah hash stabil atas `ChatId`, jadi seluruh pesan satu percakapan selalu di node yang sama dan pembacaan tidak pernah fan-out. Menambah shard **mengubah pemetaan** — rencanakan jumlahnya di awal, atau siapkan migrasi data.

---

## 2. Cache

```jsonc
"Cache": {
  "Provider": "Redis",
  "ConnectionString": "redis:6379",
  "InstanceName": "telepati:"
}
```

Redis wajib begitu server dijalankan lebih dari satu instance — MemoryCache tidak dibagi antar-proses.

> Menjalankan banyak instance juga membutuhkan **backplane SignalR** (`AddStackExchangeRedis` pada `AddSignalR`) agar push sampai ke pengguna yang terhubung ke instance lain. Ini belum dikonfigurasi; lihat Plan.md.

---

## 3. Storage

```jsonc
// Amazon S3
"Storage": {
  "Provider": "S3",
  "ContainerName": "telepati-media",
  "Region": "ap-southeast-1",
  "AccessKey": "...",
  "SecretKey": "...",
  "SignedUrlTtlMinutes": 60
}

// MinIO — atau layanan S3-compatible lain
"Storage": {
  "Provider": "MinIO",
  "Endpoint": "minio:9000",
  "ContainerName": "telepati",
  "AccessKey": "...",
  "SecretKey": "...",
  "UseSsl": false
}

// Azure Blob
"Storage": {
  "Provider": "AzureBlob",
  "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...",
  "ContainerName": "telepati"
}
```

Provider cloud mengembalikan URL bertanda tangan berumur pendek; container-nya tetap privat. Hanya `FileSystem` yang disajikan sebagai berkas statis oleh server.

---

## Keamanan sebelum go-live

Wajib:

```bash
export Telepati__Security__JwtSecret="$(openssl rand -base64 48)"
export Telepati__Security__AllowedCorsOrigins__0="https://telepati.example.com"
export Telepati__Database__SeedSampleData="false"
```

Sangat disarankan:

| Pengaturan | Nilai | Alasan |
|------------|-------|--------|
| `Security:RequireTwoFactorForAdmins` | `true` | Akun admin adalah target bernilai tinggi |
| `Bot:EnableCodeExecution` | `false` | Kecuali server berjalan di container tanpa hak istimewa |
| `Bot:AllowPackageInstall` | `false` | Sudah mati secara bawaan; biarkan |
| `Features:IceServers` | tambahkan TURN | STUN saja gagal di NAT simetris — umum pada jaringan seluler |

Ganti password semua akun demo, atau jangan seed sama sekali.

---

## Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Telepati.Server -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Eksekusi skrip bot dibatasi path, bukan hak OS. Pengguna tanpa privilese
# adalah lapisan kedua yang membuatnya aman dijalankan.
RUN useradd --create-home --shell /usr/sbin/nologin telepati
USER telepati

ENTRYPOINT ["dotnet", "Telepati.Server.dll"]
```

```yaml
# compose.yaml
services:
  db:
    image: postgres:17
    environment:
      POSTGRES_DB: telepati
      POSTGRES_USER: telepati
      POSTGRES_PASSWORD: ${DB_PASSWORD}
    volumes: [pgdata:/var/lib/postgresql/data]

  cache:
    image: redis:7-alpine

  storage:
    image: minio/minio
    command: server /data --console-address ":9001"
    environment:
      MINIO_ROOT_USER: ${MINIO_USER}
      MINIO_ROOT_PASSWORD: ${MINIO_PASSWORD}
    volumes: [miniodata:/data]

  server:
    build: .
    depends_on: [db, cache, storage]
    ports: ["8080:8080", "8081:8081"]
    environment:
      Telepati__Database__Provider: PostgreSql
      Telepati__Database__ConnectionString: "Host=db;Database=telepati;Username=telepati;Password=${DB_PASSWORD}"
      Telepati__Cache__Provider: Redis
      Telepati__Cache__ConnectionString: "cache:6379"
      Telepati__Storage__Provider: MinIO
      Telepati__Storage__Endpoint: "storage:9000"
      Telepati__Storage__AccessKey: ${MINIO_USER}
      Telepati__Storage__SecretKey: ${MINIO_PASSWORD}
      Telepati__Storage__UseSsl: "false"
      Telepati__Security__JwtSecret: ${JWT_SECRET}
      Telepati__Database__SeedSampleData: "false"

volumes:
  pgdata:
  miniodata:
```

---

## Reverse proxy

gRPC butuh HTTP/2 dari ujung ke ujung. Untuk nginx:

```nginx
server {
    listen 443 ssl http2;
    server_name telepati.example.com;

    # REST, Swagger, dan berkas statis
    location / {
        proxy_pass http://server:8080;
        proxy_http_version 1.1;
    }

    # SignalR butuh upgrade WebSocket
    location /hubs/ {
        proxy_pass http://server:8080;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
    }

    # gRPC memakai protokol dan port terpisah
    location /telepati.TelepatiService/ {
        grpc_pass grpc://server:8081;
    }
}
```

Batas waktu baca yang panjang pada `/hubs/` penting — tanpa itu proxy memutus koneksi realtime yang sedang menganggur.

---

## Aplikasi klien

Ketiga klien membaca `TelepatiClient:ServerUrl`:

```jsonc
"TelepatiClient": {
  "ServerUrl": "https://telepati.example.com",
  "Transport": "SignalR"
}
```

Untuk mobile, ingat bahwa emulator Android memetakan host ke `10.0.2.2`. Untuk build rilis, isi alamat produksi sebenarnya.

`Telepati.Client.Core` menerima sertifikat apa pun agar sertifikat pengembangan yang ditandatangani sendiri bisa dipakai. **Hapus `DangerousAcceptAnyServerCertificateValidator` di `DependencyInjection.cs` sebelum merilis klien** — di produksi sertifikatnya asli dan validasi harus menyala.
