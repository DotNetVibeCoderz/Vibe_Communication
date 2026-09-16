using System.Runtime.CompilerServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.AI;
using VoipNet.AI.Agents;
using VoipNet.AI.Llm;
using VoipNet.AI.Speech;
using VoipNet.Audio;
using VoipNet.Gallery.Infrastructure;

namespace VoipNet.Gallery.Pages;

/// <summary>Model settings shared by the AI pages.</summary>
public sealed partial class AiSettings : ObservableObject
{
    public static AiSettings Shared { get; } = Load();

    [ObservableProperty]
    public partial string Endpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Model { get; set; } = "gpt-5-mini";

    [ObservableProperty]
    public partial string Prompt { get; set; } = "A caller says their internet has been down since this morning. Reply as a friendly Indonesian ISP agent, in two sentences.";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);

    public IChatClient CreateClient()
    {
        var options = Endpoint.Contains("azure.com", StringComparison.OrdinalIgnoreCase)
            ? OpenAiChatOptions.ForAzure(Endpoint, ApiKey, Model)
            : new OpenAiChatOptions { BaseUri = new Uri(Endpoint.TrimEnd('/') + "/"), ApiKey = ApiKey, Model = Model };

        // Conversations need quick answers more than deep deliberation.
        options.ReasoningEffort = "minimal";
        return new ChatClientBuilder(new OpenAiChatClient(options)).UseFunctionInvocation().Build();
    }

    /// <summary>
    /// Environment variables configure the model without typing the key into the UI:
    /// VOIPNET_AI_ENDPOINT, VOIPNET_AI_KEY and VOIPNET_AI_MODEL.
    /// </summary>
    private static AiSettings Load() => new()
    {
        Endpoint = Environment.GetEnvironmentVariable("VOIPNET_AI_ENDPOINT") ?? string.Empty,
        ApiKey = Environment.GetEnvironmentVariable("VOIPNET_AI_KEY") ?? string.Empty,
        Model = Environment.GetEnvironmentVariable("VOIPNET_AI_MODEL") ?? "gpt-5-mini",
    };
}

public sealed class ChatModelsPage : DemoPage
{
    public ChatModelsPage()
        : base("AI", "Language models", "OpenAI, Azure OpenAI, DeepSeek and any OpenAI-compatible server share one client; Anthropic Claude and Google Gemini have their own. All implement IChatClient from Microsoft.Extensions.AI, so tools and middleware work the same everywhere.")
    {
        AddAction("Ask the model", AskAsync, "go");
        AddAction("Ask with a tool", ToolAsync, "quiet");
    }

    public AiSettings Settings => AiSettings.Shared;

    public override bool HasInput => true;

    public override string Code => """
        IChatClient chat = new OpenAiChatClient(
            OpenAiChatOptions.ForAzure(endpoint, key, deployment: "gpt-5-mini"));
        // or: new AnthropicChatClient(new() { ApiKey = key, Model = "claude-sonnet-5" })
        // or: new GeminiChatClient(new() { ApiKey = key, Model = "gemini-2.0-flash" })

        chat = new ChatClientBuilder(chat).UseFunctionInvocation().Build();

        var ticket = AIFunctionFactory.Create(
            (string customer) => $"Ticket TCK-1042 opened for {customer}",
            "open_ticket");

        await foreach (var update in chat.GetStreamingResponseAsync(prompt,
                           new ChatOptions { Tools = [ticket] }))
            Console.Write(update.Text);
        """;

    private async Task AskAsync()
    {
        if (!EnsureConfigured())
        {
            return;
        }

        using var chat = Settings.CreateClient();
        Write($"→ {Settings.Prompt}");
        var answer = new StringBuilder();
        var started = DateTime.UtcNow;
        await foreach (var update in chat.GetStreamingResponseAsync(Settings.Prompt, new ChatOptions { MaxOutputTokens = 2000 }))
        {
            answer.Append(update.Text);
        }

        SetMetric("Model", Settings.Model);
        SetMetric("Round trip", $"{(DateTime.UtcNow - started).TotalSeconds:0.0} s");
        Write($"← {answer.ToString().Trim()}");
    }

