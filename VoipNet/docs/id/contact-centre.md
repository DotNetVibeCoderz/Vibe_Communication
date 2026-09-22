# Contact center: IVR, antrean, rekaman, CRM

🇬🇧 [English](../en/contact-centre.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

![Wallboard call center](../images/callcenter-wallboard-light.png)

## IVR

```csharp
var flow = IvrFlow.Create()
    .Welcome("Selamat datang di Gravicode Net.")
    .Menu("main", "Tekan 1 untuk penjualan, 2 untuk bantuan teknis, 0 untuk operator.", m => m
        .Option('1', "Penjualan", new IvrAction.Enqueue("sales", "Menghubungkan ke tim penjualan."))
        .Option('2', "Bantuan", new IvrAction.Collect("account", "Masukkan nomor pelanggan, akhiri dengan pagar.", 8, "support"))
        .Option('0', "Operator", new IvrAction.Transfer("sip:operator@pbx", "Mohon tunggu."))
        .WaitFor(TimeSpan.FromSeconds(6))
        .Attempts(3)
        .OnFailure(new IvrAction.Hangup("Terima kasih.")))
    .Menu("support", "Tekan 1 untuk asisten AI.", m => m
        .Option('1', "Asisten AI", new IvrAction.Handoff("ai", (call, context, ct) =>
            agentFactory.Create(o => o.SystemPrompt += $" Nomor pelanggan: {context.Values["account"]}").RunAsync(call, ct)))
        .Option('9', "Kembali", new IvrAction.Goto("main")))
    .Build();                                 // memvalidasi bahwa setiap menu tujuan ada

var result = await new IvrRunner(textToSpeech).RunAsync(call, flow);
// result.Outcome: Queued · Transferred · HandedOff · Completed · CallerLeft
// result.Context.Values["account"], result.Context.RequestedQueue, result.Context.Path
```

| Aksi | Efek |
| --- | --- |
| `Goto(menu)` | tampilkan menu lain |
| `Say(text)` | ucapkan lalu ulangi menu saat ini |
| `Collect(key, prompt, maxDigits, nextMenu, terminator)` | baca digit ke `Context.Values[key]` |
| `Transfer(target, announcement)` | REFER penelepon ke tujuan lain |
| `Enqueue(queue, announcement)` | berhenti dan laporkan antrean (teruskan ke `CallCenterService`) |
| `Handoff(name, handler)` | serahkan panggilan ke handler async apa pun, biasanya `VoiceAgent` |
| `Hangup(announcement)` | ucapkan salam penutup dan akhiri panggilan |

Menu dapat memutar file WAV (`PromptFromFile`) sebagai pengganti prompt sintesis.

![IVR Studio](../images/ivrstudio-test-call.png)

## Antrean dan agen

```csharp
var centre = new CallCenterService(pbxClient, textToSpeech);
centre.AddQueue(new CallQueueOptions
{
    Name = "support",
    Strategy = RoutingStrategy.SkillBased,   // RoundRobin · LongestIdle · FewestCalls · SkillBased
    RequiredSkill = "support",
    ServiceLevelTarget = TimeSpan.FromSeconds(20),
    MaxWait = TimeSpan.FromMinutes(5),
    OverflowTarget = "sip:voicemail@pbx",
    RingTimeout = TimeSpan.FromSeconds(25),
    WrapupTime = TimeSpan.FromSeconds(15),
    AnnouncePosition = true,
    MusicOnHoldFile = "hold.wav",
});

centre.AddAgent(new Agent { Id = "2001", Name = "Sari", Uri = "sip:2001@pbx", Skills = { "support", "english" } });
centre.SetAgentState("2001", AgentState.Available);

pbxClient.IncomingCall += async (_, e) =>
{
    await e.Call.AnswerAsync();
    var ivr = await ivrRunner.RunAsync(e.Call, flow);
    if (ivr.Outcome == IvrOutcome.Queued)
    {
        var result = await centre.EnqueueAsync(e.Call, ivr.Context.RequestedQueue!, priority: 0, context: ivr.Context.Values);
    }
};
```

Selama penelepon menunggu: pengumuman posisi dan/atau musik tunggu diputar; saat penelepon berada di urutan pertama dan ada agen dengan skill yang sesuai, agen dipesan (`Ringing`) dan ditelepon. Jika agen menjawab, kedua leg dijembatani dalam konferensi dan agen berstatus `OnCall`; jika tidak, agen berikutnya dicoba. Setelah panggilan, agen melalui `Wrapup` lalu kembali `Available`.

Supervisor listen-in dan whisper:

```csharp
var supervisor = await pbxClient.CallAsync("sip:supervisor@pbx");
centre.Monitor(callerCallId, supervisor, whisper: false);   // hanya mendengar
```

Mode hanya-mendengar membuat leg ke supervisor menjadi send-only: supervisor mendengar percakapan, tetapi suaranya tidak masuk ke jembatan.

### Callback dan estimasi waktu tunggu

Penelepon yang tidak ingin menunggu di telepon bisa menyimpan posisinya lalu menutup panggilan:

```csharp
centre.CallQueued += async (_, queued) =>
{
    var wait = centre.EstimatedWait(queued.QueueName, queued.Position);
    if (wait > TimeSpan.FromMinutes(2) && await OffersCallbackAsync(queued.Call, wait))
    {
        centre.RequestCallback(queued);      // bawaannya nomor penelepon sendiri
    }
};

centre.CallbackCompleted += (_, request) => log.Info($"{request.Destination}: {request.Outcome}");
```

`EnqueueAsync` lalu mengembalikan `QueueOutcome.CallbackScheduled` sehingga aplikasi bisa berterima
kasih dan menutup panggilan. Saat ada agent bebas dan callback itu sudah menunggu lebih lama daripada
penelepon yang masih memegang saluran, layanan memesan agent tersebut, menelepon balik pelanggan,
memutar `CallbackOptions.Announcement`, lalu menyambungkan keduanya. Pelanggan yang tidak mengangkat
dicoba lagi setelah `RetryAfter` sampai `MaxAttempts`, lalu permintaannya berakhir sebagai `NoAnswer`.
`PendingCallbacks(queue)` menampilkan yang masih tertunda dan `CancelCallback(id)` membatalkan satu.

`EstimatedWait(queue, position)` membagi rata-rata waktu penanganan (bicara plus wrap-up, tiga menit
selama antrean belum punya riwayat) ke jumlah agent yang sedang login untuk antrean itu. Hasilnya
`TimeSpan.Zero` bila ada agent bebas, dan `TimeSpan.MaxValue` bila tidak ada agent sama sekali — saat
itu tidak ada estimasi yang jujur untuk diberikan, dan lebih baik mengatakannya daripada mengarang
angka.

### Berbagi state antar-node

Satu node memegang panggilan yang ia jawab, tetapi siapa yang sedang login dan callback apa yang masih
terutang harus disepakati bersama. `ICallCenterStore` menyimpan tepat hal itu, dan `SqlCallCenterStore`
mengimplementasikannya di atas provider ADO.NET mana pun — SQLite untuk satu node yang ingin tahan
restart, SQL Server atau PostgreSQL untuk beberapa node:

```csharp
var store = new SqlCallCenterStore(() => new SqliteConnection("Data Source=callcentre.db"), node: "pbx-1");
var centre = new CallCenterService(pbxClient, textToSpeech, store: store);
centre.AddQueue(new CallQueueOptions { Name = "support" });
await centre.RestoreAsync();        // ambil kembali callback yang masih terutang
```

State agent ditulis setiap kali berubah dan dibaca lewat `AllAgentsAsync()`, persis yang dibutuhkan
dashboard lintas node. Sebelum menelepon balik, node mengklaim callback lewat satu update bersyarat,
sehingga dua node tidak pernah menelepon pelanggan yang sama; pelanggan yang tidak mengangkat
dilepaskan kembali untuk node mana pun yang bebas berikutnya. Tabel dibuat saat pertama dipakai, dan
store yang sesaat tidak tersedia hanya dicatat di log, bukan menghentikan call center.

### Metrik

```csharp
var s = centre.Metrics.Snapshot("support");
// Offered, Answered, Abandoned, Overflowed, AverageWait, LongestWait, AverageTalk, ServiceLevel, AbandonRate
centre.Metrics.Changed += (_, _) => dashboard.Refresh();
```

## Layanan rekaman

```csharp
using var recordings = new RecordingService(pbxClient, new RecordingOptions
{
    Directory = "recordings", Format = RecordingFormat.Mp3, Layout = RecordingLayout.Stereo,
    RecordAllCalls = true, Retention = TimeSpan.FromDays(90),
});
recordings.RecordingSaved += (_, info) => Upload(info.Path);
var latest = recordings.List(50);           // indeks disimpan sebagai JSON di samping setiap file
recordings.ApplyRetention();
```

## Tools CRM

Implementasikan `ICrmConnector` untuk CRM Anda lalu berikan tools-nya ke model:

```csharp
public sealed class HubSpotConnector : ICrmConnector { … }

var tools = CrmToolset.Create(new HubSpotConnector(...));
var options = new VoiceAgentOptions { ChatOptions = new ChatOptions { Tools = [.. tools] } };
```

`InMemoryCrmConnector` mencocokkan nomor telepon berdasarkan sembilan digit terakhir, sehingga `+62 812…`, `62812…`, dan `0812…` merujuk ke pelanggan yang sama.

## Analitik dengan AI

Sample Call Centre mengirim snapshot wallboard ke model dan menampilkan saran penjadwalan staf — pola yang bisa dipakai ulang untuk laporan shift, ringkasan QA dari rekaman (transkripsi dengan `TranscribeOnceAsync`, ringkas dengan `IChatClient` apa pun), atau analitik intent.
