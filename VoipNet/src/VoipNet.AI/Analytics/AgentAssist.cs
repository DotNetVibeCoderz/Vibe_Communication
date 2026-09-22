using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.AI.Agents;
using VoipNet.AI.Speech;

namespace VoipNet.AI.Analytics;

/// <summary>One line of a live transcript.</summary>
/// <param name="Speaker">Who spoke: <c>caller</c> or <c>agent</c>.</param>
/// <param name="Text">What was said, or the current hypothesis while they are still speaking.</param>
/// <param name="IsFinal">False while the recogniser may still change its mind.</param>
/// <param name="At">When the line was recognised.</param>
public sealed record TranscriptLine(string Speaker, string Text, bool IsFinal, DateTimeOffset At);

/// <summary>Something the agent could say next, and why.</summary>
/// <param name="Text">The suggested reply, ready to read out.</param>
/// <param name="Reason">Why it is being suggested, for the agent to judge it.</param>
public sealed record AssistSuggestion(string Text, string Reason = "");

/// <summary>How the assistant behaves.</summary>
public sealed class AgentAssistOptions
{
    /// <summary>Language for the suggestions, as a name the model understands.</summary>
    public string Language { get; set; } = "the language of the call";

    /// <summary>How many replies to offer at a time.</summary>
    public int SuggestionCount { get; set; } = 3;

    /// <summary>Facts the agent may rely on: prices, policies, opening hours.</summary>
    public string? Knowledge { get; set; }

    /// <summary>Extra house rules for the suggestions, appended to the prompt.</summary>
    public string? AdditionalInstructions { get; set; }

    /// <summary>Shortest gap between two trips to the model, so a talkative caller does not flood it.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Transcribe what the agent says as well, which costs a second recognition stream.</summary>
    public bool TranscribeAgent { get; set; }

    /// <summary>Recognition options for the call audio.</summary>
    public SpeechRecognitionOptions Recognition { get; set; } = new();
}

/// <summary>
/// Listens to a call a human agent is handling: it writes the transcript as the call goes on and, each
/// time the caller finishes a sentence, offers replies the agent can read out or ignore. Nothing is
/// ever sent to the caller — the agent stays in charge of the conversation.
/// </summary>
public sealed class AgentAssist
{
    private readonly IChatClient _chat;
    private readonly ISpeechToText _speechToText;
    private readonly AgentAssistOptions _options;
    private readonly ILogger _logger;
    private readonly List<ConversationTurn> _transcript = [];
    private readonly Lock _gate = new();
    private DateTimeOffset _lastSuggestion = DateTimeOffset.MinValue;

    /// <summary>Creates an assistant.</summary>
    /// <param name="chat">Model that writes the suggestions.</param>
    /// <param name="speechToText">Recogniser for the call audio.</param>
    /// <param name="options">Language, knowledge and pacing.</param>
    /// <param name="logger">Optional logger.</param>
    public AgentAssist(IChatClient chat, ISpeechToText speechToText, AgentAssistOptions? options = null, ILogger<AgentAssist>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(speechToText);
        _chat = chat;
        _speechToText = speechToText;
        _options = options ?? new AgentAssistOptions();
        _logger = logger ?? NullLogger<AgentAssist>.Instance;
    }

    /// <summary>Raised for every recognised line, interim ones included.</summary>
    public event EventHandler<TranscriptLine>? TranscriptUpdated;

    /// <summary>Raised with fresh replies each time the caller finishes a sentence.</summary>
    public event EventHandler<IReadOnlyList<AssistSuggestion>>? SuggestionsUpdated;

    /// <summary>What has been said so far, oldest first.</summary>
    public IReadOnlyList<ConversationTurn> Transcript
    {
        get
        {
            lock (_gate)
            {
                return _transcript.ToArray();
            }
        }
    }

    /// <summary>The replies offered last.</summary>
    public IReadOnlyList<AssistSuggestion> Suggestions { get; private set; } = [];

    /// <summary>
    /// Follows a call until it ends or the token is cancelled. Transcription of both directions runs
    /// concurrently, so the agent's own words land in the transcript in the order they were said.
    /// </summary>
    /// <param name="call">The call the human agent is on.</param>
    /// <param name="cancellationToken">Stops following the call.</param>
    public async Task RunAsync(VoipCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnState(object? sender, CallStateEventArgs e)
        {
            if (e.State == CallState.Terminated)
            {
                lifetime.Cancel();
            }
        }

        call.StateChanged += OnState;
        try
        {
            var caller = ListenAsync(call, AudioDirection.Inbound, "caller", lifetime.Token);
            var agent = _options.TranscribeAgent
                ? ListenAsync(call, AudioDirection.Outbound, "agent", lifetime.Token)
                : Task.CompletedTask;
            await Task.WhenAll(caller, agent).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call ended or the caller stopped listening.
        }
        finally
        {
            call.StateChanged -= OnState;
        }
    }

