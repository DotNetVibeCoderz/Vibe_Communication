# Kang Bacot 🤖

Bot temen ngobrol Telepati. Dibangun di atas **Semantic Kernel**, jadi model penyedianya bisa diganti tanpa menyentuh kode.

---

![Kang Bacot menjawab dalam Markdown](screenshots/05-bot.png)

## Cara memanggilnya

| Tempat | Cara |
|--------|------|
| **Chat langsung** | Cari `@bacot` di daftar kontak, lalu kirim pesan seperti biasa |
| **Grup** | Sebut `@bacot` di dalam pesan — tanpa mention, dia diam |

Perilaku di grup memang begitu: bot yang menjawab setiap pesan akan mengacaukan ruang obrolan.

## Perintah

| Perintah | Yang terjadi |
|----------|--------------|
| `#resetbot` | Mengosongkan riwayat sesi **dan** mengembalikan persona ke bawaan |
| `#newpersona [teks]` | Mengganti system prompt untuk sesi ini saja |

Contoh:

```
#newpersona Kamu adalah asisten riset yang formal dan selalu menyertakan sumber.
```

Ketik `#resetbot` untuk kembali ke Kang Bacot yang santai.

## Isolasi sesi

Riwayat percakapan dipisah dengan tegas:

- **Chat langsung** — satu sesi per pengguna. Percakapanmu tidak terlihat oleh siapa pun.
- **Grup** — satu sesi per grup, dibagi semua anggotanya. Bot mengingat konteks ruangan itu.

Di database ini terlihat sebagai `BotSession` yang unik pada `(ChatId, UserId, BotHandle)`: sesi grup membawa `ChatId`, sesi langsung membawa `UserId`, dan keduanya tidak pernah berbagi riwayat.

## Auto-compact

Percakapan panjang akhirnya melampaui context window model. Saat pemakaian melewati `AutoCompactThreshold` (bawaan 75%), Kang Bacot:

1. Mengambil giliran-giliran terlama, menyisakan `MaxTurnsKeptVerbatim` (bawaan 20) yang terbaru
2. Meminta model meringkasnya menjadi satu paragraf padat — fakta, keputusan, nama, angka, preferensi
3. Menandai giliran lama sebagai tidak aktif dan menyimpan ringkasannya

Ringkasan itu diputar ulang sebagai satu pesan sistem di giliran berikutnya. Kalau peringkasan gagal, giliran lama tetap dibuang — lebih baik kehilangan detail daripada menabrak batas context.

---

## Kernel functions

Bot memanggil fungsi ini sendiri saat memang membantu.

### Waktu

| Fungsi | Kegunaan |
|--------|----------|
| `Time.GetCurrentDateTime` | Tanggal dan jam sekarang, bawaannya WIB |
| `Time.GetDaysBetween` | Selisih hari antara dua tanggal |
| `Time.AddToDate` | Tambah atau kurangi hari dari sebuah tanggal |

### Hitungan

| Fungsi | Kegunaan |
|--------|----------|
| `Math.Calculate` | Ekspresi aritmetika, termasuk kurung |
| `Math.Statistics` | Jumlah, rata-rata, min, maks, median dari deret angka |
| `Math.Percentage` | Diskon, pajak, dan sejenisnya |

### Internet

| Fungsi | Kegunaan |
|--------|----------|
| `Web.Search` | Pencarian lewat Tavily — butuh `TavilyApiKey` |
| `Web.ScrapePage` | Membaca isi teks sebuah halaman |
| `Web.ReadTextFromUrl` | Mengambil berkas teks (txt, csv, json, md, xml) |

Hasilnya selalu dipotong sebelum masuk ke prompt; satu halaman utuh bisa menghabiskan seluruh context window.

### Berkas

| Fungsi | Kegunaan |
|--------|----------|
| `Files.ListFiles` | Melihat isi workspace |
| `Files.ReadFile` | Membaca berkas teks dari workspace |
| `Files.WriteFile` | Menulis berkas — kode, dokumen, data |
| `Files.DownloadFile` | Mengunduh dari URL, termasuk lampiran yang dikirim pengguna |
| `Files.ShareResult` | Mengunggah hasil ke storage dan mengembalikan tautan unduh |
| `Files.DeleteFile` | Menghapus berkas atau folder |

`Files.ShareResult` otomatis meng-**zip** folder sebelum mengunggah, karena folder tidak bisa dibagikan apa adanya.

