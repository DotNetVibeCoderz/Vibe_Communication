using System.Text;
using Microsoft.Extensions.AI;
using VoipNet.AI.Llm;
using Xunit;

namespace VoipNet.Tests;

/// <summary>
/// Real calls to hosted models. Keys are read at run time from the file named by
/// VOIPNET_TEST_KEYS (default: ..\testkey.txt next to the repository) and never stored in the repo.
/// The tests skip when the file or a section is missing.
/// </summary>
public sealed class LiveLlmTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, Dictionary<string, string>>> Keys = new(Load);

    private static IReadOnlyDictionary<string, Dictionary<string, string>> Load()
    {
        var path = Environment.GetEnvironmentVariable("VOIPNET_TEST_KEYS") ?? FindDefault();
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path))
        {
            return sections;
        }

        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                current = null;
                continue;
            }

            var colon = line.IndexOf(':');
            // "https://..." values contain a colon too, so a header is a line without "key: value" shape.
            if (current is null || colon < 0)
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line] = current;
                continue;
            }

            current[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return sections;
    }

    private static string? FindDefault()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "testkey.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static Dictionary<string, string>? Section(string name) =>
        Keys.Value.FirstOrDefault(p => p.Key.Replace(" ", string.Empty).Contains(name.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase)).Value;

    private static IChatClient? Azure()
    {
        var s = Section("Azure OpenAI");
        if (s is null || !s.TryGetValue("apikey", out var key) || !s.TryGetValue("endpoint", out var endpoint) || !s.TryGetValue("model", out var model))
        {
            return null;
        }

        return new OpenAiChatClient(OpenAiChatOptions.ForAzure(endpoint, key, model));
    }

    private static IChatClient? DeepSeek()
    {
        var s = Section("DeepSeek");
        if (s is null)
        {
            return null;
        }

        var key = s.FirstOrDefault(p => p.Key.Contains("Key", StringComparison.OrdinalIgnoreCase)).Value;
        var endpoint = s.FirstOrDefault(p => p.Key.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)).Value;
        var model = s.FirstOrDefault(p => p.Key.Contains("Model", StringComparison.OrdinalIgnoreCase)).Value?.Split(',')[0].Trim();
        if (key is null || endpoint is null || model is null)
        {
            return null;
        }

        return new OpenAiChatClient(new OpenAiChatOptions { ApiKey = key, BaseUri = new Uri(endpoint.TrimEnd('/') + "/"), Model = model });
    }

    [Fact]
    public async Task AzureOpenAiAnswersInIndonesian()
    {
        using var client = Azure();
        Assert.SkipWhen(client is null, "No Azure OpenAI key available.");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Jawab dengan satu kata saja: apa ibu kota Indonesia?")],
            new ChatOptions { MaxOutputTokens = 2000 });

        Assert.Contains("jakarta", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AzureOpenAiStreamsAndCallsTools()
    {
        using var inner = Azure();
        Assert.SkipWhen(inner is null, "No Azure OpenAI key available.");

        var called = false;
        var tool = AIFunctionFactory.Create(
            (string orderId) => { called = true; return $"Order {orderId} dikirim hari ini via JNE."; },
            "get_order_status",
            "Returns the shipping status of an order.");
        using var client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "Bagaimana status pesanan INV-778?")],
            new ChatOptions { Tools = [tool], MaxOutputTokens = 3000 }))
        {
            text.Append(update.Text);
        }

        Assert.True(called, "the model did not call the tool");
        Assert.Contains("JNE", text.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeepSeekAnswersThroughTheCompatibleClient()
    {
        using var client = DeepSeek();
        Assert.SkipWhen(client is null, "No DeepSeek key available.");

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Reply with the single word: pong")],
            new ChatOptions { MaxOutputTokens = 400, Temperature = 0 });

        Assert.Contains("pong", response.Text, StringComparison.OrdinalIgnoreCase);
    }
}
