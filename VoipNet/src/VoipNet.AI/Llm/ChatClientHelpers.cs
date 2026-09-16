using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace VoipNet.AI.Llm;

/// <summary>Shared plumbing for the hand written chat clients.</summary>
internal static class ChatClientHelpers
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads a server-sent event stream and yields the payload of each <c>data:</c> line.</summary>
    internal static async IAsyncEnumerable<string> ReadServerSentEventsAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                if (payload == "[DONE]")
                {
                    yield break;
                }

                continue;
            }

            yield return payload;
        }
    }

    /// <summary>Converts an <see cref="AIFunction"/> schema into a plain JSON node the providers accept.</summary>
    internal static JsonNode? FunctionSchema(AIFunction function, bool stripUnsupportedKeywords)
    {
        var node = JsonSerializer.SerializeToNode(function.JsonSchema);
        if (node is JsonObject obj && stripUnsupportedKeywords)
        {
            Strip(obj);
        }

        return node;

        static void Strip(JsonObject obj)
        {
            foreach (var keyword in new[] { "$schema", "additionalProperties", "default", "examples" })
            {
                obj.Remove(keyword);
            }

            foreach (var child in obj.ToList())
            {
                switch (child.Value)
                {
                    case JsonObject childObject:
                        Strip(childObject);
                        break;
                    case JsonArray array:
                        foreach (var item in array.OfType<JsonObject>())
                        {
                            Strip(item);
                        }

                        break;
                }
            }
        }
    }

    /// <summary>Parses a JSON object of function arguments into the dictionary the abstractions use.</summary>
    internal static IDictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            return node?.ToDictionary(p => p.Key, p => (object?)p.Value) ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>Renders the result of a tool call as text the model can read.</summary>
    internal static string ResultToString(object? result) => result switch
    {
        null => string.Empty,
        string s => s,
        JsonElement e => e.ToString(),
        _ => JsonSerializer.Serialize(result, Json),
    };
}
