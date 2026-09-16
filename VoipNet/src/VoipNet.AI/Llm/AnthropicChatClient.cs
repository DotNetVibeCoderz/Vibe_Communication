using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace VoipNet.AI.Llm;

/// <summary>Settings for the Anthropic Messages API.</summary>
public sealed class AnthropicChatOptions
{
    /// <summary>Anthropic API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL of the API.</summary>
    public Uri BaseUri { get; set; } = new("https://api.anthropic.com/v1/");

    /// <summary>Model name.</summary>
    public string Model { get; set; } = "claude-sonnet-5";

    /// <summary>API version header value.</summary>
    public string ApiVersion { get; set; } = "2023-06-01";

    /// <summary>Default output limit, which the Messages API requires.</summary>
    public int MaxOutputTokens { get; set; } = 1024;
}

/// <summary>Chat completions with Anthropic Claude, including streaming and tool use.</summary>
public sealed class AnthropicChatClient : IChatClient
{
    private readonly AnthropicChatOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Endpoint settings.</param>
    /// <param name="httpClient">HTTP client to use; one is created when null.</param>
    public AnthropicChatClient(AnthropicChatOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = httpClient is null;
        Metadata = new ChatClientMetadata("anthropic", options.BaseUri, options.Model);
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
        var root = document.RootElement;

        var contents = new List<AIContent>();
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            switch (block.GetProperty("type").GetString())
            {
                case "text":
                    contents.Add(new TextContent(block.GetProperty("text").GetString() ?? string.Empty));
                    break;
                case "tool_use":
                    contents.Add(new FunctionCallContent(
                        block.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                        block.GetProperty("name").GetString() ?? string.Empty,
                        ChatClientHelpers.ParseArguments(block.GetProperty("input").GetRawText())));
                    break;
            }
        }

        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = root.TryGetProperty("model", out var model) ? model.GetString() : _options.Model,
            ResponseId = root.TryGetProperty("id", out var id) ? id.GetString() : null,
            FinishReason = root.TryGetProperty("stop_reason", out var stop) ? ToFinishReason(stop.GetString()) : null,
        };

        if (root.TryGetProperty("usage", out var usage))
        {
            result.Usage = new UsageDetails
            {
                InputTokenCount = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : null,
                OutputTokenCount = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : null,
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

        // Tool arguments stream as partial JSON inside the block they belong to.
        var openTools = new Dictionary<int, (string Id, string Name, StringBuilder Arguments)>();
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
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "message_start":
                        responseId = root.GetProperty("message").TryGetProperty("id", out var id) ? id.GetString() : null;
                        break;

                    case "content_block_start" when root.GetProperty("content_block").GetProperty("type").GetString() == "tool_use":
                        var start = root.GetProperty("content_block");
                        openTools[root.GetProperty("index").GetInt32()] = (
                            start.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                            start.GetProperty("name").GetString() ?? string.Empty,
                            new StringBuilder());
                        break;

                    case "content_block_delta":
                        var delta = root.GetProperty("delta");
                        var deltaType = delta.GetProperty("type").GetString();
                        if (deltaType == "text_delta" && delta.GetProperty("text").GetString() is { Length: > 0 } text)
                        {
                            yield return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = responseId, ModelId = _options.Model };
                        }
                        else if (deltaType == "input_json_delta" && openTools.TryGetValue(root.GetProperty("index").GetInt32(), out var tool))
                        {
                            tool.Arguments.Append(delta.GetProperty("partial_json").GetString());
                        }

                        break;

                    case "content_block_stop" when openTools.Remove(root.GetProperty("index").GetInt32(), out var finished):
                        yield return new ChatResponseUpdate(ChatRole.Assistant, [
                            new FunctionCallContent(finished.Id, finished.Name, ChatClientHelpers.ParseArguments(finished.Arguments.ToString()))
                        ])
                        {
                            ResponseId = responseId,
                            ModelId = _options.Model,
                        };
                        break;
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var payload = BuildRequest(messages, options, stream);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, "messages"))
        {
            Content = new StringContent(payload.ToJsonString(ChatClientHelpers.Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", _options.ApiVersion);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"Anthropic returned {(int)response.StatusCode}: {error}");
        }

        return response;
    }

    private JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = options?.ModelId ?? _options.Model,
            ["max_tokens"] = options?.MaxOutputTokens ?? _options.MaxOutputTokens,
            ["stream"] = stream,
        };
        if (options?.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }

        if (options?.TopP is { } topP)
        {
            payload["top_p"] = topP;
        }

        var system = new StringBuilder(options?.Instructions ?? string.Empty);
        var wire = new JsonArray();

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (system.Length > 0)
                {
                    system.Append("\n\n");
                }

                system.Append(message.Text);
                continue;
            }

            var blocks = new JsonArray();
            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                        break;
                    case FunctionCallContent call:
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.CallId,
                            ["name"] = call.Name,
                            ["input"] = JsonSerializer.SerializeToNode(call.Arguments, ChatClientHelpers.Json) ?? new JsonObject(),
                        });
                        break;
                    case FunctionResultContent result:
                        // Tool results are sent as a user turn, which is what the Messages API expects.
                        role = "user";
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.CallId,
                            ["content"] = ChatClientHelpers.ResultToString(result.Result),
                        });
                        break;
                }
            }

            if (blocks.Count > 0)
            {
                wire.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
            }
        }

        payload["messages"] = wire;
        if (system.Length > 0)
        {
            payload["system"] = system.ToString();
        }

        var functions = options?.Tools?.OfType<AIFunction>().ToList();
        if (functions is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var function in functions)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["input_schema"] = ChatClientHelpers.FunctionSchema(function, stripUnsupportedKeywords: false),
                });
            }

            payload["tools"] = tools;
        }

        return payload;
    }

    private static ChatFinishReason? ToFinishReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
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
