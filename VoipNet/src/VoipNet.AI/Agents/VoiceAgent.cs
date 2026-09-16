using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.AI.Speech;

namespace VoipNet.AI.Agents;

/// <summary>
/// Runs a spoken conversation on a call: it listens, thinks and answers, and stops talking the
/// moment the caller interrupts. One agent instance handles one call.
/// </summary>
/// <example>
/// <code>
/// var agent = new VoiceAgent(chatClient, speechToText, textToSpeech, new VoiceAgentOptions
/// {
///     Greeting = "Halo, ada yang bisa saya bantu?",
///     SystemPrompt = "Kamu adalah agen layanan pelanggan yang ramah.",
/// });
/// await agent.RunAsync(call);
/// </code>
/// </example>
public sealed class VoiceAgent : IAsyncDisposable
{
    private readonly IChatClient _chat;
    private readonly ISpeechToText _speechToText;
    private readonly ITextToSpeech _textToSpeech;
    private readonly VoiceAgentOptions _options;
    private readonly IConversationStore? _store;
    private readonly ILogger _logger;
    private readonly List<ChatMessage> _history = [];
    private readonly List<ConversationTurn> _turns = [];
    private readonly SemaphoreSlim _speaking = new(1, 1);

    private CancellationTokenSource? _speechCancellation;
    private VoipCall? _call;
    private DateTimeOffset _lastCallerActivity = DateTimeOffset.UtcNow;
    private bool _promptedForSilence;
    private bool _disposed;

    /// <summary>Creates an agent.</summary>
    /// <param name="chatClient">Model used to think. Wrap it with function invocation to enable tools.</param>
    /// <param name="speechToText">Recogniser.</param>
    /// <param name="textToSpeech">Synthesiser.</param>
    /// <param name="options">Agent behaviour.</param>
    /// <param name="conversationStore">Optional store that remembers callers between calls.</param>
    /// <param name="logger">Optional logger.</param>
    public VoiceAgent(
        IChatClient chatClient,
        ISpeechToText speechToText,
        ITextToSpeech textToSpeech,
        VoiceAgentOptions? options = null,
        IConversationStore? conversationStore = null,
        ILogger<VoiceAgent>? logger = null)
    {
        _chat = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _speechToText = speechToText ?? throw new ArgumentNullException(nameof(speechToText));
        _textToSpeech = textToSpeech ?? throw new ArgumentNullException(nameof(textToSpeech));
        _options = options ?? new VoiceAgentOptions();
        _store = conversationStore;
        _logger = logger ?? NullLogger<VoiceAgent>.Instance;
    }

    /// <summary>The call this agent is handling, once <see cref="RunAsync"/> has started.</summary>
    public VoipCall? Call => _call;

    /// <summary>Everything said so far, in order.</summary>
    public IReadOnlyList<ConversationTurn> Turns => _turns.ToArray();

    /// <summary>True while the agent is speaking.</summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>Set by a tool or by the application to end the call after the current answer.</summary>
    public bool ShouldHangUp { get; set; }

    /// <summary>Raised for each final thing the caller said.</summary>
    public event EventHandler<string>? CallerSaid;

    /// <summary>Raised for each sentence the agent speaks.</summary>
    public event EventHandler<string>? AgentSaid;

    /// <summary>Raised when the caller interrupts the agent.</summary>
    public event EventHandler? Interrupted;

    /// <summary>Runs the conversation until the call ends or the token is cancelled.</summary>
    /// <param name="call">A connected call.</param>
    /// <param name="cancellationToken">Stops the agent.</param>
    public async Task RunAsync(VoipCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _call = call;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.MaxCallDuration > TimeSpan.Zero)
        {
            lifetime.CancelAfter(_options.MaxCallDuration);
        }

        void OnState(object? sender, CallStateEventArgs e)
        {
            if (e.State == CallState.Terminated)
            {
                lifetime.Cancel();
            }
        }

        call.StateChanged += OnState;

        var key = _options.ConversationKey ?? call.RemoteUri;
        await RestoreHistoryAsync(key, lifetime.Token).ConfigureAwait(false);

        var watchdog = WatchSilenceAsync(lifetime.Token);

        // The agent's turn (greeting or answer) runs beside the recognition loop, so the loop keeps
        // listening while the agent talks — that is what makes interruption possible.
        Task turn = Task.CompletedTask;
        CancellationTokenSource? turnCancellation = null;

