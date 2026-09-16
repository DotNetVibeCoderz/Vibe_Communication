using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using VoipNet.AI.Llm;
using VoipNet.AI.Speech;
using VoipNet.Audio;
using VoipNet.Enterprise.Ivr;

namespace VoipNet.IvrStudio.Services;

public enum TranscriptSpeaker
{
    System,
    Ivr,
    Caller,
    Ai,
}

public sealed record TranscriptLine(DateTimeOffset At, TranscriptSpeaker Speaker, string Text);

/// <summary>
/// Speaks prompts into the call as short syllable tones and reports the text, so the studio can show
/// what a real voice would say. Swap in ElevenLabs, OpenAI or Google speech for production.
/// </summary>
internal sealed class ReadAloudSpeech(Action<string> spoken) : ITextToSpeech
{
    public string Name => "studio";

    public int PreferredSampleRate => 16000;

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, SpeechSynthesisOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        spoken(text);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = 16 * Math.Clamp(word.Length * 55, 110, 450);
            var samples = new short[length + 1100];
            for (var i = 0; i < length; i++)
            {
                samples[i] = (short)(6000 * Math.Sin(Math.PI * i / length) * Math.Sin(2 * Math.PI * (180 + (word.Length * 8)) * i / 16000));
            }

            yield return new AudioChunk(Pcm.ToBytes(samples), 16000);
            await Task.Yield();
        }
    }
}

