# Getting started

🇮🇩 [Bahasa Indonesia](../id/getting-started.md) · Made by Gravicode Studios, led by Kang Fadhil

## 1. Install

```bash
dotnet add package VoipNet.Core          # calls and media
dotnet add package VoipNet.Audio         # microphone, speakers, recording
dotnet add package VoipNet.AI            # models, speech, voice agents
dotnet add package VoipNet.Enterprise    # IVR, queues, recording service, CRM tools
dotnet tool install -g VoipNet.Cli       # the voipnet command
```

`VoipNet.Core` ships the native engine under `runtimes/<rid>/native`. Building from source is covered in [building.md](building.md).

## 2. Your first call — no PBX needed

Two clients on the loopback interface can call each other directly. This is also how the tests and the Gallery work.

```csharp
using VoipNet;

await using var alice = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "alice" });
await using var bob   = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "bob" });
await alice.StartAsync();
await bob.StartAsync();

bob.IncomingCall += async (_, e) =>
{
    Console.WriteLine($"Bob: call from {e.From}");
    await e.Call.AnswerAsync();
};

var call = await alice.CallAsync($"sip:bob@{bob.LocalAddress}");
Console.WriteLine($"Connected with {call.Codec} at {call.SampleRate} Hz");

// 1 second of 440 Hz at 16 kHz — the engine resamples to the negotiated codec.
var tone = Enumerable.Range(0, 16000).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / 16000))).ToArray();
call.SendAudio(tone, 16000);

await Task.Delay(1500);
var stats = call.GetStatistics();
Console.WriteLine($"MOS {stats.Mos:0.00}, sent {stats.PacketsSent} packets");
await call.HangupAsync();
```

## 3. Register with a PBX

```csharp
await using var client = new VoipClient(new VoipClientOptions
{
    Domain = "pbx.example.com",       // or Registrar = "10.0.0.5:5060"
    Username = "1001",
    Password = "secret",
    Transport = SipTransport.Udp,
    RegisterOnStart = true,           // StartAsync waits for the 200 OK
});

client.RegistrationChanged += (_, e) => Console.WriteLine($"{e.State} {e.StatusCode}");
await client.StartAsync();

var call = await client.CallAsync("1002");   // extension → sip:1002@pbx.example.com
```

A domain without a port is resolved the way RFC 3263 says: NAPTR to pick the transport, SRV for the
hosts and ports behind the domain (tried in the order the records ask for), then A/AAAA. That is all
most providers need — no host and port to copy from a support page. Add a port, or set `DnsSrv = false`,
to go straight to the address lookup instead.

Behind NAT, the client learns its public address from `received`/`rport` and re-registers automatically. Set `StunServer` for media, `PublicAddress` for static NAT, or `OutboundProxy` when your provider requires one.

## 4. Talk through your microphone

```csharp
using VoipNet.Audio;

var call = await client.CallAsync("1002");
using var audio = CallAudioBridge.Attach(call);   // default microphone and speakers via OpenAL
await call.Completion;
```

## 5. Let an AI answer

```csharp
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Llm;
using VoipNet.AI.Speech;

IChatClient model = new ChatClientBuilder(
        new OpenAiChatClient(OpenAiChatOptions.ForAzure(endpoint, key, "gpt-5-mini") ))
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

See [ai.md](ai.md) for providers, tools, memory and realtime models.

## 6. Use dependency injection

```csharp
builder.Services.AddVoipClient(o => builder.Configuration.GetSection("Sip").Bind(o));
builder.Services.AddVoipClientHostedService();          // start/stop with the host
builder.Services.AddOpenAiChatClient(o => { o.ApiKey = key; o.Model = "gpt-4o-mini"; });
builder.Services.AddDeepgramSpeechToText(o => o.ApiKey = deepgramKey);
builder.Services.AddElevenLabsTextToSpeech(o => o.ApiKey = elevenLabsKey);
builder.Services.AddVoiceAgent(o => o.Greeting = "Halo!");   // gives you VoiceAgentFactory
builder.Services.AddVoipNetEnterprise();                      // IvrRunner, CallCenterService, RecordingService
```

## 7. Try the samples

```bash
dotnet run --project samples/VoipNet.Softphone        # dial "echo" or "music"
dotnet run --project samples/VoipNet.Gallery
dotnet run --project samples/VoipNet.CallCenter        # http://localhost:5184
dotnet run --project samples/VoipNet.IvrStudio         # http://localhost:5209
dotnet run --project samples/VoipNet.WebPhone          # http://localhost:5190, browser calls SIP
dotnet run --project samples/VoipNet.RealtimeAgent -- --demo
```

AI features in the samples read `VOIPNET_AI_ENDPOINT`, `VOIPNET_AI_KEY` and `VOIPNET_AI_MODEL`.
