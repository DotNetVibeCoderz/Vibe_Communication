using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace VoipNet.AI.Speech;

/// <summary>Settings for a self-hosted ElBruno.Realtime speech server.</summary>
public sealed class ElBrunoRealtimeOptions
{
    /// <summary>Web socket endpoint that accepts raw PCM and returns transcript JSON.</summary>
    public Uri TranscriptionUri { get; set; } = new("ws://localhost:5006/stt");

    /// <summary>HTTP endpoint that turns text into PCM.</summary>
    public Uri SynthesisUri { get; set; } = new("http://localhost:5006/tts");

    /// <summary>Optional bearer token when the server is protected.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Voice name understood by the server.</summary>
    public string Voice { get; set; } = "default";

    /// <summary>Sample rate used in both directions.</summary>
    public int SampleRate { get; set; } = 16000;
}

/// <summary>
/// Speech to text against an ElBruno.Realtime style open-source server. The protocol is deliberately
/// small: binary frames carry 16-bit PCM upstream, and each downstream text frame is a JSON object
/// with <c>text</c> and <c>final</c> fields. See docs/en/ai-speech.md for a reference server.
/// </summary>
/// <param name="options">Server settings.</param>
public sealed class ElBrunoRealtimeSpeechToText(ElBrunoRealtimeOptions options) : ISpeechToText
{
    /// <inheritdoc/>
    public string Name => "ElBruno.Realtime";

    /// <inheritdoc/>
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechRecognitionOptions? speechOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var socket = new ClientWebSocket();
        if (options.ApiKey is { Length: > 0 } key)
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        }

        var uri = new UriBuilder(options.TranscriptionUri)
        {
            Query = $"sample_rate={options.SampleRate}&language={speechOptions?.Language ?? "auto"}",
        }.Uri;
        await socket.ConnectAsync(uri, cts.Token).ConfigureAwait(false);

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in audio.WithCancellation(cts.Token).ConfigureAwait(false))
                {
                    await socket.SendAsync(chunk.Data, WebSocketMessageType.Binary, true, cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // The call ended; the receive loop stops as the socket closes.
            }
        }, cts.Token);

        var buffer = new byte[8192];
        var message = new StringBuilder();
        while (socket.State == WebSocketState.Open && !cts.IsCancellationRequested)
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
            TranscriptSegment? segment = null;
            try
            {
                using var document = JsonDocument.Parse(json);
                var text = document.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var final = document.RootElement.TryGetProperty("final", out var f) && f.ValueKind == JsonValueKind.True;
                    segment = new TranscriptSegment(text, final);
                }
            }
            catch (JsonException)
            {
                segment = null;
            }

            if (segment is not null)
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
        var text = new StringBuilder();
        await foreach (var segment in TranscribeAsync(One(pcm, sampleRate), speechOptions, cancellationToken).ConfigureAwait(false))
        {
            if (segment.IsFinal)
            {
                text.Append(segment.Text).Append(' ');
            }
        }

        return text.ToString().Trim();

        static async IAsyncEnumerable<AudioChunk> One(ReadOnlyMemory<byte> data, int rate)
        {
            yield return new AudioChunk(data, rate);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}

/// <summary>Speech synthesis against an ElBruno.Realtime style server that returns raw PCM.</summary>
/// <param name="options">Server settings.</param>
/// <param name="httpClient">HTTP client to use.</param>
public sealed class ElBrunoRealtimeTextToSpeech(ElBrunoRealtimeOptions options, HttpClient? httpClient = null) : ITextToSpeech
{
    private readonly HttpClient _http = httpClient ?? new HttpClient();

    /// <inheritdoc/>
    public string Name => "ElBruno.Realtime";

    /// <inheritdoc/>
    public int PreferredSampleRate => options.SampleRate;

    /// <inheritdoc/>
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? ttsOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var rate = ttsOptions?.SampleRate ?? options.SampleRate;
        var payload = JsonSerializer.Serialize(new
        {
            text,
            voice = ttsOptions?.Voice ?? options.Voice,
            sample_rate = rate,
            speed = ttsOptions?.Speed ?? 1.0,
            format = "pcm_s16le",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, options.SynthesisUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (options.ApiKey is { Length: > 0 } key)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        }

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
}