    private async Task ListenAsync(VoipCall call, AudioDirection direction, string speaker, CancellationToken cancellationToken)
    {
        var options = new SpeechRecognitionOptions
        {
            Language = _options.Recognition.Language,
            Model = _options.Recognition.Model,
            InterimResults = _options.Recognition.InterimResults,
            EndpointingMs = _options.Recognition.EndpointingMs,
        };
        foreach (var hint in _options.Recognition.Hints)
        {
            options.Hints.Add(hint);
        }

        await foreach (var segment in _speechToText
            .TranscribeAsync(call.ReadAudioChunksAsync(direction, cancellationToken), options, cancellationToken)
            .ConfigureAwait(false))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                continue;
            }

            var line = new TranscriptLine(speaker, segment.Text.Trim(), segment.IsFinal, DateTimeOffset.UtcNow);
            TranscriptUpdated?.Invoke(this, line);
            if (!segment.IsFinal)
            {
                continue;
            }

            lock (_gate)
            {
                _transcript.Add(new ConversationTurn(speaker == "caller" ? "user" : "assistant", line.Text, line.At));
            }

            // Only the caller's sentences call for an answer; the agent's own words are context.
            if (speaker == "caller")
            {
                await SuggestAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Asks the model what the agent could say next, at most once per interval.</summary>
    private async Task SuggestAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSuggestion < _options.MinimumInterval)
        {
            return;
        }

        _lastSuggestion = now;
        var transcript = new StringBuilder();
        foreach (var turn in Transcript)
        {
            transcript.Append(turn.Role == "user" ? "Caller: " : "Agent: ").AppendLine(turn.Text);
        }

        var system =
            $$"""
            You sit beside a human call centre agent and suggest what they could say next. Answer with one
            JSON object and nothing else:
            {"suggestions": [{"text": string, "reason": string}]}

            Offer at most {{_options.SuggestionCount}} suggestions, each one sentence the agent can read out as it stands,
            in {{_options.Language}}. "reason" is a short note to the agent, not something to read out. Never invent
            facts that are not in the knowledge below or the transcript; when something is unknown, suggest asking.
            {{(string.IsNullOrWhiteSpace(_options.Knowledge) ? string.Empty : "Knowledge:\n" + _options.Knowledge)}}
            {{_options.AdditionalInstructions}}
            """;

        try
        {
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, system),
                new(ChatRole.User, transcript.ToString()),
            };
            var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.3f }, cancellationToken).ConfigureAwait(false);
            var suggestions = ParseSuggestions(response.Text, _options.SuggestionCount);
            if (suggestions.Count == 0)
            {
                return;
            }

            Suggestions = suggestions;
            SuggestionsUpdated?.Invoke(this, suggestions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A suggestion the agent never sees is not worth failing a live call over.
            _logger.LogWarning(ex, "Could not produce suggestions for this turn");
        }
    }

    /// <summary>
    /// Reads suggestions out of a model's answer, accepting the documented object, a bare array, or
    /// plain lines when the model forgets the shape. Public because applications that call a model
    /// themselves can reuse it.
    /// </summary>
    /// <param name="answer">What the model replied.</param>
    /// <param name="limit">Most suggestions to return.</param>
    public static IReadOnlyList<AssistSuggestion> ParseSuggestions(string? answer, int limit)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }

        var start = answer.IndexOfAny(['{', '[']);
        var end = answer.LastIndexOfAny(['}', ']']);
        if (start >= 0 && end > start)
        {
            try
            {
                using var document = JsonDocument.Parse(answer[start..(end + 1)]);
                var array = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement
                    : document.RootElement.TryGetProperty("suggestions", out var value) ? value : default;
                if (array.ValueKind == JsonValueKind.Array)
                {
                    return array.EnumerateArray()
                        .Select(Read)
                        .Where(s => s is not null)
                        .Select(s => s!)
                        .Take(limit)
                        .ToArray();
                }
            }
            catch (JsonException)
            {
                // Fall through to the plain-text reading below.
            }
        }

        return answer.Split('\n')
            .Select(line => line.TrimStart(' ', '-', '*', '•').Trim())
            .Where(line => line.Length > 1)
            .Take(limit)
            .Select(line => new AssistSuggestion(line))
            .ToArray();
    }

    private static AssistSuggestion? Read(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()?.Trim();
            return string.IsNullOrEmpty(text) ? null : new AssistSuggestion(text);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var suggestion = element.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        var reason = element.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString()?.Trim() ?? string.Empty : string.Empty;
        return string.IsNullOrEmpty(suggestion) ? null : new AssistSuggestion(suggestion, reason);
    }
}
