using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoipNet.AI.Speech;

/// <summary>Settings for <see cref="DeepgramSpeechToText"/>.</summary>
public sealed class DeepgramOptions
{
    /// <summary>Deepgram API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Recognition model.</summary>
    public string Model { get; set; } = "nova-3";

    /// <summary>Default language tag.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Add punctuation to the transcript.</summary>
    public bool Punctuate { get; set; } = true;

    /// <summary>Mark each speaker in multi-party audio.</summary>
    public bool Diarize { get; set; }

    /// <summary>Base URL, for on-premises deployments.</summary>
    public Uri BaseUri { get; set; } = new("wss://api.deepgram.com/v1/listen");

    /// <summary>Batch endpoint used by <see cref="ISpeechToText.TranscribeOnceAsync"/>.</summary>
    public Uri BatchUri { get; set; } = new("https://api.deepgram.com/v1/listen");

    /// <summary>Synthesis endpoint for Aura voices.</summary>
    public Uri SpeakUri { get; set; } = new("https://api.deepgram.com/v1/speak");

    /// <summary>Aura voice, for example <c>aura-2-thalia-en</c>.</summary>
    public string Voice { get; set; } = "aura-2-thalia-en";
}

/// <summary>
/// Speech synthesis with Deepgram Aura. The service returns raw little-endian PCM at the rate that
/// was asked for, so the audio goes straight onto a call.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class DeepgramTextToSpeech(DeepgramOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "Deepgram Aura";

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
        var voice = ttsOptions?.Voice ?? ttsOptions?.Model ?? options.Voice;
        var uri = new UriBuilder(options.SpeakUri)
        {
            Query = $"model={Uri.EscapeDataString(voice)}&encoding=linear16&sample_rate={rate}",
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {options.ApiKey}");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync(Name, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var chunk in PcmStream.ReadChunksAsync(stream, rate, cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }
}

/// <summary>
/// Real-time transcription with Deepgram. Audio is streamed over a web socket and interim results
/// arrive while the caller is still speaking, which is what makes barge-in feel immediate.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="httpClient">HTTP client used for one-shot transcription.</param>
/// <param name="logger">Optional logger.</param>
public sealed class DeepgramSpeechToText(DeepgramOptions options, HttpClient? httpClient = null, ILogger<DeepgramSpeechToText>? logger = null) : ISpeechToText
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly ILogger _logger = logger ?? NullLogger<DeepgramSpeechToText>.Instance;

    /// <inheritdoc/>
    public string Name => "Deepgram";

    /// <inheritdoc/>
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechRecognitionOptions? speechOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Token {options.ApiKey}");

        // The first chunk tells us the sample rate the call negotiated.
        await using var enumerator = audio.GetAsyncEnumerator(cts.Token);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            yield break;
        }

        var first = enumerator.Current;
        await socket.ConnectAsync(BuildUri(first.SampleRate, speechOptions), cts.Token).ConfigureAwait(false);
        _logger.LogDebug("Deepgram session started at {SampleRate} Hz", first.SampleRate);

        var sender = Task.Run(async () =>
        {
            try
            {
                await socket.SendAsync(first.Data, WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    await socket.SendAsync(enumerator.Current.Data, WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);
                }

                var close = Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}");
                await socket.SendAsync(close, WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // The call ended or the socket closed; the receive loop finishes on its own.
            }
        }, cts.Token);

        var buffer = new byte[16 * 1024];
        var message = new StringBuilder();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent && !cts.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = message.ToString();
            message.Clear();
            if (Parse(json) is { } segment)
            {
                yield return segment;
            }
        }

        await cts.CancelAsync().ConfigureAwait(false);
        await sender.ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? speechOptions = null,
        CancellationToken cancellationToken = default)
    {
        var uri = new UriBuilder(options.BatchUri)
        {
            Query = $"model={speechOptions?.Model ?? options.Model}&language={speechOptions?.Language ?? options.Language}" +
                    $"&punctuate={options.Punctuate.ToString().ToLowerInvariant()}&encoding=linear16&sample_rate={sampleRate}&channels=1",
        }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"Token {options.ApiKey}");
        request.Content = new ReadOnlyMemoryContent(pcm);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "audio/raw");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await response.EnsureSuccessAsync("Deepgram", cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement
            .GetProperty("results").GetProperty("channels")[0]
            .GetProperty("alternatives")[0]
            .GetProperty("transcript").GetString() ?? string.Empty;
    }

    private Uri BuildUri(int sampleRate, SpeechRecognitionOptions? speechOptions)
    {
        var query = new StringBuilder()
            .Append("encoding=linear16&channels=1&smart_format=true")
            .Append("&sample_rate=").Append(sampleRate)
            .Append("&model=").Append(Uri.EscapeDataString(speechOptions?.Model ?? options.Model))
            .Append("&language=").Append(Uri.EscapeDataString(speechOptions?.Language ?? options.Language))
            .Append("&punctuate=").Append(options.Punctuate.ToString().ToLowerInvariant())
            .Append("&interim_results=").Append((speechOptions?.InterimResults ?? true).ToString().ToLowerInvariant())
            .Append("&endpointing=").Append(speechOptions?.EndpointingMs ?? 500);
        if (options.Diarize)
        {
            query.Append("&diarize=true");
        }

        foreach (var hint in speechOptions?.Hints ?? [])
        {
            query.Append("&keyterm=").Append(Uri.EscapeDataString(hint));
        }

        return new UriBuilder(options.BaseUri) { Query = query.ToString() }.Uri;
    }

    private static TranscriptSegment? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() != "Results")
            {
                return null;
            }

            var alternative = root.GetProperty("channel").GetProperty("alternatives")[0];
            var text = alternative.GetProperty("transcript").GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var isFinal = root.TryGetProperty("is_final", out var f) && f.GetBoolean();
            var confidence = alternative.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0;
            var start = root.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
            var duration = root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
            return new TranscriptSegment(text, isFinal, confidence, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(duration));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