### Eksekusi

| Fungsi | Kegunaan |
|--------|----------|
| `Script.RunScript` | Menjalankan PowerShell, Python, Node, .NET, atau cmd |
| `Script.InstallPackage` | `pip` / `npm` / `dotnet add package` — mati secara bawaan |
| `Script.CheckAvailableExecutors` | Memeriksa executor mana yang benar-benar terpasang |

### Skill

| Fungsi | Kegunaan |
|--------|----------|
| `Skills.ListSkills` | Nama dan deskripsi setiap skill aktif |
| `Skills.LoadSkill` | Instruksi lengkap satu skill beserta daftar berkasnya |
| `Skills.ReadReference` | Membaca satu dokumen referensi bawaan skill |
| `Skills.ListSkillFiles` | Isi folder skill |
| `Skills.RunSkillScript` | Menjalankan skrip bawaan skill |

Kelimanya dimuat bertahap: model hanya melihat nama dan deskripsi sampai ia benar-benar memilih
satu skill. `RunSkillScript` melewati tiga gerbang terpisah — lihat
[docs/skills-and-mcp.md](skills-and-mcp.md).

### Tool dari MCP

Server MCP yang diaktifkan admin menyumbang tool-nya sendiri ke kernel lewat `McpToolProvider`.
Tool itu muncul berdampingan dengan fungsi bawaan di atas, dan model memilihnya tanpa perlakuan
khusus.

---

## Sandbox

Eksekusi skrip adalah bagian paling berisiko, jadi dibatasi tiga lapis:

1. **Saklar konfigurasi** — `EnableCodeExecution` bisa dimatikan sepenuhnya
2. **Daftar izin** — hanya executor di `AllowedExecutors` yang boleh jalan
3. **Sandbox path** — setiap path melewati `Workspace.Resolve`, yang menolak path absolut dan traversal `../`; proses berjalan dengan working directory terkunci ke folder sesi

Ditambah batas waktu keras: skrip yang melewati `ExecutionTimeoutSeconds` dibunuh beserta seluruh pohon prosesnya.

Skrip ditulis ke berkas, bukan dioper sebagai argumen, supaya kode multi-baris dan tanda kutip selamat melintasi shell.

Untuk `node`, ekstensinya dipilih dari isi skrip: `.mjs` bila ada `import`/`export` di awal baris, `.cjs` bila tidak. Ini bukan soal gaya — berkas `.js` tanpa `package.json` membuat Node memperingatkan di stderr bahwa ia harus mem-parse ulang, dan peringatan itu akan sampai ke model sebagai blok `--- ERROR ---` pada skrip yang sebenarnya berhasil.

> Sandbox ini membatasi **path**, bukan hak sistem operasi. Untuk deployment publik, jalankan server di dalam container dengan pengguna tanpa hak istimewa, atau matikan `EnableCodeExecution`.

---

## Rendering jawaban

Kang Bacot menjawab dalam Markdown. `MarkdownRenderer` mengubahnya menjadi HTML dan menangani:

- Tabel — digulung dalam pembungkus yang bisa di-scroll agar tidak melebarkan thread
- Blok kode dengan penanda bahasa
- Gambar, video, dan audio — URL media telanjang pada barisnya sendiri otomatis jadi elemen yang bisa diputar
- Blockquote, daftar, tautan otomatis, emoji

**Keluarannya selalu disanitasi.** HTML mentah dimatikan di pipeline Markdig, lalu hasilnya dibersihkan sekali lagi dari tag `script`/`iframe`, handler event `on*`, dan URI `javascript:`. Output model tidak pernah dipercaya, sekalipun modelnya sendiri jinak.

---

## Mengganti penyedia model

Dari **Admin → Pengaturan**, atau di `appsettings.json`:

```jsonc
"Bot": {
  "Provider": "Anthropic",     // OpenAI | AzureOpenAI | DeepSeek | Anthropic | Gemini | Ollama
  "Model": "claude-sonnet-5",
  "ApiKey": "sk-ant-...",
  "Endpoint": "",              // wajib untuk AzureOpenAI, DeepSeek, dan Ollama
  "ContextWindowTokens": 200000
}
```

### Contoh per penyedia

