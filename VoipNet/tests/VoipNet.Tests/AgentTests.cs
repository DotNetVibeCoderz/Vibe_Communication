using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Speech;
using VoipNet.Audio;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Fakes that let the agent pipeline run on a real call without cloud services.</summary>
internal sealed class ScriptedSpeechToText(params string[] utterances) : ISpeechToText
{
    public string Name => "scripted";

    /// <summary>Emits the next utterance each time the caller stops talking (detected with the VAD).</summary>
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var vad = new VoiceActivityDetector(16000) { SilenceHangoverMs = 300 };
        var index = 0;
        var wasSpeaking = false;
        await foreach (var chunk in audio.WithCancellation(cancellationToken))
        {
            var speaking = vad.Process(Pcm.AsSamples(chunk.Data.Span));
            if (speaking && !wasSpeaking && index < utterances.Length)
            {
                yield return new TranscriptSegment("…", IsFinal: false);
            }

            if (wasSpeaking && !speaking && index < utterances.Length)
            {
                yield return new TranscriptSegment(utterances[index++], IsFinal: true);
            }

            wasSpeaking = speaking;
        }
    }

    public Task<string> TranscribeOnceAsync(ReadOnlyMemory<byte> pcm, int sampleRate, SpeechRecognitionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(utterances.FirstOrDefault() ?? string.Empty);
}

internal sealed class ToneTextToSpeech : ITextToSpeech
{
    public List<string> Spoken { get; } = [];

    public string Name => "tone";

    public int PreferredSampleRate => 16000;

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, SpeechSynthesisOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Spoken)
        {
            Spoken.Add(text);
        }

        // 60 ms of tone per character, capped, delivered in 100 ms chunks like a real provider.
        var total = Math.Min(text.Length * 60, 3000);
        for (var sent = 0; sent < total; sent += 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AudioChunk(Pcm.ToBytes(TestHelpers.Tone(16000, 100, 500)), 16000);
            await Task.Yield();
        }
    }
}

internal sealed class EchoChatClient : IChatClient
{
    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply(messages))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in Reply(messages).Split(' '))
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            await Task.Yield();
        }
    }

    private string Reply(IEnumerable<ChatMessage> messages)
    {
        var last = messages.Last(m => m.Role == ChatRole.User).Text;
        Prompts.Add(last);
        return $"Anda bilang {last}. Ada lagi?";
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class AgentTests
{
    [Fact]
    public async Task VoiceAgentGreetsListensAndAnswers()
    {
        await using var pair = await LoopbackPair.ConnectAsync("customer", "agent");
        var chat = new EchoChatClient();
        var tts = new ToneTextToSpeech();
        var store = new InMemoryConversationStore();
        await using var agent = new VoiceAgent(chat, new ScriptedSpeechToText("cek saldo"), tts, new VoiceAgentOptions
        {
            Greeting = "Selamat datang.",
            SilencePrompt = TimeSpan.Zero,
            SilenceHangup = TimeSpan.Zero,
            EnableCallControlTools = false,
            ConversationKey = "customer-1",
        }, store);

        var heard = 0;
        pair.CallerLeg.AudioReceived += (_, direction, _, samples) =>
        {
            if (direction == AudioDirection.Inbound && Pcm.Rms(samples) > 0.05)
            {
                Interlocked.Increment(ref heard);
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = agent.RunAsync(pair.CalleeLeg, cts.Token);

        // Wait for the greeting to finish, then the customer speaks for a moment.
        await TestHelpers.WaitUntilAsync(() => heard > 10 && !agent.IsSpeaking, TimeSpan.FromSeconds(10), "greeting");
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 700, 300, 7000), 16000);

        await TestHelpers.WaitUntilAsync(() => agent.Turns.Any(t => t.Role == "assistant"), TimeSpan.FromSeconds(10), "agent answer");
        Assert.Equal("cek saldo", Assert.Single(chat.Prompts));
        Assert.Contains(tts.Spoken, s => s.Contains("cek saldo", StringComparison.Ordinal));

        await pair.CallerLeg.HangupAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var saved = await store.LoadAsync("customer-1");
        Assert.Equal(["user", "assistant"], saved.Select(t => t.Role));
    }

    [Fact]
    public async Task CallerCanBargeIn()
    {
        await using var pair = await LoopbackPair.ConnectAsync("customer", "agent");
        var tts = new ToneTextToSpeech();
        await using var agent = new VoiceAgent(new EchoChatClient(), new ScriptedSpeechToText("stop"), tts, new VoiceAgentOptions
        {
            Greeting = new string('x', 50), // three seconds of prompt
            SilencePrompt = TimeSpan.Zero,
            SilenceHangup = TimeSpan.Zero,
            EnableCallControlTools = false,
        });

        var interrupted = false;
        agent.Interrupted += (_, _) => interrupted = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = agent.RunAsync(pair.CalleeLeg, cts.Token);

        await TestHelpers.WaitUntilAsync(() => agent.IsSpeaking, TimeSpan.FromSeconds(5), "agent speaking");
        await Task.Delay(500);
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 600, 300, 7000), 16000);

        await TestHelpers.WaitUntilAsync(() => interrupted, TimeSpan.FromSeconds(5), "barge-in");
        await pair.CallerLeg.HangupAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task JsonConversationStorePersistsTurns()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voipnet-conv-{Guid.NewGuid():N}");
        var store = new JsonFileConversationStore(dir);
        await store.SaveAsync("sip:0812@pbx", [new ConversationTurn("user", "halo", DateTimeOffset.UtcNow)]);
        var loaded = await store.LoadAsync("sip:0812@pbx");
        Directory.Delete(dir, true);
        Assert.Equal("halo", Assert.Single(loaded).Text);
    }
}
