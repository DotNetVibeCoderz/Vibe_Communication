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