        void StartTurn(Func<CancellationToken, Task> work)
        {
            turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = turnCancellation.Token;
            turn = Task.Run(async () =>
            {
                try
                {
                    await work(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Interrupted or the call ended.
                }
            }, CancellationToken.None);
        }

        async Task CancelTurnAsync()
        {
            if (turnCancellation is { } cancellation)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                await turn.ConfigureAwait(false);
                cancellation.Dispose();
                turnCancellation = null;
            }
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_options.Greeting))
            {
                StartTurn(token => SpeakAsync(_options.Greeting, token));
            }

            var options = new SpeechRecognitionOptions
            {
                Language = _options.SpeechToText.Language ?? _options.Language,
                Model = _options.SpeechToText.Model,
                InterimResults = _options.BargeIn,
                EndpointingMs = _options.SpeechToText.EndpointingMs,
            };
            foreach (var hint in _options.SpeechToText.Hints)
            {
                options.Hints.Add(hint);
            }

            await foreach (var segment in _speechToText
                .TranscribeAsync(call.ReadAudioChunksAsync(AudioDirection.Inbound, lifetime.Token), options, lifetime.Token)
                .ConfigureAwait(false))
            {
                if (lifetime.IsCancellationRequested || !call.IsActive)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(segment.Text))
                {
                    continue;
                }

                _lastCallerActivity = DateTimeOffset.UtcNow;
                _promptedForSilence = false;

                if (!segment.IsFinal)
                {
                    if (_options.BargeIn && IsSpeaking)
                    {
                        StopSpeaking();
                        await CancelTurnAsync().ConfigureAwait(false);
                        Interrupted?.Invoke(this, EventArgs.Empty);
                    }

                    continue;
                }

                // A new utterance supersedes whatever the agent was still doing.
                await CancelTurnAsync().ConfigureAwait(false);
                CallerSaid?.Invoke(this, segment.Text);
                Record("user", segment.Text);
                var said = segment.Text;
                StartTurn(async token =>
                {
                    await RespondAsync(said, token).ConfigureAwait(false);
                    if (ShouldHangUp)
                    {
                        await WaitUntilSpokenAsync(token).ConfigureAwait(false);
                        call.Hangup();
                    }
                });
            }

            // Recognition ended (for example the audio stream closed): let the last answer finish.
            await turn.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call ended or the caller cancelled; fall through to cleanup.
        }
        finally
        {
            call.StateChanged -= OnState;
            StopSpeaking();
            await lifetime.CancelAsync().ConfigureAwait(false);
            await turn.ConfigureAwait(false);
            turnCancellation?.Dispose();
            await watchdog.ConfigureAwait(false);
            await PersistHistoryAsync(key).ConfigureAwait(false);
        }
    }

    /// <summary>Asks the model for an answer and speaks it sentence by sentence.</summary>
    /// <param name="userText">What the caller said.</param>
    /// <param name="cancellationToken">Stops the turn.</param>
    public async Task RespondAsync(string userText, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        _history.Add(new ChatMessage(ChatRole.User, userText));

        var chatOptions = CloneOptions();
        var answer = new StringBuilder();
        var sentence = new StringBuilder();

        try
        {
            await foreach (var update in _chat.GetStreamingResponseAsync(_history, chatOptions, cancellationToken).ConfigureAwait(false))
            {
                var text = update.Text;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                answer.Append(text);
                sentence.Append(text);

                // Speak as soon as a sentence is complete, so the caller does not wait for the whole answer.
                if (TryTakeSentence(sentence, out var ready))
                {
                    await SpeakAsync(ready, cancellationToken).ConfigureAwait(false);
                }
            }

            if (sentence.Length > 0)
            {
                await SpeakAsync(sentence.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupted: keep what was already generated so the model knows where it stopped.
            RecordAnswer(answer.ToString().Trim() is { Length: > 0 } partial ? partial + " …" : string.Empty);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "The model call failed");
            await SpeakAsync("Maaf, sistem sedang bermasalah. Mohon tunggu sebentar.", cancellationToken).ConfigureAwait(false);
            return;
        }

        RecordAnswer(answer.ToString().Trim());
    }

    private void RecordAnswer(string reply)
    {
        if (reply.Length > 0)
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, reply));
            Record("assistant", reply);
        }
    }

    /// <summary>Speaks a line immediately, cancelling anything the agent was saying.</summary>
    /// <param name="text">Text to speak.</param>
    /// <param name="cancellationToken">Stops speaking.</param>
    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || _call is not { IsActive: true } call)
        {
            return;
        }

        await _speaking.WaitAsync(cancellationToken).ConfigureAwait(false);
        var speech = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _speechCancellation = speech;
        IsSpeaking = true;
        try
        {
            AgentSaid?.Invoke(this, text.Trim());
            await _textToSpeech.SpeakAsync(call, text, TtsOptions(), speech.Token).ConfigureAwait(false);

            // Wait for the audio that is still queued, unless the caller interrupts.
            while (call.IsActive && call.QueuedAudioMs > 60 && !speech.IsCancellationRequested)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Interrupted by the caller: that is the point of barge-in.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            _logger.LogError(ex, "Speech synthesis failed");
        }
        finally
        {
            IsSpeaking = false;
            _speechCancellation = null;
            speech.Dispose();
            _speaking.Release();
        }
    }

    /// <summary>Stops the agent mid-sentence and clears audio already queued in the call.</summary>
    public void StopSpeaking()
    {
        var cancellation = _speechCancellation;
        if (cancellation is not null && !cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }

        try
        {
            _call?.ClearAudio();
        }
        catch (VoipException)
        {
            // The call may have ended already.
        }
    }

    /// <summary>Hands the call to a human agent or another destination.</summary>
    /// <param name="target">Destination URI; defaults to <see cref="VoiceAgentOptions.HandoffTarget"/>.</param>
    /// <param name="announcement">Optional sentence spoken before the transfer.</param>
    /// <param name="cancellationToken">Cancels the announcement.</param>
    public async Task<bool> HandOffAsync(string? target = null, string? announcement = null, CancellationToken cancellationToken = default)
    {
        var destination = target ?? _options.HandoffTarget;
        if (_call is not { IsActive: true } call || string.IsNullOrWhiteSpace(destination))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(announcement))
        {
            await SpeakAsync(announcement, cancellationToken).ConfigureAwait(false);
            await WaitUntilSpokenAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Handing call {CallId} to {Target}", call.Id, destination);
        call.Transfer(destination);
        return true;
    }

    private async Task WaitUntilSpokenAsync(CancellationToken cancellationToken)
    {
        while (_call is { IsActive: true } call && call.QueuedAudioMs > 60 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ChatOptions CloneOptions()
    {
        var options = _options.ChatOptions.Clone();
        var instructions = new StringBuilder(_options.SystemPrompt);
        if (_options.Language is { Length: > 0 } language)
        {
            instructions.Append("\n\nReply in ").Append(language).Append('.');
        }

        options.Instructions = string.IsNullOrEmpty(options.Instructions)
            ? instructions.ToString()
            : $"{instructions}\n\n{options.Instructions}";

        if (_options.EnableCallControlTools && _call is { } call)
        {
            var tools = options.Tools is null ? [] : new List<AITool>(options.Tools);
            tools.AddRange(CallControlTools.Create(call, this));
            options.Tools = tools;
        }

        return options;
    }

    private SpeechSynthesisOptions TtsOptions() => new()
    {
        Voice = _options.TextToSpeech.Voice,
        Model = _options.TextToSpeech.Model,
        Language = _options.TextToSpeech.Language ?? _options.Language,
        Speed = _options.TextToSpeech.Speed,
        SampleRate = _options.TextToSpeech.SampleRate,
    };

    private static bool TryTakeSentence(StringBuilder buffer, out string sentence)
    {
        const int longEnough = 160;
        var text = buffer.ToString();
        var cut = text.LastIndexOfAny(['.', '!', '?', '\n', ';']);
        if (cut < 0 && text.Length >= longEnough)
        {
            cut = text.LastIndexOf(',');
        }

        if (cut < 0 || cut + 1 < 12)
        {
            sentence = string.Empty;
            return false;
        }

        sentence = text[..(cut + 1)].Trim();
        buffer.Remove(0, cut + 1);
        return sentence.Length > 0;
    }

    private async Task WatchSilenceAsync(CancellationToken cancellationToken)
    {
        if (_options.SilencePrompt <= TimeSpan.Zero && _options.SilenceHangup <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                if (IsSpeaking || _call is not { IsActive: true } call)
                {
                    continue;
                }

                var silence = DateTimeOffset.UtcNow - _lastCallerActivity;
                if (_options.SilenceHangup > TimeSpan.Zero && silence > _options.SilenceHangup)
                {
                    _logger.LogInformation("Ending call {CallId}: the caller has been silent for {Silence}", call.Id, silence);
                    call.Hangup();
                    return;
                }

                if (_options.SilencePrompt > TimeSpan.Zero && !_promptedForSilence && silence > _options.SilencePrompt)
                {
                    _promptedForSilence = true;
                    await SpeakAsync(_options.SilencePromptText, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The call ended.
        }
    }

    private void Record(string role, string text)
    {
        _turns.Add(new ConversationTurn(role, text, DateTimeOffset.UtcNow));
    }

    private async Task RestoreHistoryAsync(string key, CancellationToken cancellationToken)
    {
        _history.Clear();
        if (_store is null || _options.RestoreTurns <= 0)
        {
            return;
        }

        try
        {
            var stored = await _store.LoadAsync(key, cancellationToken).ConfigureAwait(false);
            var recent = stored.TakeLast(_options.RestoreTurns).ToList();
            _history.AddRange(recent.ToChatMessages());
            if (recent.Count > 0)
            {
                _logger.LogInformation("Restored {Count} turns for {Key}", recent.Count, key);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Cannot restore the conversation for {Key}", key);
        }
    }

    private async Task PersistHistoryAsync(string key)
    {
        if (_store is null || _turns.Count == 0)
        {
            return;
        }

        try
        {
            var stored = await _store.LoadAsync(key).ConfigureAwait(false);
            await _store.SaveAsync(key, [.. stored, .. _turns]).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Cannot save the conversation for {Key}", key);
        }
    }

    /// <summary>Stops the agent and releases its resources.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        StopSpeaking();
        _speaking.Dispose();
        return ValueTask.CompletedTask;
    }
}
