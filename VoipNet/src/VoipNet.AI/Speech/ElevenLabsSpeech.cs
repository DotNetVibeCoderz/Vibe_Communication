using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoipNet.AI.Speech;

/// <summary>Settings for the ElevenLabs speech services.</summary>
public sealed class ElevenLabsOptions
{
    /// <summary>ElevenLabs API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL of the API.</summary>
    public Uri BaseUri { get; set; } = new("https://api.elevenlabs.io/v1/");

    /// <summary>Voice identifier. The default is the "Rachel" preset voice.</summary>
    public string VoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM";

    /// <summary>Synthesis model.</summary>
    public string Model { get; set; } = "eleven_flash_v2_5";

    /// <summary>Transcription model.</summary>
    public string TranscriptionModel { get; set; } = "scribe_v1";

    /// <summary>Voice stability, from 0 to 1.</summary>
    public double Stability { get; set; } = 0.5;

    /// <summary>Similarity boost, from 0 to 1.</summary>
    public double SimilarityBoost { get; set; } = 0.75;
}

/// <summary>
/// Expressive speech synthesis with ElevenLabs. PCM is requested at the rate the call uses, so no
/// resampling is needed for 8, 16, 22.05, 24 or 44.1 kHz.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class ElevenLabsTextToSpeech(ElevenLabsOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private static readonly int[] SupportedRates = [8000, 16000, 22050, 24000, 44100];
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "ElevenLabs";

    /// <inheritdoc/>
    public int PreferredSampleRate => 16000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rate = NearestRate(ttsOptions?.SampleRate ?? PreferredSampleRate);
        var voice = ttsOptions?.Voice ?? options.VoiceId;
        var uri = new Uri(options.BaseUri, $"text-to-speech/{voice}/stream?output_format=pcm_{rate}");

        var payload = new
        {
            text,
            model_id = ttsOptions?.Model ?? options.Model,
            voice_settings = new
            {
                stability = options.Stability,
                similarity_boost = options.SimilarityBoost,
                speed = ttsOptions?.Speed ?? 1.0,
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("xi-api-key", options.ApiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            yield return new AudioChunk(buffer.AsMemory(0, read).ToArray(), rate);
        }
    }

    private static int NearestRate(int requested) =>
        SupportedRates.OrderBy(r => Math.Abs(r - requested)).First();
}

/// <summary>Transcription with the ElevenLabs Scribe models.</summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class ElevenLabsSpeechToText(ElevenLabsOptions options, HttpClient? httpClient = null) : BufferedSpeechToText
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public override string Name => "ElevenLabs";

    /// <inheritdoc/>
    public override async Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? speechOptions = null,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var wav = new ByteArrayContent(ToWav(pcm, sampleRate));
        wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(wav, "file", "audio.wav");
        content.Add(new StringContent(speechOptions?.Model ?? options.TranscriptionModel), "model_id");
        if (speechOptions?.Language is { Length: > 0 } language)
        {
            content.Add(new StringContent(language.Split('-')[0]), "language_code");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "speech-to-text")) { Content = content };
        request.Headers.TryAddWithoutValidation("xi-api-key", options.ApiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
    }
}