    private async Task ToolAsync()
    {
        if (!EnsureConfigured())
        {
            return;
        }

        using var chat = Settings.CreateClient();
        var openTicket = AIFunctionFactory.Create(
            (string customer, string problem) =>
            {
                Write($"⚙ tool open_ticket(customer: {customer}, problem: {problem})");
                return $"Ticket TCK-1042 opened for {customer}. A technician visits tomorrow 09:00–12:00.";
            },
            "open_ticket",
            "Opens a support ticket and books a technician visit.");

        const string prompt = "Pelanggan bernama Rina bilang internetnya mati. Buatkan tiket lalu beri tahu jadwal teknisi dalam satu kalimat.";
        Write($"→ {prompt}");
        var response = await chat.GetResponseAsync(prompt, new ChatOptions { Tools = [openTicket], MaxOutputTokens = 3000 });
        Write($"← {response.Text.Trim()}");
        SetMetric("Tool calls", response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private bool EnsureConfigured()
    {
        if (Settings.IsConfigured)
        {
            return true;
        }

        Write("Add an endpoint and key above (or set VOIPNET_AI_ENDPOINT and VOIPNET_AI_KEY) to talk to a model.");
        return false;
    }
}

/// <summary>Synthetic speech that renders text as short syllable-like tones, so demos run offline.</summary>
internal sealed class SyllableSpeech : ITextToSpeech
{
    public string Name => "syllables";

    public int PreferredSampleRate => 16000;

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, SpeechSynthesisOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = 16 * Math.Clamp(word.Length * 45, 90, 400);
            var samples = new short[length + 1600];
            var pitch = 170 + (word.Length * 9);
            for (var i = 0; i < length; i++)
            {
                var envelope = Math.Sin(Math.PI * i / length);
                samples[i] = (short)(6500 * envelope * Math.Sin(2 * Math.PI * pitch * i / 16000));
            }

            yield return new AudioChunk(Pcm.ToBytes(samples), 16000);
            await Task.Yield();
        }
    }
}

/// <summary>Recognises "what the caller said" from a script, one line per utterance detected in the audio.</summary>
internal sealed class ScriptedRecognizer(IReadOnlyList<string> script) : ISpeechToText
{
    public string Name => "script";

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(IAsyncEnumerable<AudioChunk> audio, SpeechRecognitionOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var vad = new VoiceActivityDetector(16000) { SilenceHangoverMs = 350 };
        var index = 0;
        var speaking = false;
        await foreach (var chunk in audio.WithCancellation(cancellationToken))
        {
            var now = vad.Process(Pcm.AsSamples(chunk.Data.Span));
            if (speaking && !now && index < script.Count)
            {
                yield return new TranscriptSegment(script[index++], true);
            }

            speaking = now;
        }
    }

    public Task<string> TranscribeOnceAsync(ReadOnlyMemory<byte> pcm, int sampleRate, SpeechRecognitionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(script.FirstOrDefault() ?? string.Empty);
}

public sealed class VoiceAgentPage : DemoPage
{
    private readonly Lab _lab = new();

    public VoiceAgentPage()
        : base("AI", "Voice agent", "A caller talks to an AI agent: audio → speech-to-text → model → text-to-speech → back into the call, with barge-in and call-control tools. Here the recogniser and voice are simulated so the demo runs offline; the model is real when configured.")
    {
        AddAction("Start a conversation", RunAsync, "go");
    }

