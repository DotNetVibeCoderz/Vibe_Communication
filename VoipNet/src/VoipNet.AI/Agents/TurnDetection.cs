using Microsoft.Extensions.AI;

namespace VoipNet.AI.Agents;

/// <summary>
/// Decides whether a caller has finished speaking or is only pausing. Silence alone is a poor judge:
/// "my number is…" and "yes" both end in silence, and only one of them is a finished thought.
/// </summary>
public interface ITurnDetector
{
    /// <summary>
    /// Says whether the agent should answer now.
    /// </summary>
    /// <param name="utterance">Everything the caller has said since the agent last spoke.</param>
    /// <param name="history">The conversation so far, oldest first, for context.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>True when the caller is done and the agent should answer.</returns>
    Task<bool> IsCompleteAsync(string utterance, IReadOnlyList<ConversationTurn> history, CancellationToken cancellationToken = default);
}

/// <summary>How <see cref="SemanticTurnDetector"/> asks.</summary>
public sealed class SemanticTurnDetectorOptions
{
    /// <summary>Extra guidance for the judgement, for example the shape of the numbers callers read out.</summary>
    public string? AdditionalInstructions { get; set; }

    /// <summary>How long to wait for the model before answering anyway.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Turns of history to show the model.</summary>
    public int HistoryTurns { get; set; } = 4;
}

/// <summary>
/// Asks a model whether the caller's sentence is finished. Use a small, fast model: this runs between
/// the caller's last word and the agent's first, where every hundred milliseconds is audible.
/// </summary>
/// <remarks>
/// The detector fails open — a model that errors or takes too long is treated as "the caller is done",
/// because leaving a caller in silence is worse than answering half a beat early.
/// </remarks>
public sealed class SemanticTurnDetector(IChatClient chat, SemanticTurnDetectorOptions? options = null) : ITurnDetector
{
    private readonly SemanticTurnDetectorOptions _options = options ?? new SemanticTurnDetectorOptions();

    /// <inheritdoc/>
    public async Task<bool> IsCompleteAsync(string utterance, IReadOnlyList<ConversationTurn> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var context = string.Join(
            "\n",
            history.TakeLast(_options.HistoryTurns).Select(t => $"{(t.Role == "assistant" ? "Agent" : "Caller")}: {t.Text}"));
        var system =
            $"""
            You listen to a phone call and decide one thing: has the caller finished their sentence, or
            are they pausing mid-thought? Answer with exactly one word, YES if they are finished and the
            agent should reply, NO if more is coming (a half-spoken number, "I would like to", a trailing
            "and", a list still being read out).
            {_options.AdditionalInstructions}
            """;
        var user = context.Length > 0 ? $"Conversation so far:\n{context}\n\nThe caller just said: \"{utterance}\"" : $"The caller just said: \"{utterance}\"";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.System, system), new ChatMessage(ChatRole.User, user)],
                new ChatOptions { Temperature = 0, MaxOutputTokens = 3 },
                timeout.Token).ConfigureAwait(false);
            var answer = response.Text?.Trim().TrimEnd('.').ToUpperInvariant() ?? string.Empty;
            return !answer.StartsWith("NO", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            // Better to answer a moment early than to leave the caller listening to nothing.
            return true;
        }
    }
}
