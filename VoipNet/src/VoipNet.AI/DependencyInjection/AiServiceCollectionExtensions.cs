using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoipNet.AI.Agents;
using VoipNet.AI.Llm;
using VoipNet.AI.Realtime;
using VoipNet.AI.Speech;

namespace VoipNet.AI.DependencyInjection;

/// <summary>Registers the AI pieces of Voip.NET: models, speech services and agents.</summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>Registers an OpenAI or OpenAI-compatible chat model, with tool calling enabled.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the endpoint.</param>
    public static IServiceCollection AddOpenAiChatClient(this IServiceCollection services, Action<OpenAiChatOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new OpenAiChatOptions();
        configure(options);
        services.AddHttpClient(nameof(OpenAiChatClient));
        services.AddSingleton<IChatClient>(sp => Wrap(new OpenAiChatClient(options, Http(sp, nameof(OpenAiChatClient))), sp));
        return services;
    }

    /// <summary>Registers Anthropic Claude as the chat model, with tool calling enabled.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the endpoint.</param>
    public static IServiceCollection AddAnthropicChatClient(this IServiceCollection services, Action<AnthropicChatOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AnthropicChatOptions();
        configure(options);
        services.AddHttpClient(nameof(AnthropicChatClient));
        services.AddSingleton<IChatClient>(sp => Wrap(new AnthropicChatClient(options, Http(sp, nameof(AnthropicChatClient))), sp));
        return services;
    }

    /// <summary>Registers Google Gemini as the chat model, with tool calling enabled.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the endpoint.</param>
    public static IServiceCollection AddGeminiChatClient(this IServiceCollection services, Action<GeminiChatOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GeminiChatOptions();
        configure(options);
        services.AddHttpClient(nameof(GeminiChatClient));
        services.AddSingleton<IChatClient>(sp => Wrap(new GeminiChatClient(options, Http(sp, nameof(GeminiChatClient))), sp));
        return services;
    }

    /// <summary>Registers Deepgram for streaming transcription.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider.</param>
    public static IServiceCollection AddDeepgramSpeechToText(this IServiceCollection services, Action<DeepgramOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new DeepgramOptions();
        configure(options);
        services.AddHttpClient(nameof(DeepgramSpeechToText));
        services.AddSingleton<ISpeechToText>(sp => new DeepgramSpeechToText(
            options,
            Http(sp, nameof(DeepgramSpeechToText)),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<DeepgramSpeechToText>>()));
        return services;
    }

    /// <summary>Registers OpenAI for transcription and synthesis.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider.</param>
    public static IServiceCollection AddOpenAiSpeech(this IServiceCollection services, Action<OpenAiSpeechOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new OpenAiSpeechOptions();
        configure(options);
        services.AddHttpClient("openai-speech");
        services.TryAddSingleton<ISpeechToText>(sp => new OpenAiSpeechToText(options, Http(sp, "openai-speech")));
        services.TryAddSingleton<ITextToSpeech>(sp => new OpenAiTextToSpeech(options, Http(sp, "openai-speech")));
        return services;
    }

    /// <summary>Registers ElevenLabs for expressive synthesis.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider.</param>
    public static IServiceCollection AddElevenLabsTextToSpeech(this IServiceCollection services, Action<ElevenLabsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElevenLabsOptions();
        configure(options);
        services.AddHttpClient(nameof(ElevenLabsTextToSpeech));
        services.AddSingleton<ITextToSpeech>(sp => new ElevenLabsTextToSpeech(options, Http(sp, nameof(ElevenLabsTextToSpeech))));
        return services;
    }

    /// <summary>Registers Google Cloud for transcription and synthesis.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider.</param>
    public static IServiceCollection AddGoogleCloudSpeech(this IServiceCollection services, Action<GoogleCloudSpeechOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new GoogleCloudSpeechOptions();
        configure(options);
        services.AddHttpClient("google-speech");
        services.TryAddSingleton<ISpeechToText>(sp => new GoogleCloudSpeechToText(options, Http(sp, "google-speech")));
        services.TryAddSingleton<ITextToSpeech>(sp => new GoogleCloudTextToSpeech(options, Http(sp, "google-speech")));
        return services;
    }

    /// <summary>Registers Amazon Polly for synthesis.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider.</param>
    public static IServiceCollection AddAmazonPollyTextToSpeech(this IServiceCollection services, Action<AmazonPollyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AmazonPollyOptions();
        configure(options);
        services.AddHttpClient(nameof(AmazonPollyTextToSpeech));
        services.AddSingleton<ITextToSpeech>(sp => new AmazonPollyTextToSpeech(options, Http(sp, nameof(AmazonPollyTextToSpeech))));
        return services;
    }

    /// <summary>Registers a self-hosted ElBruno.Realtime speech server for transcription and synthesis.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the server.</param>
    public static IServiceCollection AddElBrunoRealtimeSpeech(this IServiceCollection services, Action<ElBrunoRealtimeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ElBrunoRealtimeOptions();
        configure(options);
        services.AddHttpClient("elbruno-realtime");
        services.TryAddSingleton<ISpeechToText>(_ => new ElBrunoRealtimeSpeechToText(options));
        services.TryAddSingleton<ITextToSpeech>(sp => new ElBrunoRealtimeTextToSpeech(options, Http(sp, "elbruno-realtime")));
        return services;
    }

    /// <summary>Registers agent defaults: the options, an in-memory conversation store and a factory.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the agent.</param>
    public static IServiceCollection AddVoiceAgent(this IServiceCollection services, Action<VoiceAgentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.Configure(configure);
        services.TryAddSingleton<IConversationStore, InMemoryConversationStore>();
        services.TryAddSingleton<VoiceAgentFactory>();
        return services;
    }

    /// <summary>Registers the realtime speech-to-speech agent options.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider.</param>
    public static IServiceCollection AddRealtimeVoiceAgent(this IServiceCollection services, Action<RealtimeVoiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RealtimeVoiceOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddTransient<RealtimeVoiceAgent>();
        return services;
    }

    private static HttpClient Http(IServiceProvider provider, string name) =>
        provider.GetService<IHttpClientFactory>()?.CreateClient(name) ?? new HttpClient();

    private static IChatClient Wrap(IChatClient client, IServiceProvider provider) =>
        new ChatClientBuilder(client)
            .UseLogging(provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>())
            .UseFunctionInvocation(provider.GetService<Microsoft.Extensions.Logging.ILoggerFactory>())
            .Build(provider);
}

