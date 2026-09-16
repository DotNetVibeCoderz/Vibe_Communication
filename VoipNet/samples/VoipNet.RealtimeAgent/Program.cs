// Voip.NET realtime agent: answers SIP calls with an AI voice agent.
// Made by Gravicode Studios, led by Kang Fadhil.
//
//   dotnet run                → wait for calls on the configured SIP account
//   dotnet run -- --demo      → place a simulated call to itself and print the conversation

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoipNet;
using VoipNet.AI.Agents;
using VoipNet.AI.DependencyInjection;
using VoipNet.AI.Llm;
using VoipNet.AI.Realtime;
using VoipNet.AI.Speech;
using VoipNet.DependencyInjection;
using VoipNet.RealtimeAgent;

var demo = args.Contains("--demo");
// Load appsettings.json from the app folder even when started from another working directory.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args.Where(a => a != "--demo").ToArray(),
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddEnvironmentVariables("VOIPNET_");
var config = builder.Configuration;

builder.Services.AddVoipClient(options =>
{
    config.GetSection("Sip").Bind(options);
    if (demo)
    {
        options.BindAddress = "127.0.0.1";
        options.SipPort = 0;
        options.RegisterOnStart = false;
    }
});

AddChatModel(builder.Services, config.GetSection("AI:Chat"));
if (demo)
{
    // The demo has no microphone: a scripted caller speaks and simulated voices keep it offline,
    // while the chat model is real.
    builder.Services.AddSingleton<ISpeechToText>(new ScriptedCaller(DemoScript.Lines));
    builder.Services.AddSingleton<ITextToSpeech>(new SyllableVoice());
}
else
{
    AddSpeechToText(builder.Services, config.GetSection("AI:SpeechToText"));
    AddTextToSpeech(builder.Services, config.GetSection("AI:TextToSpeech"));
}

builder.Services.AddVoiceAgent(options =>
{
    config.GetSection("Agent").Bind(options);
    options.ChatOptions = new ChatOptions { MaxOutputTokens = 1500 };
    if (demo)
    {
        options.SilencePrompt = TimeSpan.Zero;
        options.SilenceHangup = TimeSpan.Zero;
    }
});
builder.Services.AddSingleton<IConversationStore>(new JsonFileConversationStore(Path.Combine(AppContext.BaseDirectory, "conversations")));
builder.Services.AddRealtimeVoiceAgent(options => config.GetSection("AI:Realtime").Bind(options));
builder.Services.AddHostedService<AgentService>();
if (demo)
{
    builder.Services.AddHostedService<DemoCaller>();
}

await builder.Build().RunAsync();

static void AddChatModel(IServiceCollection services, IConfigurationSection section)
{
    // Empty values in appsettings.json mean "not set here": fall back to environment variables.
    static string? Value(string? configured, string environment) =>
        string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable(environment) : configured;

    var provider = section["Provider"]?.ToLowerInvariant() ?? "openai";
    var endpoint = Value(section["Endpoint"], "VOIPNET_AI_ENDPOINT") ?? string.Empty;
    var key = Value(section["ApiKey"], "VOIPNET_AI_KEY") ?? string.Empty;
    var model = Value(section["Model"], "VOIPNET_AI_MODEL") ?? "gpt-5-mini";

    switch (provider)
    {
        case "anthropic":
            services.AddAnthropicChatClient(o => { o.ApiKey = key; o.Model = model; });
            break;
        case "gemini":
            services.AddGeminiChatClient(o => { o.ApiKey = key; o.Model = model; });
            break;
        case "azure":
            services.AddOpenAiChatClient(o =>
            {
                var azure = OpenAiChatOptions.ForAzure(endpoint, key, model);
                (o.BaseUri, o.ApiKey, o.Model, o.UseApiKeyHeader) = (azure.BaseUri, azure.ApiKey, azure.Model, true);
                o.ReasoningEffort = "minimal";
            });
            break;
        case "deepseek":
            services.AddOpenAiChatClient(o => { o.BaseUri = new Uri("https://api.deepseek.com/"); o.ApiKey = key; o.Model = model; });
            break;
        default:
            services.AddOpenAiChatClient(o =>
            {
                if (endpoint.Length > 0)
                {
                    o.BaseUri = new Uri(endpoint.TrimEnd('/') + "/");
                }

                o.ApiKey = key;
                o.Model = model;
                o.ReasoningEffort = "minimal";
            });
            break;
    }
}

static void AddSpeechToText(IServiceCollection services, IConfigurationSection section)
{
    var key = section["ApiKey"] ?? string.Empty;
    switch (section["Provider"]?.ToLowerInvariant())
    {
        case "openai":
            services.AddOpenAiSpeech(o => o.ApiKey = key);
            break;
        case "google":
            services.AddGoogleCloudSpeech(o => o.ApiKey = key);
            break;
        case "elevenlabs":
            services.AddSingleton<ISpeechToText>(new ElevenLabsSpeechToText(new ElevenLabsOptions { ApiKey = key }));
            break;
        case "elbruno":
            services.AddElBrunoRealtimeSpeech(o => config(section, o));
            break;
        default:
            services.AddDeepgramSpeechToText(o => o.ApiKey = key);
            break;
    }

    static void config(IConfigurationSection s, ElBrunoRealtimeOptions o) => s.Bind(o);
}

static void AddTextToSpeech(IServiceCollection services, IConfigurationSection section)
{
    var key = section["ApiKey"] ?? string.Empty;
    var voice = section["Voice"];
    switch (section["Provider"]?.ToLowerInvariant())
    {
        case "openai":
            services.AddOpenAiSpeech(o => { o.ApiKey = key; o.Voice = voice is { Length: > 0 } ? voice : o.Voice; });
            break;
        case "google":
            services.AddGoogleCloudSpeech(o => { o.ApiKey = key; o.Voice = voice is { Length: > 0 } ? voice : o.Voice; });
            break;
        case "polly":
            services.AddAmazonPollyTextToSpeech(o => section.Bind(o));
            break;
        case "elbruno":
            services.AddElBrunoRealtimeSpeech(o => section.Bind(o));
            break;
        default:
            services.AddElevenLabsTextToSpeech(o => { o.ApiKey = key; o.VoiceId = voice is { Length: > 0 } ? voice : o.VoiceId; });
            break;
    }
}
