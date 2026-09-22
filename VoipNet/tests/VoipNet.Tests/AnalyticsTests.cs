using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Analytics;
using VoipNet.AI.Speech;
using Xunit;

namespace VoipNet.Tests;

/// <summary>A model that answers with whatever the test hands it, and keeps the prompt it was given.</summary>
internal sealed class ScriptedChatClient(string answer) : IChatClient
{
    public List<string> Prompts { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Prompts.AddRange(messages.Select(m => m.Text));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Prompts.AddRange(messages.Select(m => m.Text));
        yield return new ChatResponseUpdate(ChatRole.Assistant, answer);
        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class AnalyticsTests
{
    private const string Transcript = """
        Caller: Halo, paket saya belum sampai padahal sudah seminggu.
        Agent: Mohon maaf atas keterlambatannya. Boleh saya cek nomor resinya?
        Caller: Nomornya JNE123456.
        Agent: Terima kasih. Paket sedang di gudang Surabaya dan akan dikirim besok.
        """;

    [Fact]
    public async Task AnalysisIsReadEvenWhenTheModelWrapsItsJson()
    {
        // Models like to explain themselves and fence their JSON; the analysis must survive both.
        var chat = new ScriptedChatClient("""
            Here is the analysis:
            ```json
            {
              "summary": "Penelepon menanyakan paket yang terlambat.",
              "sentiment": "NEGATIVE",
              "topics": ["pengiriman", "keterlambatan", ""],
              "actionItems": ["Kirim paket dari gudang Surabaya"],
              "qualityScore": "88",
              "qualityNotes": "Agen sopan dan menawarkan solusi.",
              "followUpNeeded": true
            }
            ```
            """);
        var analyzer = new CallAnalyzer(chat, options: new CallAnalyzerOptions { Language = "Indonesian" });

        var analysis = await analyzer.AnalyzeTranscriptAsync(Transcript);

        Assert.Equal("Penelepon menanyakan paket yang terlambat.", analysis.Summary);
        Assert.Equal("negative", analysis.Sentiment);
        Assert.Equal(["pengiriman", "keterlambatan"], analysis.Topics);
        Assert.Single(analysis.ActionItems);
        Assert.Equal(88, analysis.QualityScore);
        Assert.True(analysis.FollowUpNeeded);
        Assert.Equal(Transcript, analysis.Transcript);
        Assert.Contains("Indonesian", chat.Prompts[0]);
    }

    [Fact]
    public async Task AnAnswerThatIsNotJsonStillReportsWhatTheModelSaid()
    {
        var chat = new ScriptedChatClient("The line was too noisy to judge.");
        var analyzer = new CallAnalyzer(chat);

        var analysis = await analyzer.AnalyzeTranscriptAsync(Transcript);

        Assert.Equal("The line was too noisy to judge.", analysis.Summary);
        Assert.Equal("neutral", analysis.Sentiment);
        Assert.Empty(analysis.Topics);
        Assert.Equal(0, analysis.QualityScore);
    }

    [Fact]
    public async Task AgentTurnsAreTurnedIntoATranscript()
    {
        var chat = new ScriptedChatClient("""{"summary": "ok", "sentiment": "positive", "qualityScore": 70}""");
        var analyzer = new CallAnalyzer(chat);
        var turns = new List<ConversationTurn>
        {
            new("user", "Jam berapa tokonya buka?", DateTimeOffset.UtcNow),
            new("assistant", "Toko buka pukul sembilan pagi.", DateTimeOffset.UtcNow),
        };

        var analysis = await analyzer.AnalyzeAsync(turns);

        Assert.Equal("positive", analysis.Sentiment);
        Assert.Equal(70, analysis.QualityScore);
        Assert.Contains("Caller: Jam berapa tokonya buka?", analysis.Transcript);
        Assert.Contains("Agent: Toko buka pukul sembilan pagi.", analysis.Transcript);
    }

    [Fact]
    public async Task AnalysingARecordingNeedsARecogniser()
    {
        var analyzer = new CallAnalyzer(new ScriptedChatClient("{}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeRecordingAsync("call.wav"));
    }

    [Fact]
    public async Task AgentAssistTranscribesTheCallerAndSuggestsReplies()
    {
        await using var pair = await LoopbackPair.ConnectAsync("customer", "human-agent");
        var chat = new ScriptedChatClient("""
            {"suggestions": [
              {"text": "Saya cek dulu status pesanannya ya, Pak.", "reason": "caller asked about an order"},
              {"text": "Boleh saya minta nomor pesanannya?", "reason": "the order number is missing"}
            ]}
            """);
        var assist = new AgentAssist(chat, new ScriptedSpeechToText("pesanan saya di mana"), new AgentAssistOptions
        {
            Language = "Indonesian",
            MinimumInterval = TimeSpan.Zero,
            Knowledge = "Pengiriman reguler 2-3 hari kerja.",
        });

        var lines = new List<TranscriptLine>();
        IReadOnlyList<AssistSuggestion> suggestions = [];
        assist.TranscriptUpdated += (_, line) => { lock (lines) lines.Add(line); };
        assist.SuggestionsUpdated += (_, s) => suggestions = s;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = assist.RunAsync(pair.CalleeLeg, cts.Token);
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 700, 500), 16000);

        await TestHelpers.WaitAsync(() => suggestions.Count > 0 ? "ready" : null, TimeSpan.FromSeconds(15), "suggestions");
        await pair.CallerLeg.HangupAsync();
        await run;

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("Saya cek dulu status pesanannya ya, Pak.", suggestions[0].Text);
        Assert.Contains("order", suggestions[0].Reason);
        lock (lines)
        {
            Assert.Contains(lines, l => l is { Speaker: "caller", IsFinal: true, Text: "pesanan saya di mana" });
        }

        Assert.Contains(assist.Transcript, t => t.Role == "user" && t.Text == "pesanan saya di mana");
        // The knowledge and the transcript both reach the model, and nothing is sent to the caller.
        Assert.Contains("Pengiriman reguler", chat.Prompts[0]);
        Assert.Contains("Caller: pesanan saya di mana", chat.Prompts[1]);
    }

    [Theory]
    [InlineData("""["Halo, ada yang bisa dibantu?", "Boleh minta nomor pesanan?"]""", 2)]
    [InlineData("- Halo, ada yang bisa dibantu?\n- Boleh minta nomor pesanan?", 2)]
    [InlineData("", 0)]
    public void SuggestionsAreReadFromWhateverShapeTheModelReturns(string answer, int expected)
    {
        var suggestions = AgentAssist.ParseSuggestions(answer, 5);
        Assert.Equal(expected, suggestions.Count);
        if (expected > 0)
        {
            Assert.Equal("Halo, ada yang bisa dibantu?", suggestions[0].Text);
        }
    }
}
