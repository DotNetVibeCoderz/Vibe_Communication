using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoipNet.AI.Speech;

/// <summary>Settings for the Google Cloud speech services.</summary>
public sealed class GoogleCloudSpeechOptions
{
    /// <summary>API key for key-based access.</summary>
    public string? ApiKey { get; set; }

    /// <summary>OAuth access token, when using a service account instead of an API key.</summary>
    public string? AccessToken { get; set; }

    /// <summary>Speech-to-Text endpoint.</summary>
    public Uri SpeechUri { get; set; } = new("https://speech.googleapis.com/v1/speech:recognize");

    /// <summary>Text-to-Speech endpoint.</summary>
    public Uri TextToSpeechUri { get; set; } = new("https://texttospeech.googleapis.com/v1/text:synthesize");

    /// <summary>Default language tag.</summary>
    public string Language { get; set; } = "id-ID";

    /// <summary>Recognition model, for example <c>telephony</c> or <c>latest_long</c>.</summary>
    public string RecognitionModel { get; set; } = "telephony";

    /// <summary>Voice name, for example <c>id-ID-Wavenet-A</c>.</summary>
    public string Voice { get; set; } = "id-ID-Wavenet-A";
}

/// <summary>Transcription with Google Cloud Speech-to-Text.</summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class GoogleCloudSpeechToText(GoogleCloudSpeechOptions options, HttpClient? httpClient = null) : BufferedSpeechToText
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public override string Name => "Google Cloud";

    /// <inheritdoc/>
    public override async Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? speechOptions = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            config = new
            {
                encoding = "LINEAR16",
                sampleRateHertz = sampleRate,
                languageCode = speechOptions?.Language ?? options.Language,
                model = speechOptions?.Model ?? options.RecognitionModel,
                enableAutomaticPunctuation = true,
                speechContexts = speechOptions?.Hints.Count > 0 ? new[] { new { phrases = speechOptions.Hints.ToArray() } } : null,
            },
            audio = new { content = Convert.ToBase64String(pcm.Span) },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, WithKey(options.SpeechUri))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        Authorize(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var result in results.EnumerateArray())
        {
            text.Append(result.GetProperty("alternatives")[0].GetProperty("transcript").GetString());
        }

        return text.ToString();
    }

    private Uri WithKey(Uri uri) =>
        options.ApiKey is { Length: > 0 } key ? new UriBuilder(uri) { Query = $"key={Uri.EscapeDataString(key)}" }.Uri : uri;

    private void Authorize(HttpRequestMessage request)
    {
        if (options.AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}

/// <summary>Speech synthesis with Google Cloud Text-to-Speech.</summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class GoogleCloudTextToSpeech(GoogleCloudSpeechOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "Google Cloud";

    /// <inheritdoc/>
    public int PreferredSampleRate => 16000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rate = ttsOptions?.SampleRate ?? PreferredSampleRate;
        var voice = ttsOptions?.Voice ?? options.Voice;
        var language = ttsOptions?.Language ?? options.Language;
        var payload = new
        {
            input = new { text },
            voice = new { languageCode = language, name = voice },
            audioConfig = new { audioEncoding = "LINEAR16", sampleRateHertz = rate, speakingRate = ttsOptions?.Speed ?? 1.0 },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, WithKey(options.TextToSpeechUri))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        if (options.AccessToken is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var audio = Convert.FromBase64String(document.RootElement.GetProperty("audioContent").GetString() ?? string.Empty);

        // LINEAR16 comes back inside a WAV container; hand the caller the PCM payload only.
        var offset = FindDataChunk(audio);
        yield return new AudioChunk(audio.AsMemory(offset), rate);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Uri WithKey(Uri uri) =>
        options.ApiKey is { Length: > 0 } key ? new UriBuilder(uri) { Query = $"key={Uri.EscapeDataString(key)}" }.Uri : uri;

    private static int FindDataChunk(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8))
        {
            return 0;
        }

        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var size = BitConverter.ToInt32(wav.Slice(position + 4, 4));
            if (wav.Slice(position, 4).SequenceEqual("data"u8))
            {
                return position + 8;
            }

            position += 8 + size + (size % 2);
        }

        return 44;
    }
}