```jsonc
// Azure OpenAI — Model berisi nama deployment, bukan nama model
"Bot": { "Provider": "AzureOpenAI", "Model": "gpt-5-mini",
         "Endpoint": "https://<resource>.openai.azure.com/", "ApiKey": "..." }

// DeepSeek — kompatibel OpenAI, jadi memakai konektor yang sama
"Bot": { "Provider": "DeepSeek", "Model": "deepseek-v4-flash",
         "Endpoint": "https://api.deepseek.com", "ApiKey": "sk-..." }

// Layanan lain yang kompatibel OpenAI (Groq, OpenRouter, vLLM lokal)
"Bot": { "Provider": "OpenAI", "Model": "...", "Endpoint": "https://...", "ApiKey": "..." }
```

> **Model reasoning** (seri `o1`/`o3`/`o4`, keluarga `gpt-5`) menolak `max_tokens`, `temperature`,
> dan `top_p` dengan HTTP 400. `KernelFactory` mengenali nama modelnya dan tidak mengirim
> ketiganya sama sekali — pengaturan bawaan deployment yang berlaku. Jadi `Temperature` dan
> `MaxTokens` di konfigurasi hanya berpengaruh pada model non-reasoning.

---

## Hasil uji coba dengan model sungguhan

Dijalankan dengan [`tools/Telepati.BotTest`](../tools/Telepati.BotTest), yang menjalankan
percakapan nyata lewat `BotService` — bukan mock:

```bash
TELEPATI_BOT_PROVIDER=AzureOpenAI TELEPATI_BOT_MODEL=gpt-5-mini TELEPATI_BOT_APIKEY=... TELEPATI_BOT_ENDPOINT=https://<resource>.openai.azure.com/ TELEPATI_TAVILY_KEY=... dotnet run --project tools/Telepati.BotTest
```

Kredensial dibaca dari variabel lingkungan supaya tidak pernah masuk ke berkas yang ter-commit.

**16 dari 16 pemeriksaan lolos pada kedua penyedia** (6 Agustus 2026):

| Yang diuji | Azure `gpt-5-mini` | DeepSeek `deepseek-v4-flash` |
|---|---|---|
| Ngobrol biasa | ✅ | ✅ |
| Kernel function — hitungan (11% dari 1.250.000 → **137.500**) | ✅ | ✅ |
| Kernel function — waktu (mengembalikan tanggal hari itu) | ✅ | ✅ |
| Kernel function — statistik (rata-rata 35,6 · median 23) | ✅ | ✅ |
| Markdown — tabel dan blok kode | ✅ | ✅ |
| Tool — pencarian Tavily dengan sumber | ✅ | ✅ |
| Memori sesi lintas giliran | ✅ | ✅ |
| `#newpersona` benar-benar dipakai | ✅ | ✅ |
| `#resetbot` benar-benar mengosongkan memori | ✅ | ✅ |
| Diam di grup tanpa mention | ✅ | ✅ |
| Menjawab di grup saat di-mention | ✅ | ✅ |
| Sesi grup dan sesi langsung terpisah | ✅ | ✅ |

Uji waktu adalah yang paling menentukan: model tidak punya cara mengetahui tanggal hari ini,
jadi jawaban yang benar hanya mungkin kalau `Time.GetCurrentDateTime` benar-benar dipanggil.

### Latensi terukur

Waktu balasan penuh, termasuk perjalanan tool:

| Jenis permintaan | Azure `gpt-5-mini` | DeepSeek `deepseek-v4-flash` |
|---|---|---|
| Ngobrol biasa | 6.146 ms | 4.869 ms |
| Panggilan satu kernel function | 3.654 ms | 2.716 ms |
| Pencarian internet (Tavily) | 10.041 ms | 6.986 ms |
| **Rata-rata seluruh giliran** | **10.230 ms** | **4.039 ms** |

DeepSeek konsisten **2–3× lebih cepat** di sini. Itu wajar: `gpt-5-mini` adalah model reasoning
yang menghabiskan token untuk berpikir sebelum menjawab. Untuk bot obrolan yang jawabannya
ditunggu orang, kecepatan itu lebih terasa daripada selisih kualitasnya.

Untuk pengembangan lokal tanpa biaya, Ollama adalah bawaannya:

```bash
ollama pull llama3.2
ollama serve
```

Semantic Kernel tidak menyediakan konektor Anthropic resmi, jadi Telepati mengadaptasi `IChatClient` dari SDK Anthropic ke abstraksi chat-completion milik kernel. Dari sisi pemakaian tidak ada bedanya.