/// <summary>Creates a <see cref="VoiceAgent"/> per call from the registered services.</summary>
/// <param name="chatClient">Model used to think.</param>
/// <param name="speechToText">Recogniser.</param>
/// <param name="textToSpeech">Synthesiser.</param>
/// <param name="options">Default agent options.</param>
/// <param name="conversationStore">Conversation memory.</param>
/// <param name="loggerFactory">Logger factory.</param>
public sealed class VoiceAgentFactory(
    IChatClient chatClient,
    ISpeechToText speechToText,
    ITextToSpeech textToSpeech,
    Microsoft.Extensions.Options.IOptions<VoiceAgentOptions> options,
    IConversationStore? conversationStore = null,
    Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null)
{
    /// <summary>Creates an agent, optionally adjusting the options for this call.</summary>
    /// <param name="configure">Per-call adjustments, for example a different greeting or language.</param>
    public VoiceAgent Create(Action<VoiceAgentOptions>? configure = null)
    {
        var perCall = Clone(options.Value);
        configure?.Invoke(perCall);
        return new VoiceAgent(
            chatClient,
            speechToText,
            textToSpeech,
            perCall,
            conversationStore,
            loggerFactory?.CreateLogger<VoiceAgent>());
    }

    private static VoiceAgentOptions Clone(VoiceAgentOptions source) => new()
    {
        SystemPrompt = source.SystemPrompt,
        Greeting = source.Greeting,
        SilencePromptText = source.SilencePromptText,
        SilencePrompt = source.SilencePrompt,
        SilenceHangup = source.SilenceHangup,
        MaxCallDuration = source.MaxCallDuration,
        BargeIn = source.BargeIn,
        Language = source.Language,
        ChatOptions = source.ChatOptions.Clone(),
        SpeechToText = source.SpeechToText,
        TextToSpeech = source.TextToSpeech,
        HandoffTarget = source.HandoffTarget,
        EnableCallControlTools = source.EnableCallControlTools,
        ConversationKey = source.ConversationKey,
        RestoreTurns = source.RestoreTurns,
    };
}