    public override string Code => """
        var agent = new VoiceAgent(
            chatClient,                                   // any IChatClient
            new DeepgramSpeechToText(new() { ApiKey = dg }),
            new ElevenLabsTextToSpeech(new() { ApiKey = el }),
            new VoiceAgentOptions
            {
                Greeting = "Halo, dengan Gravicode Net. Ada yang bisa dibantu?",
                SystemPrompt = "Kamu agen layanan pelanggan ISP. Jawab singkat.",
                Language = "id-ID",
                HandoffTarget = "sip:tier2@pbx",           // transfer_call tool
            },
            new JsonFileConversationStore("conversations"));

        client.IncomingCall += async (_, e) =>
        {
            await e.Call.AnswerAsync();
            await agent.RunAsync(e.Call);
        };
        """;

    private async Task RunAsync()
    {
        await _lab.ResetAsync();
        var customer = await _lab.StartAsync("customer");
        var service = await _lab.StartAsync("service");

        IChatClient chat = AiSettings.Shared.IsConfigured ? AiSettings.Shared.CreateClient() : new ScriptedModel();
        string[] said = ["Internet saya mati sejak pagi.", "Nomor pelanggan saya 4321.", "Terima kasih."];
        var agent = new VoiceAgent(chat, new ScriptedRecognizer(said), new SyllableSpeech(), new VoiceAgentOptions
        {
            Greeting = "Halo, dengan Gravicode Net. Ada yang bisa dibantu?",
            SystemPrompt = "Kamu agen layanan pelanggan ISP bernama Sari. Jawab maksimal dua kalimat pendek.",
            SilencePrompt = TimeSpan.Zero,
            SilenceHangup = TimeSpan.Zero,
            EnableCallControlTools = false,
            ChatOptions = new ChatOptions { MaxOutputTokens = 1500 },
        });
        agent.AgentSaid += (_, text) => Write($"Agent  · {text}");
        agent.CallerSaid += (_, text) => Write($"Caller · {text}");
        SetMetric("Model", AiSettings.Shared.IsConfigured ? AiSettings.Shared.Model : "scripted (configure a model on the previous page)");

        service.IncomingCall += (_, e) => Lab.Safe(async () =>
        {
            await e.Call.AnswerAsync();
            TraceAudio(e.Call, AudioDirection.Outbound);
            await agent.RunAsync(e.Call);
        });

        var call = await customer.CallAsync(Lab.Uri(service, "service")).WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForQuietAsync(call);
        foreach (var _ in said)
        {
            // Speak, then give the agent time to think and answer before the next line.
            var answered = agent.Turns.Count(t => t.Role == "assistant");
            call.SendAudio(Voice(1200, 210), 16000);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (agent.Turns.Count(t => t.Role == "assistant") == answered && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            await WaitForQuietAsync(call);
        }
        SetMetric("Turns", agent.Turns.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await call.HangupAsync();
    }

    private static async Task WaitForQuietAsync(VoipCall call)
    {
        // Wait until the agent has finished talking: nothing arriving for 700 ms.
        var quietSince = DateTime.UtcNow;
        void OnAudio(VoipCall c, AudioDirection d, int r, ReadOnlySpan<short> s)
        {
            if (d == AudioDirection.Inbound && Pcm.Rms(s) > 0.02)
            {
                quietSince = DateTime.UtcNow;
            }
        }

        call.AudioReceived += OnAudio;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow - quietSince < TimeSpan.FromMilliseconds(900) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        call.AudioReceived -= OnAudio;
    }

    public override Task ResetAsync() => _lab.ResetAsync();

    private sealed class ScriptedModel : IChatClient
    {
        private int _turn;

        private readonly string[] _replies =
        [
            "Mohon maaf atas gangguannya. Boleh saya minta nomor pelanggan Anda?",
            "Terima kasih, ada gangguan di area Anda dan teknisi sedang menanganinya. Estimasi normal pukul 14.00.",
            "Sama-sama, semoga harinya menyenangkan.",
        ];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies[Math.Min(_turn++, _replies.Length - 1)])));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var reply = _replies[Math.Min(_turn++, _replies.Length - 1)];
            foreach (var word in reply.Split(' '))
            {
                await Task.Delay(40, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
