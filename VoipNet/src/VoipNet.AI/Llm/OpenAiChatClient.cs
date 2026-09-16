using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace VoipNet.AI.Llm;

/// <summary>Settings for an OpenAI or OpenAI-compatible chat endpoint.</summary>
public sealed class OpenAiChatOptions
{
    /// <summary>API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL. Point this at Azure OpenAI, OpenRouter, Ollama or any compatible server.</summary>
    public Uri BaseUri { get; set; } = new("https://api.openai.com/v1/");

    /// <summary>Model name.</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Extra headers, for example an organisation id.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Sends the key in an <c>api-key</c> header instead of <c>Authorization: Bearer</c>, as Azure
    /// OpenAI expects.
    /// </summary>
    public bool UseApiKeyHeader { get; set; }

    /// <summary>
    /// Treat the model as a reasoning model (GPT-5, o-series): send <c>max_completion_tokens</c> and
    /// omit sampling parameters those models reject. Null detects it from the model name.
    /// </summary>
    public bool? ReasoningModel { get; set; }

    /// <summary>
    /// Reasoning effort for reasoning models: <c>minimal</c>, <c>low</c>, <c>medium</c> or <c>high</c>.
    /// Voice agents usually want <c>minimal</c> or <c>low</c> to keep the reply latency short.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Creates settings for an Azure OpenAI resource using the v1 API.</summary>
    /// <param name="endpoint">Resource endpoint, for example <c>https://my-resource.openai.azure.com/</c>.</param>
    /// <param name="apiKey">Resource key.</param>
    /// <param name="deployment">Deployment name.</param>
    public static OpenAiChatOptions ForAzure(string endpoint, string apiKey, string deployment) => new()
    {
        BaseUri = new Uri(new Uri(endpoint.TrimEnd('/') + "/"), "openai/v1/"),
        ApiKey = apiKey,
        Model = deployment,
        UseApiKeyHeader = true,
    };
}

/// <summary>
/// Chat completions over the OpenAI wire format, including streaming and tool calls. Because so
/// many providers speak this protocol, one client covers OpenAI, Azure OpenAI, OpenRouter, vLLM,
/// Ollama and similar servers.
/// </summary>
public sealed class OpenAiChatClient : IChatClient
{
    private readonly OpenAiChatOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Endpoint settings.</param>
    /// <param name="httpClient">HTTP client to use; one is created when null.</param>
    public OpenAiChatClient(OpenAiChatOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = httpClient is null;
        Metadata = new ChatClientMetadata("openai", options.BaseUri, options.Model);
    }

