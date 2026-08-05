using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telepati.Infrastructure.Services;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Telepati.Bot.Plugins;
using Telepati.Shared.Configuration;

namespace Telepati.Bot;

/// <summary>
/// Builds a Semantic Kernel instance for whichever model provider is configured. The rest of
/// the bot only ever sees <see cref="Kernel"/>, so switching between OpenAI, Anthropic, Gemini
/// and Ollama is a configuration change and nothing more.
/// </summary>
public interface IKernelFactory
{
    Kernel Create(BotOptions options);

    /// <summary>
    /// Builds a kernel and then attaches every enabled MCP server's tools. Separate from
    /// <see cref="Create"/> because reaching an MCP server involves I/O that can fail.
    /// </summary>
    Task<Kernel> CreateWithToolsAsync(BotOptions options, CancellationToken ct = default);

    PromptExecutionSettings CreateExecutionSettings(BotOptions options);
}

public class KernelFactory(IServiceProvider services) : IKernelFactory
{
    public Kernel Create(BotOptions options)
    {
        var builder = Kernel.CreateBuilder();

        switch (options.Provider.ToLowerInvariant())
        {
            case "openai":
                // A custom endpoint makes this work with any OpenAI-compatible service
                // (DeepSeek, Groq, OpenRouter, a local vLLM) without a separate connector.
                if (string.IsNullOrWhiteSpace(options.Endpoint) || options.Endpoint.Contains("openai.com"))
                {
                    builder.AddOpenAIChatCompletion(options.Model, options.ApiKey);
                }
                else
                {
                    builder.AddOpenAIChatCompletion(options.Model, new Uri(options.Endpoint), options.ApiKey);
                }
                break;

            case "azureopenai":
            case "azure":
                // Azure names the deployment, not the model, so Model carries the deployment name.
                builder.AddAzureOpenAIChatCompletion(options.Model, options.Endpoint, options.ApiKey);
                break;

            case "deepseek":
                builder.AddOpenAIChatCompletion(
                    options.Model,
                    new Uri(string.IsNullOrWhiteSpace(options.Endpoint) ? "https://api.deepseek.com" : options.Endpoint),
                    options.ApiKey);
                break;

            case "anthropic":
                // Semantic Kernel ships no first-party Anthropic connector, so the official SDK's
                // IChatClient is adapted into the kernel's chat-completion abstraction instead.
                builder.Services.AddSingleton<IChatCompletionService>(_ =>
                {
                    var client = new Anthropic.SDK.AnthropicClient(options.ApiKey);
                    return ((IChatClient)client.Messages).AsChatCompletionService();
                });
                break;

            case "gemini":
                builder.AddGoogleAIGeminiChatCompletion(options.Model, options.ApiKey);
                break;

            default:
                builder.AddOllamaChatCompletion(options.Model, new Uri(options.Endpoint));
                break;
        }

        // Plugins are resolved from DI so they can reach storage, HTTP and the workspace sandbox.
        builder.Plugins.AddFromObject(services.GetRequiredService<TimePlugin>(), "Time");
        builder.Plugins.AddFromObject(services.GetRequiredService<MathPlugin>(), "Math");
        builder.Plugins.AddFromObject(services.GetRequiredService<WebPlugin>(), "Web");
        builder.Plugins.AddFromObject(services.GetRequiredService<FilePlugin>(), "Files");
        builder.Plugins.AddFromObject(services.GetRequiredService<ScriptPlugin>(), "Script");

        // Installed skills: instructions, bundled references, and bundled scripts.
        builder.Plugins.AddFromObject(services.GetRequiredService<SkillsPlugin>(), "Skills");

        return builder.Build();
    }

    public async Task<Kernel> CreateWithToolsAsync(BotOptions options, CancellationToken ct = default)
    {
        var kernel = Create(options);

        try
        {
            var catalogue = services.GetRequiredService<IMcpCatalogService>();
            var enabled = await catalogue.GetEnabledAsync(ct);

            if (enabled.Count > 0)
            {
                var mcp = services.GetRequiredService<IMcpToolProvider>();
                await mcp.RegisterAsync(kernel, enabled, ct);
            }
        }
        catch (Exception e)
        {
            // MCP is an enhancement, never a prerequisite: the bot must still answer without it.
            services.GetService<ILogger<KernelFactory>>()?.LogWarning(e, "Could not attach MCP tools.");
        }

        return kernel;
    }

    public PromptExecutionSettings CreateExecutionSettings(BotOptions options)
    {
        var settings = new PromptExecutionSettings
        {
            // Auto invocation is what lets the model chain tools (search → read → calculate) itself.
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                autoInvoke: true,
                options: new FunctionChoiceBehaviorOptions { AllowConcurrentInvocation = false })
        };

        // Reasoning models reject `max_tokens`, `temperature` and `top_p` outright with a 400 —
        // they take `max_completion_tokens` and fix sampling internally. The connector maps
        // MaxTokens onto `max_tokens` with no override, so the only thing that actually works is
        // to send none of them and let the deployment's own defaults apply.
        if (IsReasoningModel(options.Model)) return settings;

        settings.ExtensionData = new Dictionary<string, object>
        {
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["max_tokens"] = options.MaxTokens
        };

        return settings;
    }

    /// <summary>
    /// Recognises the OpenAI reasoning families by name. Being conservative is the right way
    /// round here: omitting the sampling parameters always works, sending them to a model that
    /// refuses them fails the whole request.
    /// </summary>
    private static bool IsReasoningModel(string model)
    {
        var name = model.ToLowerInvariant();

        return name.StartsWith("o1") || name.StartsWith("o3") || name.StartsWith("o4")
               || name.Contains("gpt-5") || name.Contains("gpt5")
               || name.Contains("reason");
    }
}
