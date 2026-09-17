# Memulai

🇬🇧 [English](../en/getting-started.md) · Dibuat oleh Gravicode Studios, dipimpin oleh Kang Fadhil

## 1. Instalasi

```bash
dotnet add package VoipNet.Core          # panggilan dan media
dotnet add package VoipNet.Audio         # mikrofon, speaker, perekaman
dotnet add package VoipNet.AI            # model, suara, voice agent
dotnet add package VoipNet.Enterprise    # IVR, antrean, layanan rekaman, tools CRM
dotnet tool install -g VoipNet.Cli       # perintah voipnet
```

`VoipNet.Core` membawa engine native di `runtimes/<rid>/native`. Cara membangun dari source ada di [building.md](building.md).

## 2. Panggilan pertama — tanpa PBX

Dua client pada antarmuka loopback bisa saling menelepon secara langsung. Beginilah test dan Gallery bekerja.

```csharp
using VoipNet;

await using var alice = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "alice" });
await using var bob   = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "bob" });
await alice.StartAsync();
await bob.StartAsync();

bob.IncomingCall += async (_, e) =>
{
    Console.WriteLine($"Bob: panggilan dari {e.From}");
    await e.Call.AnswerAsync();
};

var call = await alice.CallAsync($"sip:bob@{bob.LocalAddress}");
Console.WriteLine($"Tersambung dengan {call.Codec} pada {call.SampleRate} Hz");

// 1 detik nada 440 Hz pada 16 kHz — engine melakukan resample ke codec yang disepakati.
var tone = Enumerable.Range(0, 16000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000))).ToArray();
call.SendAudio(tone, 16000);

await Task.Delay(1500);
var stats = call.GetStatistics();
Console.WriteLine($"MOS {stats.Mos:0.00}, terkirim {stats.PacketsSent} paket");
await call.HangupAsync();
```

## 3. Registrasi ke PBX

```csharp
await using var client = new VoipClient(new VoipClientOptions
{
    Domain = "pbx.contoh.co.id",      // atau Registrar = "10.0.0.5:5060"
    Username = "1001",
    Password = "rahasia",
    Transport = SipTransport.Udp,
    RegisterOnStart = true,           // StartAsync menunggu 200 OK
});

client.RegistrationChanged += (_, e) => Console.WriteLine($"{e.State} {e.StatusCode}");
await client.StartAsync();

var call = await client.CallAsync("1002");   // ekstensi → sip:1002@pbx.contoh.co.id
```

Di balik NAT, client mempelajari alamat publiknya dari `received`/`rport` dan melakukan registrasi ulang otomatis. Isi `StunServer` untuk media, `PublicAddress` untuk NAT statis, atau `OutboundProxy` bila provider mewajibkannya.

## 4. Bicara lewat mikrofon

```csharp
using VoipNet.Audio;

var call = await client.CallAsync("1002");
using var audio = CallAudioBridge.Attach(call);   // mikrofon dan speaker default via OpenAL
await call.Completion;
```

## 5. Biarkan AI menjawab

```csharp
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Llm;
using VoipNet.AI.Speech;

IChatClient model = new ChatClientBuilder(
        new OpenAiChatClient(OpenAiChatOptions.ForAzure(endpoint, key, "gpt-5-mini")))
    .UseFunctionInvocation()
    .Build();

client.IncomingCall += async (_, e) =>
{
    await e.Call.AnswerAsync();
    await using var agent = new VoiceAgent(
        model,
        new DeepgramSpeechToText(new DeepgramOptions { ApiKey = deepgramKey, Language = "id" }),
        new ElevenLabsTextToSpeech(new ElevenLabsOptions { ApiKey = elevenLabsKey }),
        new VoiceAgentOptions { Greeting = "Halo, ada yang bisa saya bantu?", Language = "id-ID" });
    await agent.RunAsync(e.Call);
};
```

Lihat [ai.md](ai.md) untuk provider, tools, memori, dan model realtime.

## 6. Dependency injection

```csharp
builder.Services.AddVoipClient(o => builder.Configuration.GetSection("Sip").Bind(o));
builder.Services.AddVoipClientHostedService();          // start/stop bersama host
builder.Services.AddOpenAiChatClient(o => { o.ApiKey = key; o.Model = "gpt-4o-mini"; });
builder.Services.AddDeepgramSpeechToText(o => o.ApiKey = deepgramKey);
builder.Services.AddElevenLabsTextToSpeech(o => o.ApiKey = elevenLabsKey);
builder.Services.AddVoiceAgent(o => o.Greeting = "Halo!");   // menyediakan VoiceAgentFactory
builder.Services.AddVoipNetEnterprise();                      // IvrRunner, CallCenterService, RecordingService
```

## 7. Coba sample

```bash
dotnet run --project samples/VoipNet.Softphone        # tekan "echo" atau "music"
dotnet run --project samples/VoipNet.Gallery
dotnet run --project samples/VoipNet.CallCenter        # http://localhost:5184
dotnet run --project samples/VoipNet.IvrStudio
dotnet run --project samples/VoipNet.WebPhone          # http://localhost:5190, browser menelepon SIP
dotnet run --project samples/VoipNet.RealtimeAgent -- --demo
```

Fitur AI pada sample membaca `VOIPNET_AI_ENDPOINT`, `VOIPNET_AI_KEY`, dan `VOIPNET_AI_MODEL`.
