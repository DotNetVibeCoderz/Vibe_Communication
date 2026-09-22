using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoipNet.AI.Speech;

/// <summary>Settings for Cartesia text to speech.</summary>
public sealed class CartesiaOptions
{
    /// <summary>API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Endpoint that returns raw audio bytes.</summary>
    public Uri BaseUri { get; set; } = new("https://api.cartesia.ai/tts/bytes");

    /// <summary>API version the service expects, sent as <c>Cartesia-Version</c>.</summary>
    public string ApiVersion { get; set; } = "2024-11-13";

    /// <summary>Model identifier, for example <c>sonic-2</c>.</summary>
    public string Model { get; set; } = "sonic-2";

    /// <summary>Voice identifier from the Cartesia voice library.</summary>
    public string Voice { get; set; } = string.Empty;

    /// <summary>Language code the voice speaks, for example <c>id</c> or <c>en</c>.</summary>
    public string Language { get; set; } = "id";
}

/// <summary>
/// Speech synthesis with Cartesia. The service returns raw little-endian PCM, which goes straight
/// onto a call without a container to unwrap or a codec to decode.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class CartesiaTextToSpeech(CartesiaOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "Cartesia";

    /// <inheritdoc/>
    public int PreferredSampleRate => 24000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rate = ttsOptions?.SampleRate ?? PreferredSampleRate;
        var voice = ttsOptions?.Voice ?? options.Voice;
        var payload = new
        {
            model_id = ttsOptions?.Model ?? options.Model,
            transcript = text,
            voice = new { mode = "id", id = voice },
            language = ttsOptions?.Language ?? options.Language,
            output_format = new { container = "raw", encoding = "pcm_s16le", sample_rate = rate },
            speed = Speed(ttsOptions?.Speed ?? 1.0),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-API-Key", options.ApiKey);
        request.Headers.Add("Cartesia-Version", options.ApiVersion);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in PcmStream.ReadChunksAsync(stream, rate, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>Cartesia names its speeds rather than taking a multiplier.</summary>
    private static string Speed(double speed) => speed switch
    {
        < 0.85 => "slow",
        > 1.15 => "fast",
        _ => "normal",
    };
}
