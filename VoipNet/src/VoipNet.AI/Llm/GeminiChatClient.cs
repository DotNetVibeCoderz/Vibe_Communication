using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace VoipNet.AI.Llm;

/// <summary>Settings for the Google Gemini API.</summary>
public sealed class GeminiChatOptions
{
    /// <summary>Gemini API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL of the API.</summary>
    public Uri BaseUri { get; set; } = new("https://generativelanguage.googleapis.com/v1beta/");

    /// <summary>Model name.</summary>
    public string Model { get; set; } = "gemini-2.0-flash";
}

/// <summary>Chat completions with Google Gemini, including streaming and function calling.</summary>
public sealed class GeminiChatClient : IChatClient
{
    private readonly GeminiChatOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>Creates a client.</summary>
    /// <param name="options">Endpoint settings.</param>
    /// <param name="httpClient">HTTP client to use; one is created when null.</param>
    public GeminiChatClient(GeminiChatOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttp = httpClient is null;
        Metadata = new ChatClientMetadata("google.gemini", options.BaseUri, options.Model);
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
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
            {
                contents.AddRange(ReadParts(parts));
            }
        }

        var result = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)) { ModelId = _options.Model };
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            result.Usage = new UsageDetails
            {
                InputTokenCount = usage.TryGetProperty("promptTokenCount", out var p) ? p.GetInt64() : null,
                OutputTokenCount = usage.TryGetProperty("candidatesTokenCount", out var c) ? c.GetInt64() : null,
                TotalTokenCount = usage.TryGetProperty("totalTokenCount", out var t) ? t.GetInt64() : null,
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
                if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                {
                    continue;
                }

                var candidate = candidates[0];
                if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                {
                    continue;
                }

                foreach (var item in ReadParts(parts))
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [item]) { ModelId = _options.Model };
                }
            }
        }
    }

    private static IEnumerable<AIContent> ReadParts(JsonElement parts)
    {
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } value)
            {
                yield return new TextContent(value);
            }
            else if (part.TryGetProperty("functionCall", out var call))
            {
                yield return new FunctionCallContent(
                    Guid.NewGuid().ToString("N"),
                    call.GetProperty("name").GetString() ?? string.Empty,
                    call.TryGetProperty("args", out var args)
                        ? ChatClientHelpers.ParseArguments(args.GetRawText())
                        : new Dictionary<string, object?>());
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
        var model = options?.ModelId ?? _options.Model;
        var method = stream ? "streamGenerateContent" : "generateContent";
        var uri = new UriBuilder(new Uri(_options.BaseUri, $"models/{model}:{method}"))
        {
            Query = stream ? $"alt=sse&key={Uri.EscapeDataString(_options.ApiKey)}" : $"key={Uri.EscapeDataString(_options.ApiKey)}",
        }.Uri;

        var payload = BuildRequest(messages, options);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(ChatClientHelpers.Json), Encoding.UTF8, "application/json"),
        };

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"Gemini returned {(int)response.StatusCode}: {error}");
        }

        return response;
    }

    private static JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var contents = new JsonArray();
        var system = new StringBuilder(options?.Instructions ?? string.Empty);

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

            var parts = new JsonArray();
            var role = message.Role == ChatRole.Assistant ? "model" : "user";
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent t when !string.IsNullOrEmpty(t.Text):
                        parts.Add(new JsonObject { ["text"] = t.Text });
                        break;
                    case FunctionCallContent call:
                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["args"] = JsonSerializer.SerializeToNode(call.Arguments, ChatClientHelpers.Json) ?? new JsonObject(),
                            },
                        });
                        break;
                    case FunctionResultContent result:
                        role = "user";
                        parts.Add(new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = result.CallId,
                                ["response"] = new JsonObject { ["result"] = ChatClientHelpers.ResultToString(result.Result) },
                            },
                        });
                        break;
                }
            }

            if (parts.Count > 0)
            {
                contents.Add(new JsonObject { ["role"] = role, ["parts"] = parts });
            }
        }

        var payload = new JsonObject { ["contents"] = contents };
        if (system.Length > 0)
        {
            payload["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system.ToString() }) };
        }

        var generation = new JsonObject();
        if (options?.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        if (options?.MaxOutputTokens is { } maxTokens)
        {
            generation["maxOutputTokens"] = maxTokens;
        }

        if (options?.TopP is { } topP)
        {
            generation["topP"] = topP;
        }

        if (generation.Count > 0)
        {
            payload["generationConfig"] = generation;
        }

        var functions = options?.Tools?.OfType<AIFunction>().ToList();
        if (functions is { Count: > 0 })
        {
            var declarations = new JsonArray();
            foreach (var function in functions)
            {
                declarations.Add(new JsonObject
                {
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    // Gemini accepts an OpenAPI subset, so JSON Schema keywords it rejects are removed.
                    ["parameters"] = ChatClientHelpers.FunctionSchema(function, stripUnsupportedKeywords: true),
                });
            }

            payload["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
        }

        return payload;
    }

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
