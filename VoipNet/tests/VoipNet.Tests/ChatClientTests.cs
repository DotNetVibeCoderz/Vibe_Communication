using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using VoipNet.AI.Llm;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Protocol tests for the chat connectors against canned HTTP responses.</summary>
public sealed class ChatClientTests
{
    private sealed class CannedHandler(Func<HttpRequestMessage, string, (string ContentType, string Body)> respond) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            Requests.Add(request);
            var (type, payload) = respond(request, body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, type) };
        }
    }

    private static string Sse(params string[] events) => string.Concat(events.Select(e => $"data: {e}\n\n"));

    [Fact]
    public async Task OpenAiStreamsTextAndSendsBearerKey()
    {
        var handler = new CannedHandler((_, _) => ("text/event-stream", Sse(
            """{"id":"r1","choices":[{"delta":{"content":"Halo"}}]}""",
            """{"id":"r1","choices":[{"delta":{"content":" dunia"}}]}""",
            "[DONE]")));
        using var client = new OpenAiChatClient(new OpenAiChatOptions { ApiKey = "k", Model = "gpt-4o-mini" }, new HttpClient(handler));

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Temperature = 0.2f, MaxOutputTokens = 50 }))
        {
            text.Append(update.Text);
        }

        Assert.Equal("Halo dunia", text.ToString());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Contains("\"max_tokens\":50", handler.RequestBodies[0]);
        Assert.Contains("\"temperature\"", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task OpenAiReasoningModelsUseCompletionTokensAndAzureHeader()
    {
        var handler = new CannedHandler((_, _) => ("application/json",
            """{"id":"r","model":"gpt-5-mini","choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}"""));
        var options = OpenAiChatOptions.ForAzure("https://example.openai.azure.com", "secret", "gpt-5-mini");
        using var client = new OpenAiChatClient(options, new HttpClient(handler));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 20 });

        Assert.Equal("ok", response.Text);
        Assert.Equal(4, response.Usage?.TotalTokenCount);
        Assert.Equal("https://example.openai.azure.com/openai/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
        Assert.True(handler.Requests[0].Headers.Contains("api-key"));
        Assert.Contains("max_completion_tokens", handler.RequestBodies[0]);
        Assert.DoesNotContain("temperature", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task OpenAiToolCallsAreInvokedThroughTheFunctionPipeline()
    {
        var round = 0;
        var handler = new CannedHandler((_, body) =>
        {
            round++;
            return round == 1
                ? ("text/event-stream", Sse(
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"get_balance","arguments":""}}]}}]}""",
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"account\":\"12"}}]}}]}""",
                    """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"34\"}"}}]}}]}""",
                    "[DONE]"))
                : ("text/event-stream", Sse("""{"choices":[{"delta":{"content":"Saldo Anda 500 ribu."}}]}""", "[DONE]"));
        });

        string? seenAccount = null;
        var tool = AIFunctionFactory.Create((string account) => { seenAccount = account; return "Rp500.000"; }, "get_balance", "Account balance");
        using var inner = new OpenAiChatClient(new OpenAiChatOptions { ApiKey = "k" }, new HttpClient(handler));
        using var client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();

        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "saldo 1234?")], new ChatOptions { Tools = [tool] }))
        {
            text.Append(update.Text);
        }

        Assert.Equal("1234", seenAccount);
        Assert.Equal("Saldo Anda 500 ribu.", text.ToString());
        Assert.Contains("\"tool_call_id\":\"call_1\"", handler.RequestBodies[1]);
        Assert.Contains("Rp500.000", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task AnthropicStreamsTextAndToolUse()
    {
        var handler = new CannedHandler((_, _) => ("text/event-stream", Sse(
            """{"type":"message_start","message":{"id":"msg_1"}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Sebentar"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"tu_1","name":"lookup"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"q\":"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"x\"}"}}""",
            """{"type":"content_block_stop","index":1}""")));
        using var client = new AnthropicChatClient(new AnthropicChatOptions { ApiKey = "k" }, new HttpClient(handler));

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.System, "be brief"), new ChatMessage(ChatRole.User, "cari x")]))
        {
            updates.Add(update);
        }

        Assert.Equal("Sebentar", string.Concat(updates.Select(u => u.Text)));
        var call = updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("lookup", call.Name);
        Assert.Equal("tu_1", call.CallId);
        Assert.Equal("x", call.Arguments!["q"]!.ToString());
        Assert.Contains("\"system\":\"be brief\"", handler.RequestBodies[0]);
        Assert.True(handler.Requests[0].Headers.Contains("anthropic-version"));
    }

    [Fact]
    public async Task GeminiParsesTextAndFunctionCalls()
    {
        var handler = new CannedHandler((_, _) => ("application/json",
            """{"candidates":[{"content":{"parts":[{"text":"Baik."},{"functionCall":{"name":"book","args":{"day":"senin"}}}]}}],"usageMetadata":{"promptTokenCount":5,"candidatesTokenCount":2,"totalTokenCount":7}}"""));
        using var client = new GeminiChatClient(new GeminiChatOptions { ApiKey = "g" }, new HttpClient(handler));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "pesan senin")], new ChatOptions { Instructions = "sopan" });

        Assert.Equal("Baik.", response.Text);
        var call = response.Messages[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.Equal("book", call.Name);
        Assert.Contains("generateContent?key=g", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("systemInstruction", handler.RequestBodies[0]);
        Assert.Equal(7, response.Usage?.TotalTokenCount);
    }
}
