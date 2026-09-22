using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Speech;
using VoipNet.Audio;

namespace VoipNet.AI.Analytics;

/// <summary>What a finished call amounted to.</summary>
public sealed record CallAnalysis
{
    /// <summary>A few sentences on what the call was about and how it ended.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>How the caller sounded overall: <c>positive</c>, <c>neutral</c> or <c>negative</c>.</summary>
    public string Sentiment { get; init; } = "neutral";

    /// <summary>What the call was about, a few words each.</summary>
    public IReadOnlyList<string> Topics { get; init; } = [];

    /// <summary>What somebody still has to do after the call.</summary>
    public IReadOnlyList<string> ActionItems { get; init; } = [];

    /// <summary>Quality assurance score from 0 to 100 against the checklist that was asked for.</summary>
    public int QualityScore { get; init; }

    /// <summary>Why the call scored what it did.</summary>
    public string QualityNotes { get; init; } = string.Empty;

    /// <summary>True when the caller asked for something the agent could not settle.</summary>
    public bool FollowUpNeeded { get; init; }

    /// <summary>The transcript the analysis was made from.</summary>
    public string Transcript { get; init; } = string.Empty;
}

/// <summary>How a call is analysed.</summary>
public sealed class CallAnalyzerOptions
{
    /// <summary>Language to answer in, as a name the model understands, for example <c>Indonesian</c>.</summary>
    public string Language { get; set; } = "the language of the call";

    /// <summary>What the score should reward; one line per item.</summary>
    public IList<string> QualityChecklist { get; set; } =
    [
        "greeted the caller and identified the company",
        "understood the reason for the call",
        "answered accurately and completely",
        "stayed polite and calm throughout",
        "confirmed the next step before closing",
    ];

    /// <summary>Extra instructions appended to the prompt, for example house rules or regulatory wording.</summary>
    public string? AdditionalInstructions { get; set; }

    /// <summary>Recognition options used when a recording has to be transcribed first.</summary>
    public SpeechRecognitionOptions? Recognition { get; set; }
}

/// <summary>
/// Turns a finished call into something a supervisor can read: a summary, the caller's sentiment, the
/// topics, what still has to be done, and a score against a checklist. Works from a recording, from a
/// transcript, or from the turns a <see cref="VoiceAgent"/> already collected.
/// </summary>
/// <remarks>
/// The model is asked for JSON and its answer is parsed leniently — a model that wraps its JSON in a
/// code fence or adds a sentence before it still produces a usable analysis, because a report that
/// fails on formatting is worse than one missing a field.
/// </remarks>
public sealed class CallAnalyzer
{
    private readonly IChatClient _chat;
    private readonly ISpeechToText? _speechToText;
    private readonly CallAnalyzerOptions _options;

    /// <summary>Creates an analyser.</summary>
    /// <param name="chat">Model that writes the analysis.</param>
    /// <param name="speechToText">Recogniser used when analysing a recording; optional for transcripts.</param>
    /// <param name="options">Language, checklist and extra instructions.</param>
    public CallAnalyzer(IChatClient chat, ISpeechToText? speechToText = null, CallAnalyzerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        _speechToText = speechToText;
        _options = options ?? new CallAnalyzerOptions();
    }

    /// <summary>Transcribes a recorded call and analyses it.</summary>
    /// <param name="recordingPath">A WAV recording, as <c>RecordingService</c> and <c>CallRecorder</c> write.</param>
    /// <param name="cancellationToken">Cancels the transcription and the analysis.</param>
    public async Task<CallAnalysis> AnalyzeRecordingAsync(string recordingPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordingPath);
        if (_speechToText is null)
        {
            throw new InvalidOperationException("Analysing a recording needs a speech-to-text provider; pass one to the constructor.");
        }

