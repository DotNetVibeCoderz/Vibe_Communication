using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoipNet.AI.Speech;

/// <summary>Settings shared by the OpenAI speech services. Works with OpenAI-compatible servers too.</summary>
public sealed class OpenAiSpeechOptions
{
    /// <summary>API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Base URL, for example the OpenAI endpoint or a compatible local server.</summary>
    public Uri BaseUri { get; set; } = new("https://api.openai.com/v1/");

    /// <summary>Transcription model.</summary>
    public string TranscriptionModel { get; set; } = "gpt-4o-mini-transcribe";

    /// <summary>Synthesis model.</summary>
    public string SpeechModel { get; set; } = "gpt-4o-mini-tts";

    /// <summary>Default voice.</summary>
    public string Voice { get; set; } = "alloy";

    /// <summary>Optional instruction that shapes how the voice sounds.</summary>
    public string? VoiceInstructions { get; set; }
}

/// <summary>Transcription with OpenAI (Whisper or the GPT-4o transcribe models).</summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class OpenAiSpeechToText(OpenAiSpeechOptions options, HttpClient? httpClient = null) : BufferedSpeechToText
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public override string Name => "OpenAI";

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
        content.Add(new StringContent(speechOptions?.Model ?? options.TranscriptionModel), "model");
        if (speechOptions?.Language is { Length: > 0 } language)
        {
            content.Add(new StringContent(language.Split('-')[0]), "language");
        }

        if (speechOptions?.Hints.Count > 0)
        {
            content.Add(new StringContent(string.Join(", ", speechOptions.Hints)), "prompt");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "audio/transcriptions")) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync("OpenAI", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty;
    }
}

/// <summary>Speech synthesis with OpenAI. Audio is requested as raw PCM so it can go straight into a call.</summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class OpenAiTextToSpeech(OpenAiSpeechOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "OpenAI";

    /// <summary>OpenAI streams PCM at 24 kHz; the engine resamples to the codec rate.</summary>
    public int PreferredSampleRate => 24000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = ttsOptions?.Model ?? options.SpeechModel,
            ["voice"] = ttsOptions?.Voice ?? options.Voice,
            ["input"] = text,
            ["response_format"] = "pcm",
            ["speed"] = ttsOptions?.Speed ?? 1.0,
        };
        if (options.VoiceInstructions is { Length: > 0 } instructions)
        {
            payload["instructions"] = instructions;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "audio/speech"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync("OpenAI", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var chunk in PcmStream.ReadChunksAsync(stream, PreferredSampleRate, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}