/// <summary>
/// One test call from the browser: a softphone endpoint dials an IVR endpoint, both in this process,
/// and the IVR runs the flow being edited. Keypad presses become real RFC 4733 DTMF.
/// </summary>
public sealed class TestLine : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly List<TranscriptLine> _transcript = [];
    private readonly Channel<string> _callerMessages = Channel.CreateUnbounded<string>();
    private VoipClient? _ivrEndpoint;
    private VoipClient? _phone;
    private VoipCall? _call;

    public TestLine(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public event Action? Changed;

    public IReadOnlyList<TranscriptLine> Transcript
    {
        get
        {
            lock (_transcript)
            {
                return [.. _transcript];
            }
        }
    }

    public bool InCall => _call is { IsActive: true };

    public bool AiMode { get; private set; }

    public bool AiConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);

    public string? Outcome { get; private set; }

    public string Dialled { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, string> Collected { get; private set; } = new Dictionary<string, string>();

    private string? Endpoint => _configuration["AI:Endpoint"] ?? Environment.GetEnvironmentVariable("VOIPNET_AI_ENDPOINT");

    private string? ApiKey => _configuration["AI:ApiKey"] ?? Environment.GetEnvironmentVariable("VOIPNET_AI_KEY");

    private string Model => _configuration["AI:Model"] ?? Environment.GetEnvironmentVariable("VOIPNET_AI_MODEL") ?? "gpt-5-mini";

    public async Task StartAsync(FlowModel model)
    {
        await HangUpAsync();
        lock (_transcript)
        {
            _transcript.Clear();
        }

        Outcome = null;
        AiMode = false;
        Dialled = string.Empty;
        Collected = new Dictionary<string, string>();

        IvrFlow flow;
        try
        {
            flow = model.Build(RunAiAgentAsync);
        }
        catch (InvalidOperationException ex)
        {
            Add(TranscriptSpeaker.System, $"The flow cannot run: {ex.Message}");
            return;
        }

        _ivrEndpoint = await StartEndpointAsync("ivr");
        _phone = await StartEndpointAsync("browser");

        _ivrEndpoint.IncomingCall += async (_, e) =>
        {
            try
            {
                await e.Call.AnswerAsync();
                var runner = new IvrRunner(new ReadAloudSpeech(text => Add(TranscriptSpeaker.Ivr, text)));
                var result = await runner.RunAsync(e.Call, flow);
                Collected = result.Context.Values;
                Outcome = result.Outcome switch
                {
                    IvrOutcome.Queued => $"Queued in “{result.Context.RequestedQueue}”",
                    IvrOutcome.Transferred => "Transferred",
                    IvrOutcome.HandedOff => "AI agent finished",
                    IvrOutcome.Completed => "IVR hung up",
                    _ => "Caller hung up",
                };
                Add(TranscriptSpeaker.System, $"{Outcome} · path {string.Join(" → ", result.Context.Path)}");
                if (result.Outcome is IvrOutcome.Queued && e.Call.IsActive)
                {
                    await Task.Delay(1500);
                    e.Call.Hangup();
                }
            }
            catch (Exception ex) when (ex is VoipException or ObjectDisposedException or TimeoutException)
            {
                Add(TranscriptSpeaker.System, "The test call ended.");
            }
        };

        Add(TranscriptSpeaker.System, $"Calling the IVR at {_ivrEndpoint.LocalAddress}…");
        _call = _phone.Call($"sip:ivr@{_ivrEndpoint.LocalAddress}");
        _call.StateChanged += (_, e) =>
        {
            if (e.State is CallState.Connected or CallState.Terminated)
            {
                Add(TranscriptSpeaker.System, e.State == CallState.Connected ? $"Connected · {_call.Codec}" : "Call ended");
            }
        };
    }

    public void Press(char digit)
    {
        if (_call is not { IsActive: true } call)
        {
            return;
        }

        Dialled += digit;
        call.SendDtmf(digit.ToString(), 100);
        Add(TranscriptSpeaker.Caller, $"pressed {digit}");
    }

    public void Say(string message)
    {
        if (!AiMode || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        Add(TranscriptSpeaker.Caller, message.Trim());
        _callerMessages.Writer.TryWrite(message.Trim());
    }

    public async Task HangUpAsync()
    {
        if (_call is { IsActive: true } call)
        {
            call.Hangup();
            await call.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        foreach (var endpoint in new[] { _phone, _ivrEndpoint })
        {
            if (endpoint is not null)
            {
                await endpoint.DisposeAsync();
            }
        }

        _phone = _ivrEndpoint = null;
        _call = null;
        AiMode = false;
        Changed?.Invoke();
    }

    /// <summary>
    /// The AI hand-off. In the studio the caller types instead of speaking (no speech-to-text key is
    /// needed); answers are spoken into the call and shown in the transcript.
    /// </summary>
    private async Task RunAiAgentAsync(VoipCall call, IvrContext context, CancellationToken cancellationToken)
    {
        AiMode = true;
        var speech = new ReadAloudSpeech(text => Add(TranscriptSpeaker.Ai, text));
        using var chat = CreateChat();
        var history = new List<ChatMessage>();
        var known = context.Values.Count == 0 ? "none" : string.Join(", ", context.Values.Select(v => $"{v.Key}={v.Value}"));
        var options = new ChatOptions
        {
            Instructions = $"{_configuration["AI:Instructions"] ?? CurrentInstructions}\nValues the caller keyed into the IVR: {known}.",
            MaxOutputTokens = 1500,
        };

        await speech.SpeakAsync(call, "Halo, saya asisten AI. Silakan sampaikan kebutuhan Anda.", cancellationToken: cancellationToken);
        Add(TranscriptSpeaker.System, "AI agent connected. Type what the caller says.");

        while (call.IsActive && !cancellationToken.IsCancellationRequested)
        {
            string message;
            try
            {
                message = await _callerMessages.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            history.Add(new ChatMessage(ChatRole.User, message));
            string reply;
            if (chat is null)
            {
                reply = "Mode demo: hubungkan model AI (AI:Endpoint dan AI:ApiKey) agar saya bisa menjawab pertanyaan Anda.";
            }
            else
            {
                try
                {
                    reply = (await chat.GetResponseAsync(history, options, cancellationToken)).Text.Trim();
                }
                catch (HttpRequestException ex)
                {
                    reply = "Maaf, sistem AI sedang tidak tersedia.";
                    Add(TranscriptSpeaker.System, ex.Message);
                }
            }

            history.Add(new ChatMessage(ChatRole.Assistant, reply));
            await speech.SpeakAsync(call, reply, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Instructions from the flow being tested, set by the studio before each call.</summary>
    public string CurrentInstructions { get; set; } = "Jawab singkat dan ramah.";

    private IChatClient? CreateChat()
    {
        if (!AiConfigured)
        {
            return null;
        }

        var options = Endpoint!.Contains("azure.com", StringComparison.OrdinalIgnoreCase)
            ? OpenAiChatOptions.ForAzure(Endpoint, ApiKey!, Model)
            : new OpenAiChatOptions { BaseUri = new Uri(Endpoint.TrimEnd('/') + "/"), ApiKey = ApiKey!, Model = Model };
        options.ReasoningEffort = "minimal";
        return new OpenAiChatClient(options);
    }

    private static async Task<VoipClient> StartEndpointAsync(string name)
    {
        var client = new VoipClient(new VoipClientOptions
        {
            BindAddress = "127.0.0.1",
            SipPort = 0,
            Username = name,
            RtpPortMin = 54000,
            RtpPortMax = 58999,
        });
        await client.StartAsync();
        return client;
    }

    private void Add(TranscriptSpeaker speaker, string text)
    {
        lock (_transcript)
        {
            _transcript.Add(new TranscriptLine(DateTimeOffset.Now, speaker, text));
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() => await HangUpAsync();
}