        var (samples, sampleRate, channels) = WavReader.Read(recordingPath);
        var mono = channels > 1 ? Downmix(samples, channels) : samples;
        var pcm = new byte[mono.Length * 2];
        Buffer.BlockCopy(mono, 0, pcm, 0, pcm.Length);
        var transcript = await _speechToText
            .TranscribeOnceAsync(pcm, sampleRate, _options.Recognition, cancellationToken)
            .ConfigureAwait(false);
        return await AnalyzeTranscriptAsync(transcript, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Analyses the turns an agent already collected, which needs no transcription.</summary>
    /// <param name="turns">Conversation turns, oldest first.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    public Task<CallAnalysis> AnalyzeAsync(IReadOnlyList<ConversationTurn> turns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var transcript = new StringBuilder();
        foreach (var turn in turns)
        {
            var speaker = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Agent" : "Caller";
            transcript.Append(speaker).Append(": ").AppendLine(turn.Text);
        }

        return AnalyzeTranscriptAsync(transcript.ToString(), cancellationToken);
    }

    /// <summary>Analyses a transcript. Lines are expected to name who spoke, but plain text works too.</summary>
    /// <param name="transcript">What was said on the call.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    public async Task<CallAnalysis> AnalyzeTranscriptAsync(string transcript, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        var checklist = string.Join("\n", _options.QualityChecklist.Select(item => $"- {item}"));
        var system =
            $$"""
            You review recorded customer service calls. Read the transcript and answer with one JSON object and nothing else:
            {"summary": string, "sentiment": "positive" | "neutral" | "negative", "topics": string[], "actionItems": string[], "qualityScore": integer 0-100, "qualityNotes": string, "followUpNeeded": boolean}

            "sentiment" is how the caller felt, not the agent. "qualityScore" rates the agent against this checklist:
            {{checklist}}

            Write summary, qualityNotes, topics and actionItems in {{_options.Language}}. Keep the summary under 80 words.
            {{_options.AdditionalInstructions}}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, transcript),
        };
        var response = await _chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0 }, cancellationToken).ConfigureAwait(false);
        return Parse(response.Text, transcript);
    }

    /// <summary>
    /// Reads the model's answer. Anything outside the JSON object is ignored, and missing fields keep
    /// their defaults rather than failing the whole analysis.
    /// </summary>
    internal static CallAnalysis Parse(string? answer, string transcript)
    {
        var json = ExtractJson(answer);
        if (json is null)
        {
            return new CallAnalysis { Summary = answer?.Trim() ?? string.Empty, Transcript = transcript };
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new CallAnalysis
            {
                Summary = GetString(root, "summary"),
                Sentiment = Normalize(GetString(root, "sentiment")),
                Topics = GetStrings(root, "topics"),
                ActionItems = GetStrings(root, "actionItems"),
                QualityScore = Math.Clamp(GetInt(root, "qualityScore"), 0, 100),
                QualityNotes = GetString(root, "qualityNotes"),
                FollowUpNeeded = GetBool(root, "followUpNeeded"),
                Transcript = transcript,
            };
        }
        catch (JsonException)
        {
            return new CallAnalysis { Summary = answer?.Trim() ?? string.Empty, Transcript = transcript };
        }
    }

    /// <summary>Finds the outermost JSON object in an answer that may be fenced or prefaced.</summary>
    private static string? ExtractJson(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return null;
        }

        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        return start >= 0 && end > start ? answer[start..(end + 1)] : null;
    }

    private static string Normalize(string sentiment) => sentiment.Trim().ToLowerInvariant() switch
    {
        var s when s.Contains("positive") || s.Contains("positif") => "positive",
        var s when s.Contains("negative") || s.Contains("negatif") => "negative",
        _ => "neutral",
    };

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;

    /// <summary>Reads a number that the model may have written as a number, a decimal or a string.</summary>
    private static int GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var number) => (int)Math.Round(number),
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }

    private static bool GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private static IReadOnlyList<string> GetStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
    }

    /// <summary>Mixes a stereo recording down, because a transcript does not care who was on which channel.</summary>
    private static short[] Downmix(short[] samples, int channels)
    {
        var mono = new short[samples.Length / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++)
            {
                sum += samples[(i * channels) + c];
            }

            mono[i] = (short)Math.Clamp(sum / channels, short.MinValue, short.MaxValue);
        }

        return mono;
    }
}