    /// <summary>Provider metadata.</summary>
    public ChatClientMetadata Metadata { get; }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(messages, options, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var choice = document.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var contents = new List<AIContent>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
        {
            contents.Add(new TextContent(text));
        }

        if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                contents.Add(new FunctionCallContent(
                    call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    function.GetProperty("name").GetString() ?? string.Empty,
                    ChatClientHelpers.ParseArguments(function.TryGetProperty("arguments", out var a) ? a.GetString() : null)));
            }
        }

        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = document.RootElement.TryGetProperty("model", out var model) ? model.GetString() : _options.Model,
            ResponseId = document.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null,
            FinishReason = choice.TryGetProperty("finish_reason", out var reason) ? ToFinishReason(reason.GetString()) : null,
        };

        if (document.RootElement.TryGetProperty("usage", out var usage))
        {
            result.Usage = new UsageDetails
            {
                InputTokenCount = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt64() : null,
                OutputTokenCount = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt64() : null,
                TotalTokenCount = usage.TryGetProperty("total_tokens", out var t) ? t.GetInt64() : null,
            };
        }

        return result;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(messages, options, stream: true, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        // Tool call arguments arrive in fragments keyed by their index in the call list.
        var toolCalls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        string? responseId = null;

        await foreach (var payload in ChatClientHelpers.ReadServerSentEventsAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                responseId ??= root.TryGetProperty("id", out var id) ? id.GetString() : null;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                var delta = choice.TryGetProperty("delta", out var d) ? d : default;

                if (delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is { Length: > 0 } text)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = responseId, ModelId = _options.Model };
                }

                if (delta.ValueKind == JsonValueKind.Object && delta.TryGetProperty("tool_calls", out var calls))
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                        if (!toolCalls.TryGetValue(index, out var entry))
                        {
                            entry = (Guid.NewGuid().ToString("N"), string.Empty, new StringBuilder());
                        }

                        if (call.TryGetProperty("id", out var callId) && callId.GetString() is { Length: > 0 } cid)
                        {
                            entry.Id = cid;
                        }

                        if (call.TryGetProperty("function", out var function))
                        {
                            if (function.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } n)
                            {
                                entry.Name = n;
                            }

                            if (function.TryGetProperty("arguments", out var args) && args.GetString() is { Length: > 0 } fragment)
                            {
                                entry.Arguments.Append(fragment);
                            }
                        }

                        toolCalls[index] = entry;
                    }
                }
            }
        }

        foreach (var (_, call) in toolCalls)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, [
                new FunctionCallContent(call.Id, call.Name, ChatClientHelpers.ParseArguments(call.Arguments.ToString()))
            ])
            {
                ResponseId = responseId,
                ModelId = _options.Model,
            };
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var body = BuildRequest(messages, options, stream);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(ChatClientHelpers.Json), Encoding.UTF8, "application/json"),
        };
        if (_options.ApiKey is { Length: > 0 } && _options.UseApiKeyHeader)
        {
            request.Headers.TryAddWithoutValidation("api-key", _options.ApiKey);
        }
        else if (_options.ApiKey is { Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        foreach (var (name, value) in _options.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"The chat provider returned {(int)response.StatusCode}: {error}");
        }

        return response;
    }

    private JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var model = options?.ModelId ?? _options.Model;
        var reasoning = _options.ReasoningModel ?? IsReasoningModel(model);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = stream,
        };
        if (options?.Temperature is { } temperature && !reasoning)
        {
            payload["temperature"] = temperature;
        }

        if (options?.MaxOutputTokens is { } maxTokens)
        {
            payload[reasoning ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        }

        if (options?.TopP is { } topP && !reasoning)
        {
            payload["top_p"] = topP;
        }

        if (reasoning && _options.ReasoningEffort is { Length: > 0 } effort)
        {
            payload["reasoning_effort"] = effort;
        }

        var list = new JsonArray();
        if (options?.Instructions is { Length: > 0 } instructions)
        {
            list.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
        }

        foreach (var message in messages)
        {
            foreach (var item in ToWire(message))
            {
                list.Add(item);
            }
        }

        payload["messages"] = list;

        var functions = options?.Tools?.OfType<AIFunction>().ToList();
        if (functions is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var function in functions)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = function.Name,
                        ["description"] = function.Description,
                        ["parameters"] = ChatClientHelpers.FunctionSchema(function, stripUnsupportedKeywords: false),
                    },
                });
            }

            payload["tools"] = tools;
        }

        return payload;
    }

    private static IEnumerable<JsonObject> ToWire(ChatMessage message)
    {
        var role = message.Role.Value switch
        {
            "system" => "system",
            "tool" => "tool",
            "assistant" => "assistant",
            _ => "user",
        };

        var text = new StringBuilder();
        var toolCalls = new JsonArray();
        var results = new List<JsonObject>();

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent t:
                    text.Append(t.Text);
                    break;
                case FunctionCallContent call:
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = call.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = JsonSerializer.Serialize(call.Arguments, ChatClientHelpers.Json),
                        },
                    });
                    break;
                case FunctionResultContent result:
                    results.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = result.CallId,
                        ["content"] = ChatClientHelpers.ResultToString(result.Result),
                    });
                    break;
            }
        }

        foreach (var result in results)
        {
            yield return result;
        }

        if (text.Length > 0 || toolCalls.Count > 0)
        {
            var wire = new JsonObject { ["role"] = role };
            if (text.Length > 0)
            {
                wire["content"] = text.ToString();
            }

            if (toolCalls.Count > 0)
            {
                wire["tool_calls"] = toolCalls;
            }

            yield return wire;
        }
    }

    private static bool IsReasoningModel(string model) =>
        model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
        model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

    private static ChatFinishReason? ToFinishReason(string? reason) => reason switch
    {
        "stop" => ChatFinishReason.Stop,
        "length" => ChatFinishReason.Length,
        "tool_calls" or "function_call" => ChatFinishReason.ToolCalls,
        "content_filter" => ChatFinishReason.ContentFilter,
        _ => null,
    };

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? Metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
