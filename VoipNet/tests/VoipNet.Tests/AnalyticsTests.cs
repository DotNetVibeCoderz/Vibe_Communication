using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Analytics;
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
}
