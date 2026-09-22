using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Llm;
using VoipNet.AI.Speech;
using VoipNet.Audio;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Speech providers against the real services, with keys from <see cref="TestKeys"/>.</summary>
public sealed class LiveSpeechTests
{
    private static ElevenLabsOptions? ElevenLabs() =>
        TestKeys.Find("eleven labs") is { Length: > 0 } key ? new ElevenLabsOptions { ApiKey = key } : null;

    private static async Task<short[]> SynthesizeAsync(ITextToSpeech tts, string text, int rate)
    {
        var bytes = new List<byte>();
        await foreach (var chunk in tts.SynthesizeAsync(text, new SpeechSynthesisOptions { SampleRate = rate }))
        {
            Assert.Equal(0, chunk.Data.Length % 2);
            bytes.AddRange(chunk.Data.ToArray());
        }

        return Pcm.AsSamples(bytes.ToArray()).ToArray();
    }

    /// <summary>
    /// Every synthesis provider the key file has a key for, as xUnit test data. A provider without a
    /// key is simply absent, so the suite grows as keys are added rather than failing without them.
    /// </summary>
    public static TheoryData<string> SynthesisProviders()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "ElevenLabs", "OpenAI", "Google Cloud", "Azure AI Speech", "Cartesia", "Deepgram", "Amazon Polly" })
        {
            if (Synthesizer(name) is not null)
            {
                data.Add(name);
            }
        }

        // A theory with no data fails the run, so keep one placeholder that skips.
        if (data.Count == 0)
        {
            data.Add("none");
        }

        return data;
    }

    /// <summary>Builds a synthesiser for a provider, or null when its key is not in the key file.</summary>
    private static ITextToSpeech? Synthesizer(string provider) => provider switch
    {
        "ElevenLabs" => ElevenLabs() is { } o ? new ElevenLabsTextToSpeech(o) : null,
        "OpenAI" => TestKeys.Section("OpenAI Speech") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new OpenAiTextToSpeech(new OpenAiSpeechOptions { ApiKey = key })
            : null,
        "Google Cloud" => TestKeys.Section("Google Cloud") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new GoogleCloudTextToSpeech(new GoogleCloudSpeechOptions { ApiKey = key })
            : null,
        "Azure AI Speech" => TestKeys.Section("Azure Speech") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new AzureTextToSpeech(new AzureSpeechOptions { ApiKey = key, Region = TestKeys.Value(s, "region") ?? "southeastasia" })
            : null,
        "Cartesia" => TestKeys.Section("Cartesia") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new CartesiaTextToSpeech(new CartesiaOptions { ApiKey = key, Voice = TestKeys.Value(s, "voice") ?? string.Empty })
            : null,
        "Deepgram" => TestKeys.Section("Deepgram") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new DeepgramTextToSpeech(new DeepgramOptions { ApiKey = key })
            : null,
        "Amazon Polly" => TestKeys.Section("Amazon") is { } s && TestKeys.Value(s, "accesskey") is { Length: > 0 } id && TestKeys.Value(s, "secret") is { Length: > 0 } secret
            ? new AmazonPollyTextToSpeech(new AmazonPollyOptions { AccessKeyId = id, SecretAccessKey = secret, Region = TestKeys.Value(s, "region") ?? "ap-southeast-1" })
            : null,
        _ => null,
    };

    /// <summary>Every recogniser with a key, paired with the language to ask for.</summary>
    private static ISpeechToText? Recognizer(string provider) => provider switch
    {
        "ElevenLabs" => ElevenLabs() is { } o ? new ElevenLabsSpeechToText(o) : null,
        "OpenAI" => TestKeys.Section("OpenAI Speech") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new OpenAiSpeechToText(new OpenAiSpeechOptions { ApiKey = key })
            : null,
        "Google Cloud" => TestKeys.Section("Google Cloud") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new GoogleCloudSpeechToText(new GoogleCloudSpeechOptions { ApiKey = key })
            : null,
        "Azure AI Speech" => TestKeys.Section("Azure Speech") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new AzureSpeechToText(new AzureSpeechOptions { ApiKey = key, Region = TestKeys.Value(s, "region") ?? "southeastasia" })
            : null,
        "Deepgram" => TestKeys.Section("Deepgram") is { } s && TestKeys.Value(s, "apikey") is { Length: > 0 } key
            ? new DeepgramSpeechToText(new DeepgramOptions { ApiKey = key, Language = "id" })
            : null,
        _ => null,
    };

    /// <summary>
    /// Speaks a phrase with each provider that has a key and, where the same provider can also listen,
    /// transcribes it back. This is the opt-in live suite: drop a key in the file and it runs.
    /// </summary>
    [Theory]
    [MemberData(nameof(SynthesisProviders))]
    public async Task EverySpeechProviderWithAKeySpeaksAndListens(string provider)
    {
        var tts = Synthesizer(provider);
        Assert.SkipWhen(tts is null, $"No key for {provider}.");

        const string phrase = "Pesanan Anda sudah dikirim hari ini.";
        var samples = await SynthesizeAsync(tts!, phrase, 16000);
        Assert.True(samples.Length > 8000, $"{provider} produced only {samples.Length} samples");
        Assert.True(samples.Max(s => Math.Abs((int)s)) > 2000, $"{provider} produced silence");

        if (Recognizer(provider) is { } stt)
        {
            var text = await stt.TranscribeOnceAsync(Pcm.ToBytes(samples), 16000, new SpeechRecognitionOptions { Language = "id-ID" });
            Assert.False(string.IsNullOrWhiteSpace(text), $"{provider} transcribed nothing");
        }
    }

    [Fact]
    public async Task ElevenLabsSpeaksAndTranscribesIndonesian()
    {
        var options = ElevenLabs();
        Assert.SkipWhen(options is null, "No ElevenLabs key available.");

        var samples = await SynthesizeAsync(new ElevenLabsTextToSpeech(options!), "Selamat pagi, pesanan Anda sudah dikirim hari ini.", 16000);
        Assert.True(samples.Length > 16000, $"only {samples.Length} samples");
        Assert.True(samples.Max(s => Math.Abs((int)s)) > 3000, "synthesized audio is silent");

        var text = await new ElevenLabsSpeechToText(options!).TranscribeOnceAsync(
            Pcm.ToBytes(samples), 16000, new SpeechRecognitionOptions { Language = "id-ID" });
        Assert.Contains("pesanan", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoiceAgentAnswersACallerWithElevenLabsAndAzure()
    {
        var speech = ElevenLabs();
        var azure = TestKeys.Section("Azure OpenAI");
        var (endpoint, key, model) = azure is null ? default : (TestKeys.Value(azure, "endpoint"), TestKeys.Value(azure, "apikey"), TestKeys.Value(azure, "model"));
        Assert.SkipWhen(speech is null || endpoint is null || key is null || model is null, "Needs ElevenLabs and Azure OpenAI keys.");

        var chatOptions = OpenAiChatOptions.ForAzure(endpoint!, key!, model!.Split(',')[0].Trim());
        chatOptions.ReasoningEffort = "minimal";
        using var chat = new OpenAiChatClient(chatOptions);
        var tts = new ElevenLabsTextToSpeech(speech!);
        await using var pair = await LoopbackPair.ConnectAsync("caller", "shop");
        await using var agent = new VoiceAgent(chat, new ElevenLabsSpeechToText(speech!), tts, new VoiceAgentOptions
        {
            SystemPrompt = "Kamu layanan pelanggan Toko Maju. Toko buka setiap hari jam sembilan pagi sampai jam lima sore. Jawab dalam satu kalimat pendek bahasa Indonesia.",
            Greeting = "Halo, Toko Maju, ada yang bisa dibantu?",
            Language = "id-ID",
            EnableCallControlTools = false,
            ChatOptions = new ChatOptions { MaxOutputTokens = 600 },
        });

        var callerSaid = new ConcurrentQueue<string>();
        var agentSaid = new ConcurrentQueue<string>();
        agent.CallerSaid += (_, text) => callerSaid.Enqueue(text);
        agent.AgentSaid += (_, text) => agentSaid.Enqueue(text);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var run = agent.RunAsync(pair.CalleeLeg, cts.Token);

        await TestHelpers.WaitUntilAsync(() => !agentSaid.IsEmpty, TimeSpan.FromSeconds(30), "the greeting");
        await TestHelpers.WaitUntilAsync(() => !agent.IsSpeaking && pair.CalleeLeg.QueuedAudioMs == 0, TimeSpan.FromSeconds(30), "the greeting to finish");

        // The caller asks a question in a synthesized voice, followed by silence so the recogniser ends the utterance.
        var question = await SynthesizeAsync(tts, "Jam berapa tokonya buka?", 16000);
        pair.CallerLeg.SendAudio(question, 16000);
        pair.CallerLeg.SendAudio(new short[16000 * 2], 16000);

        await TestHelpers.WaitUntilAsync(() => agentSaid.Count >= 2, TimeSpan.FromSeconds(60), "the agent's answer");
        var transcript = $"caller: [{string.Join(" | ", callerSaid)}] agent: [{string.Join(" | ", agentSaid)}]";
        Assert.True(callerSaid.Any(text => text.Contains("buka", StringComparison.OrdinalIgnoreCase)), transcript);
        var answer = agentSaid.Last();
        Assert.True(answer.Contains("sembilan", StringComparison.OrdinalIgnoreCase) || answer.Contains('9'), transcript);

        await pair.CallerLeg.HangupAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SynthesizedChunksNeverSplitASample()
    {
        // A stream that returns three bytes per read, as a slow network can.
        var pcm = Enumerable.Range(0, 1001).Select(i => (byte)i).ToArray();
        using var http = new HttpClient(new StubHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TrickleStream(pcm, 3)) }));
        var tts = new ElevenLabsTextToSpeech(new ElevenLabsOptions { ApiKey = "test" }, http);

        var received = new List<byte>();
        await foreach (var chunk in tts.SynthesizeAsync("hi", new SpeechSynthesisOptions { SampleRate = 16000 }))
        {
            Assert.Equal(0, chunk.Data.Length % 2);
            received.AddRange(chunk.Data.ToArray());
        }

        Assert.Equal(pcm[..1000], received);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private sealed class TrickleStream(byte[] data, int step) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, step));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, step)], cancellationToken);
    }
}
