# AI: model, suara, dan agen

🇬🇧 [English](../en/ai.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

![Voice agent di Gallery](../images/gallery-voice-agent.png)

## Model bahasa

Setiap konektor mengimplementasikan `Microsoft.Extensions.AI.IChatClient`, sehingga middleware logging, caching, OpenTelemetry, dan function invocation dari ekosistem Microsoft.Extensions.AI langsung berlaku, dan Semantic Kernel dapat memakainya secara langsung.

| Client | Mencakup |
| --- | --- |
| `OpenAiChatClient` | OpenAI, Azure OpenAI (`OpenAiChatOptions.ForAzure`), DeepSeek, OpenRouter, vLLM, Ollama, LM Studio — server `/chat/completions` apa pun |
| `AnthropicChatClient` | Claude melalui Messages API |
| `GeminiChatClient` | Google Gemini melalui `generateContent` |

Semuanya mendukung streaming, instruksi sistem, temperature/top-p/max tokens, dan tool calling.

```csharp
IChatClient chat = new OpenAiChatClient(OpenAiChatOptions.ForAzure(
    endpoint: "https://resource-anda.openai.azure.com/", apiKey: key, deployment: "gpt-5-mini"));

// Model reasoning (GPT-5, seri o) otomatis memakai max_completion_tokens.
// Untuk suara, buat tetap cepat:
var options = OpenAiChatOptions.ForAzure(endpoint, key, "gpt-5-mini");
options.ReasoningEffort = "minimal";

var deepseek = new OpenAiChatClient(new OpenAiChatOptions
{
    BaseUri = new Uri("https://api.deepseek.com/"), ApiKey = key, Model = "deepseek-chat",
});

var claude = new AnthropicChatClient(new AnthropicChatOptions { ApiKey = key, Model = "claude-sonnet-5" });
var gemini = new GeminiChatClient(new GeminiChatOptions { ApiKey = key, Model = "gemini-2.0-flash" });
```

### Tools (AI functions)

```csharp
var ticket = AIFunctionFactory.Create(
    (string customer, string problem) => crm.OpenTicket(customer, problem),
    "open_ticket", "Membuka tiket dukungan.");

using var client = new ChatClientBuilder(chat).UseFunctionInvocation().Build();
var answer = await client.GetResponseAsync("Internet Rina mati, buatkan tiket.", new ChatOptions { Tools = [ticket] });
```

`CallControlTools.Create(call, agent)` memberi model `transfer_call`, `end_call`, `send_dtmf`, `set_hold`, dan `get_call_quality`. `CrmToolset.Create(connector)` menambahkan `crm_lookup_customer`, `crm_recent_tickets`, `crm_create_ticket`, dan `crm_add_note`.

### Semantic Kernel

Semantic Kernel menerima `IChatClient` apa pun, sehingga konektor bisa langsung dipakai di kernel, agent, dan plugin:

```csharp
var builder = Kernel.CreateBuilder();
builder.Services.AddSingleton<IChatClient>(chat);
builder.Services.AddSingleton<IChatCompletionService>(chat.AsChatCompletionService());
var kernel = builder.Build();
```

Kernel function kemudian dapat diberikan ke voice agent sebagai tools melalui `ChatOptions.Tools`.

## Suara

```csharp
public interface ISpeechToText
{
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(IAsyncEnumerable<AudioChunk> audio, SpeechRecognitionOptions? options, CancellationToken ct);
    Task<string> TranscribeOnceAsync(ReadOnlyMemory<byte> pcm, int sampleRate, SpeechRecognitionOptions? options, CancellationToken ct);
}

public interface ITextToSpeech
{
    int PreferredSampleRate { get; }
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, SpeechSynthesisOptions? options, CancellationToken ct);
}
```

Audio selalu PCM 16-bit mono; engine yang mengonversi sample rate.

| Provider | STT | TTS | Catatan |
| --- | --- | --- | --- |
| Deepgram | `DeepgramSpeechToText` — streaming web socket dengan hasil sementara | — | latensi barge-in terendah |
| OpenAI | `OpenAiSpeechToText` | `OpenAiTextToSpeech` (PCM 24 kHz, streaming) | bekerja dengan server kompatibel |
| ElevenLabs | `ElevenLabsSpeechToText` (Scribe) | `ElevenLabsTextToSpeech` (PCM 8–44,1 kHz, streaming) | suara ekspresif; paket gratis hanya boleh memakai voice premade lewat API (`VoiceId` bawaan termasuk di dalamnya) |
| Google Cloud | `GoogleCloudSpeechToText` | `GoogleCloudTextToSpeech` (LINEAR16) | API key atau token OAuth; model `telephony` |
| Amazon | — | `AmazonPollyTextToSpeech` (PCM 8/16 kHz, SigV4, tanpa AWS SDK) | Transcribe ada di roadmap |
| ElBruno.Realtime | `ElBrunoRealtimeSpeechToText` (web socket) | `ElBrunoRealtimeTextToSpeech` (HTTP PCM) | self-hosted, open source |

Provider tanpa pengenal streaming diturunkan dari `BufferedSpeechToText`: voice activity detector memotong ucapan (dengan pre-roll 300 ms) dan setiap ucapan ditranskripsi saat selesai.

### Protokol ElBruno.Realtime

- Pengenalan: `ws://host/stt?sample_rate=16000&language=id`; kirim frame biner PCM 16-bit; terima frame teks `{"text": "...", "final": true}`.
- Sintesis: `POST http://host/tts` dengan `{"text", "voice", "sample_rate", "speed", "format": "pcm_s16le"}`; body respons berupa PCM mentah.

## VoiceAgent

```csharp
var agent = new VoiceAgent(chat, speechToText, textToSpeech, new VoiceAgentOptions
{
    Greeting = "Halo, dengan Gravicode Net.",
    SystemPrompt = "Kamu agen layanan pelanggan. Jawab singkat.",
    Language = "id-ID",
    BargeIn = true,
    SilencePrompt = TimeSpan.FromSeconds(12),
    SilenceHangup = TimeSpan.FromSeconds(40),
    HandoffTarget = "sip:tier2@pbx",
    EnableCallControlTools = true,
    ChatOptions = new ChatOptions { Tools = [.. CrmToolset.Create(crm)] },
}, new JsonFileConversationStore("conversations"));

agent.CallerSaid += (_, t) => log.Info($"penelepon: {t}");
agent.AgentSaid += (_, t) => log.Info($"agen: {t}");
agent.Interrupted += (_, _) => log.Info("barge-in");
await agent.RunAsync(call);
```

Perilakunya:

- **Jawaban streaming.** Keluaran model dipotong per kalimat dan setiap kalimat disintesis begitu lengkap.
- **Barge-in.** Pengenalan suara tetap berjalan saat agen berbicara; transkrip sementara membatalkan giliran dan membersihkan audio yang antre.
- **Memori.** Dengan conversation store, `RestoreTurns` giliran terakhir dipulihkan untuk penelepon yang kembali (kunci `ConversationKey` atau URI penelepon).
- **Hand-off.** `HandOffAsync(target, announcement)` berbicara, menunggu audio selesai, lalu mentransfer. Model dapat melakukan hal yang sama dengan `transfer_call`.
- **Penanganan hening.** Pengingat setelah `SilencePrompt`, tutup panggilan setelah `SilenceHangup`.

Dengan DI, `AddVoiceAgent` mendaftarkan `VoiceAgentFactory` yang membuat satu agen per panggilan dengan penyesuaian per panggilan.

## RealtimeVoiceAgent

```csharp
var realtime = new RealtimeVoiceAgent(new RealtimeVoiceOptions
{
    ApiKey = openAiKey, Model = "gpt-realtime", Voice = "alloy",
    Instructions = "Kamu resepsionis yang ramah.", Greeting = "Halo, ada yang bisa dibantu?",
});
await realtime.RunAsync(call);
```

Audio panggilan di-resample ke 24 kHz dan dialirkan ke model; audio respons dialirkan kembali ke panggilan. Voice activity detection di sisi server memicu `ClearAudio` saat penelepon menyela.

Deployment realtime Azure OpenAI memakai agen yang sama:

```csharp
var options = RealtimeVoiceOptions.ForAzure("https://my-resource.openai.azure.com", azureKey, "gpt-realtime-mini");
options.Greeting = "Halo, ada yang bisa dibantu?";
var agent = new RealtimeVoiceAgent(options);
agent.AgentSaid += (_, text) => Console.WriteLine($"agen: {text}");
agent.ErrorReceived += (_, error) => Console.WriteLine($"error provider: {error}");
```

Secara bawaan agen memakai protokol realtime GA (`session.type = "realtime"`, pengaturan audio di `audio.input` dan `audio.output`); set `Protocol = RealtimeProtocol.Beta` untuk endpoint yang masih memerlukan `OpenAI-Beta: realtime=v1`. Di Azure, transkripsi penelepon memerlukan deployment tersendiri: isi `InputTranscriptionModel` untuk mengaktifkan `CallerSaid`.

## Analitik pasca-panggilan

`CallAnalyzer` mengubah panggilan yang sudah selesai menjadi laporan yang bisa dibaca supervisor:
ringkasan, sentimen penelepon, topik, hal yang masih terbuka, dan skor terhadap checklist pilihan Anda.

```csharp
var analyzer = new CallAnalyzer(chat, speechToText, new CallAnalyzerOptions
{
    Language = "Indonesian",
    QualityChecklist = { "menawarkan promo sebelum menutup" },   // ditambahkan ke daftar bawaan
});

recordings.RecordingSaved += async (_, info) =>
{
    var analysis = await analyzer.AnalyzeRecordingAsync(info.Path);
    Console.WriteLine($"{analysis.Sentiment} {analysis.QualityScore}/100 — {analysis.Summary}");
    foreach (var item in analysis.ActionItems)
    {
        Console.WriteLine($"  todo: {item}");
    }
};
```

Rekaman ditranskripsikan lebih dulu, jadi analyser memerlukan provider suara; panggilan yang sudah
ditangani agent tidak perlu, karena agent menyimpan percakapannya: `analyzer.AnalyzeAsync(agent.Turns)`.
Rekaman dibaca sebagai WAV, jadi setel `RecordingOptions.Format = RecordingFormat.Wav` bila ingin
menganalisisnya.

Model diminta menjawab dalam JSON dan jawabannya dibaca secara longgar: blok berpagar, kalimat pembuka,
atau skor yang ditulis sebagai teks tetap menghasilkan analisis yang bisa dipakai, dan field yang tidak
diisi model memakai nilai bawaan alih-alih menggagalkan laporan.

## Agent assist

`AgentAssist` mendampingi agent manusia: menuliskan transkrip selama panggilan berlangsung dan, setiap
kali penelepon selesai bicara, menawarkan kalimat balasan yang boleh dipakai atau diabaikan agent. Tidak
ada satu pun keluarannya yang dikirim ke penelepon.

```csharp
var assist = new AgentAssist(chat, speechToText, new AgentAssistOptions
{
    Language = "Indonesian",
    Knowledge = "Pengiriman reguler 2-3 hari kerja. Retur dalam 7 hari.",
    SuggestionCount = 3,
});

assist.TranscriptUpdated += (_, line) => view.Append(line.Speaker, line.Text, line.IsFinal);
assist.SuggestionsUpdated += (_, suggestions) => view.Show(suggestions);
await assist.RunAsync(call, cancellationToken);
```

Baris sementara muncul saat penelepon masih bicara sehingga transkrip mengikuti percakapan; hanya
kalimat yang sudah selesai yang dikirim ke model, dan tidak lebih sering dari `MinimumInterval`.
`TranscribeAgent` menambah satu aliran pengenalan untuk sisi agent — biayanya satu koneksi provider
lagi, tetapi model jadi melihat kedua sisi percakapan. Transkrip yang terkumpul bisa langsung diberikan
ke `CallAnalyzer` saat panggilan selesai.

## Memilih pendekatan

| Kebutuhan | Gunakan |
| --- | --- |
| Latensi terendah, satu vendor tidak masalah | `RealtimeVoiceAgent` |
| Bebas memilih model dan suara, tools, memori, opsi on-premise | `VoiceAgent` dengan Deepgram/ElBruno + model apa pun + ElevenLabs/Polly |
| Menu deterministik dulu, AI untuk pertanyaan terbuka | `IvrRunner` dengan `IvrAction.Handoff` → `VoiceAgent` |
