using Microsoft.Extensions.AI;
using VoipNet.AI.Speech;

namespace VoipNet.AI.Agents;

/// <summary>How a <see cref="VoiceAgent"/> behaves on a call.</summary>
public sealed class VoiceAgentOptions
{
    /// <summary>Instructions that define the agent's role, tone and limits.</summary>
    public string SystemPrompt { get; set; } =
        "You are a helpful phone agent. Keep answers short and natural, because the caller hears them. " +
        "Ask one question at a time and confirm anything important.";

    /// <summary>Spoken as soon as the call connects. Leave empty to let the caller speak first.</summary>
    public string Greeting { get; set; } = string.Empty;

    /// <summary>Spoken when the caller has been silent for <see cref="SilencePrompt"/>.</summary>
    public string SilencePromptText { get; set; } = "Are you still there?";

    /// <summary>How long to wait before the silence prompt. Zero disables it.</summary>
    public TimeSpan SilencePrompt { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Ends the call after this much total silence. Zero disables it.</summary>
    public TimeSpan SilenceHangup { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Upper bound on call length. Zero disables it.</summary>
    public TimeSpan MaxCallDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Stop the agent from talking as soon as the caller starts speaking.</summary>
    public bool BargeIn { get; set; } = true;

    /// <summary>Language tag passed to the speech services, for example <c>id-ID</c>.</summary>
    public string? Language { get; set; }

    /// <summary>Model options: temperature, tools, token limits.</summary>
    public ChatOptions ChatOptions { get; set; } = new() { Temperature = 0.4f };

    /// <summary>Recognition options.</summary>
    public SpeechRecognitionOptions SpeechToText { get; set; } = new();

    /// <summary>Synthesis options.</summary>
    public SpeechSynthesisOptions TextToSpeech { get; set; } = new();

    /// <summary>Destination used by the built-in transfer tool and <see cref="VoiceAgent.HandOffAsync"/>.</summary>
    public string? HandoffTarget { get; set; }

    /// <summary>Give the model call control tools: transfer, hang up, send DTMF and hold.</summary>
    public bool EnableCallControlTools { get; set; } = true;

    /// <summary>Conversation key for persistence. Defaults to the caller's number.</summary>
    public string? ConversationKey { get; set; }

    /// <summary>Number of previous turns to restore from the store when a caller returns.</summary>
    public int RestoreTurns { get; set; } = 20;
}

/// <summary>Something the agent or the caller said.</summary>
/// <param name="Role">Who spoke.</param>
/// <param name="Text">What was said.</param>
/// <param name="At">When it was said.</param>
public sealed record ConversationTurn(string Role, string Text, DateTimeOffset At);
