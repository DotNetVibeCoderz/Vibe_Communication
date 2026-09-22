using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace VoipNet.AI.Speech;

/// <summary>Settings for Azure AI Speech (the Cognitive Services speech endpoints).</summary>
public sealed class AzureSpeechOptions
{
    /// <summary>Subscription key for the Speech resource.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Azure region of the resource, for example <c>southeastasia</c>.</summary>
    public string Region { get; set; } = "southeastasia";

    /// <summary>Recognition endpoint. Set this for a custom or sovereign deployment.</summary>
    public Uri? SpeechUri { get; set; }

    /// <summary>Synthesis endpoint. Set this for a custom or sovereign deployment.</summary>
    public Uri? TextToSpeechUri { get; set; }

    /// <summary>Default language tag.</summary>
    public string Language { get; set; } = "id-ID";

    /// <summary>Default neural voice, for example <c>id-ID-GadisNeural</c>.</summary>
    public string Voice { get; set; } = "id-ID-GadisNeural";

    /// <summary>Recognition endpoint for the region, or the override.</summary>
    public Uri RecognitionEndpoint =>
        SpeechUri ?? new Uri($"https://{Region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1");

    /// <summary>Synthesis endpoint for the region, or the override.</summary>
    public Uri SynthesisEndpoint =>
        TextToSpeechUri ?? new Uri($"https://{Region}.tts.speech.microsoft.com/cognitiveservices/v1");
}

/// <summary>
/// Transcription with Azure AI Speech. Utterances are sent to the short-audio endpoint, which takes
/// audio up to a minute — the length of a caller's sentence, which is what the agent loop feeds it.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class AzureSpeechToText(AzureSpeechOptions options, HttpClient? httpClient = null) : BufferedSpeechToText
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public override string Name => "Azure AI Speech";

    /// <inheritdoc/>
    public override async Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? speechOptions = null,
        CancellationToken cancellationToken = default)
    {
        var language = speechOptions?.Language ?? options.Language;
        var query = $"language={Uri.EscapeDataString(language)}&format=detailed&profanity=raw";
        var uri = new UriBuilder(options.RecognitionEndpoint) { Query = query }.Uri;

        // The endpoint reads a WAV stream, so the raw PCM gets a header rather than a conversion.
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(Wav(pcm.Span, sampleRate)) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav")
        {
            Parameters = { new NameValueHeaderValue("codecs", "audio/pcm"), new NameValueHeaderValue("samplerate", sampleRate.ToString()) },
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (speechOptions?.Hints.Count > 0)
        {
            // Phrase hints ride along as a custom header the service reads for biasing.
            request.Headers.Add("Speech-PhraseList", string.Join(",", speechOptions.Hints));
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        // "RecognitionStatus": Success, NoMatch, InitialSilenceTimeout… Only Success carries text.
        if (root.TryGetProperty("DisplayText", out var display) && display.ValueKind == JsonValueKind.String)
        {
            return display.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("NBest", out var best) && best.ValueKind == JsonValueKind.Array && best.GetArrayLength() > 0)
        {
            return best[0].TryGetProperty("Display", out var text) ? text.GetString() ?? string.Empty : string.Empty;
        }

        return string.Empty;
    }

    /// <summary>Wraps PCM in a minimal WAV header, which is what the short-audio endpoint expects.</summary>
    private static byte[] Wav(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        var wav = new byte[44 + pcm.Length];
        var header = wav.AsSpan();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], pcm.Length);
        pcm.CopyTo(header[44..]);
        return wav;
    }
}

/// <summary>Speech synthesis with Azure AI Speech neural voices.</summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class AzureTextToSpeech(AzureSpeechOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "Azure AI Speech";

    /// <inheritdoc/>
    public int PreferredSampleRate => 24000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rate = Nearest(ttsOptions?.SampleRate ?? PreferredSampleRate);
        var voice = ttsOptions?.Voice ?? options.Voice;
        var language = ttsOptions?.Language ?? options.Language;

        using var request = new HttpRequestMessage(HttpMethod.Post, options.SynthesisEndpoint)
        {
            Content = new StringContent(Ssml(text, voice, language, ttsOptions?.Speed ?? 1.0), Encoding.UTF8, "application/ssml+xml"),
        };
        request.Headers.Add("Ocp-Apim-Subscription-Key", options.ApiKey);
        request.Headers.Add("X-Microsoft-OutputFormat", $"raw-{rate / 1000}khz-16bit-mono-pcm");
        request.Headers.Add("User-Agent", "VoipNet");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // The response is raw PCM, streamed so playback starts before the sentence is finished.
        await foreach (var chunk in PcmStream.ReadChunksAsync(stream, rate, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>The service offers a few fixed rates; anything else is rounded to the closest one.</summary>
    private static int Nearest(int sampleRate) =>
        new[] { 8000, 16000, 24000, 48000 }.OrderBy(rate => Math.Abs(rate - sampleRate)).First();

    private static string Ssml(string text, string voice, string language, double speed)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false };
        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, settings))
        {
            writer.WriteStartElement("speak", "http://www.w3.org/2001/10/synthesis");
            writer.WriteAttributeString("version", "1.0");
            writer.WriteAttributeString("xml", "lang", null, language);
            writer.WriteStartElement("voice");
            writer.WriteAttributeString("name", voice);
            if (Math.Abs(speed - 1.0) > 0.01)
            {
                writer.WriteStartElement("prosody");
                writer.WriteAttributeString("rate", $"{(speed - 1) * 100:+0;-0}%");
                writer.WriteString(text);
                writer.WriteEndElement();
            }
            else
            {
                writer.WriteString(text);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return output.ToString();
    }
}
