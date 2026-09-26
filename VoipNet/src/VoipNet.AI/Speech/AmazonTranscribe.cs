using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VoipNet.AI.Speech;

/// <summary>Settings for <see cref="AmazonTranscribeSpeechToText"/>.</summary>
public sealed class AmazonTranscribeOptions
{
    /// <summary>AWS region, for example <c>ap-southeast-1</c>.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Access key id.</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>Secret access key.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Session token, for temporary credentials.</summary>
    public string? SessionToken { get; set; }

    /// <summary>Language of the audio, as Transcribe names it.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>A custom vocabulary to apply, if one is configured in the account.</summary>
    public string? VocabularyName { get; set; }

    /// <summary>Mark each speaker in multi-party audio.</summary>
    public bool ShowSpeakerLabels { get; set; }

    /// <summary>
    /// The endpoint to open, for a private endpoint or a test double. The default is the streaming
    /// endpoint of <see cref="Region"/>.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>How long the signed URL stays valid. The session may run longer than this.</summary>
    public TimeSpan UrlLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Live transcription with Amazon Transcribe streaming.
/// </summary>
/// <remarks>
/// Transcribe's streaming API is a WebSocket whose URL is signed rather than a request that carries
/// credentials, and both directions speak AWS's event stream framing rather than plain JSON: each
/// message is a set of headers and a payload wrapped in two checksums. Audio goes up as
/// <c>AudioEvent</c> messages and transcripts come back as <c>TranscriptEvent</c> messages, with
/// partial results while somebody is speaking and a final one when they stop.
/// </remarks>
/// <param name="options">Provider settings.</param>
/// <param name="logger">Optional logger.</param>
public sealed class AmazonTranscribeSpeechToText(AmazonTranscribeOptions options, ILogger<AmazonTranscribeSpeechToText>? logger = null) : ISpeechToText
{
    private readonly ILogger _logger = logger ?? NullLogger<AmazonTranscribeSpeechToText>.Instance;

    /// <inheritdoc/>
    public string Name => "Amazon Transcribe";

    /// <inheritdoc/>
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechRecognitionOptions? speechOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = audio.GetAsyncEnumerator(cts.Token);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            yield break;
        }

        // The sample rate is part of the signed URL, so the first chunk decides it for the session.
        var first = enumerator.Current;
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(SignedUri(first.SampleRate, speechOptions), cts.Token).ConfigureAwait(false);
        _logger.LogDebug("Amazon Transcribe session started at {SampleRate} Hz", first.SampleRate);

        var sender = Task.Run(
            async () =>
            {
                try
                {
                    await SendAudioAsync(socket, first.Data, cts.Token).ConfigureAwait(false);
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        await SendAudioAsync(socket, enumerator.Current.Data, cts.Token).ConfigureAwait(false);
                    }

                    // An empty audio event is how the stream is ended politely.
                    await SendAudioAsync(socket, ReadOnlyMemory<byte>.Empty, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                {
                    // The call ended or the socket closed; the receive loop finishes on its own.
                }
            },
            cts.Token);

        var buffer = new byte[32 * 1024];
        var message = new List<byte>();
        try
        {
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

                message.AddRange(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var frame = message.ToArray();
                message.Clear();
                foreach (var segment in Read(frame))
                {
                    yield return segment;
                }
            }
        }
        finally
        {
            // Whoever is listening may stop at any moment, and the sending task shares the
            // enumerator with this loop: it has to be finished with before either is let go.
            await cts.CancelAsync().ConfigureAwait(false);
            await sender.ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Transcribe's batch API works on files in S3, which is a different service and a different
    /// account setup; a recording is transcribed here by putting it through the streaming session it
    /// would have gone through live, and joining what comes back.
    /// </remarks>
    public async Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? speechOptions = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var segment in TranscribeAsync(Chunks(pcm, sampleRate, cancellationToken), speechOptions, cancellationToken).ConfigureAwait(false))
        {
            if (segment.IsFinal && segment.Text is { Length: > 0 })
            {
                text.Append(text.Length > 0 ? " " : string.Empty).Append(segment.Text);
            }
        }

        return text.ToString();
    }

    /// <summary>Cuts a recording into the twenty-millisecond pieces a live call would have sent.</summary>
    private static async IAsyncEnumerable<AudioChunk> Chunks(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var step = Math.Max(sampleRate / 50 * 2, 2);
        for (var at = 0; at < pcm.Length && !cancellationToken.IsCancellationRequested; at += step)
        {
            yield return new AudioChunk(pcm.Slice(at, Math.Min(step, pcm.Length - at)), sampleRate);
            await Task.Yield();
        }
    }

    private static Task SendAudioAsync(ClientWebSocket socket, ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
    {
        var message = AwsEventStream.Encode(
            [
                (":message-type", "event"),
                (":event-type", "AudioEvent"),
                (":content-type", "application/octet-stream"),
            ],
            pcm.Span);
        return socket.SendAsync(message, WebSocketMessageType.Binary, true, cancellationToken);
    }

    /// <summary>Turns everything in one websocket frame into segments, ignoring what is not a transcript.</summary>
    private IEnumerable<TranscriptSegment> Read(byte[] frame)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var (headers, payload) in AwsEventStream.Decode(frame))
        {
            var kind = headers.GetValueOrDefault(":message-type");
            var eventType = headers.GetValueOrDefault(":event-type");
            if (kind == "exception" || headers.ContainsKey(":exception-type"))
            {
                var detail = headers.GetValueOrDefault(":exception-type") ?? "the service reported an error";
                throw new InvalidOperationException($"Amazon Transcribe: {detail} — {Encoding.UTF8.GetString(payload)}");
            }

            if (kind != "event" || eventType != "TranscriptEvent")
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("Transcript", out var transcript)
                || !transcript.TryGetProperty("Results", out var results))
            {
                continue;
            }

            foreach (var item in results.EnumerateArray())
            {
                var partial = item.TryGetProperty("IsPartial", out var isPartial) && isPartial.GetBoolean();
                var text = item.TryGetProperty("Alternatives", out var alternatives) && alternatives.GetArrayLength() > 0
                    ? alternatives[0].TryGetProperty("Transcript", out var value) ? value.GetString() ?? string.Empty : string.Empty
                    : string.Empty;
                if (text.Length > 0)
                {
                    segments.Add(new TranscriptSegment(text, !partial));
                }
            }
        }

        return segments;
    }

    /// <summary>
    /// The signed URL a session is opened on, which carries the credentials in its query because a
    /// socket has no headers to put them in. Worth a look when a connection is refused.
    /// </summary>
    /// <param name="sampleRate">Sample rate of the audio to send.</param>
    /// <param name="speechOptions">Recognition options, for the language.</param>
    /// <param name="now">The time to sign for; the default is now.</param>
    public Uri SignedUri(int sampleRate, SpeechRecognitionOptions? speechOptions = null, DateTime? now = null)
    {
        var endpoint = options.Endpoint ?? new Uri($"wss://transcribestreaming.{options.Region}.amazonaws.com:8443/stream-transcription-websocket");
        var query = new List<(string Key, string Value)>
        {
            ("language-code", speechOptions?.Language ?? options.Language),
            ("media-encoding", "pcm"),
            ("sample-rate", sampleRate.ToString(CultureInfo.InvariantCulture)),
        };
        if (options.VocabularyName is { Length: > 0 } vocabulary)
        {
            query.Add(("vocabulary-name", vocabulary));
        }

        if (options.ShowSpeakerLabels)
        {
            query.Add(("show-speaker-label", "true"));
        }

        return AwsSignatureV4.PresignWebSocket(
            endpoint,
            query,
            "transcribe",
            options.Region,
            options.AccessKeyId,
            options.SecretAccessKey,
            options.SessionToken,
            options.UrlLifetime,
            now ?? DateTime.UtcNow);
    }
}

/// <summary>
/// AWS event stream framing: a length, two checksums, a set of headers and a payload.
/// </summary>
/// <remarks>
/// This is how Amazon's streaming services carry both directions of a WebSocket, and it is public
/// here for the same reason <see cref="AwsSignatureV4"/> is: an application talking to another AWS
/// streaming API needs exactly this and nothing more.
/// </remarks>
/// <remarks>
/// Every message is <c>total length | headers length | prelude CRC | headers | payload | message
/// CRC</c>, all big-endian. Only string headers are written here, which is all Transcribe uses, and
/// a message whose checksums do not add up is dropped rather than guessed at.
/// </remarks>
public static class AwsEventStream
{
    private const byte StringType = 7;

    /// <summary>Wraps headers and a payload into one message.</summary>
    /// <param name="headers">Header names and their string values, in the order to write them.</param>
    /// <param name="payload">The message body.</param>
    public static byte[] Encode(IReadOnlyList<(string Name, string Value)> headers, ReadOnlySpan<byte> payload)
    {
        var head = new List<byte>();
        foreach (var (name, value) in headers)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var valueBytes = Encoding.UTF8.GetBytes(value);
            head.Add((byte)nameBytes.Length);
            head.AddRange(nameBytes);
            head.Add(StringType);
            head.Add((byte)(valueBytes.Length >> 8));
            head.Add((byte)valueBytes.Length);
            head.AddRange(valueBytes);
        }

        var total = 16 + head.Count + payload.Length;
        var message = new byte[total];
        BinaryPrimitives.WriteInt32BigEndian(message, total);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(4), head.Count);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(8), Crc32.Compute(message.AsSpan(0, 8)));
        head.CopyTo(message, 12);
        payload.CopyTo(message.AsSpan(12 + head.Count));
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(total - 4), Crc32.Compute(message.AsSpan(0, total - 4)));
        return message;
    }

    /// <summary>Reads every whole message in a buffer, skipping anything that does not add up.</summary>
    /// <param name="buffer">One or more messages, one after another.</param>
    public static List<(Dictionary<string, string> Headers, byte[] Payload)> Decode(ReadOnlySpan<byte> buffer)
    {
        var messages = new List<(Dictionary<string, string>, byte[])>();
        var at = 0;
        while (at + 16 <= buffer.Length)
        {
            var total = BinaryPrimitives.ReadInt32BigEndian(buffer[at..]);
            var headerLength = BinaryPrimitives.ReadInt32BigEndian(buffer[(at + 4)..]);
            if (total < 16 || headerLength < 0 || at + total > buffer.Length || headerLength > total - 16)
            {
                break;
            }

            var message = buffer.Slice(at, total);
            var preludeOk = BinaryPrimitives.ReadUInt32BigEndian(message[8..]) == Crc32.Compute(message[..8]);
            var messageOk = BinaryPrimitives.ReadUInt32BigEndian(message[(total - 4)..]) == Crc32.Compute(message[..(total - 4)]);
            at += total;
            if (!preludeOk || !messageOk)
            {
                continue;
            }

            messages.Add((ReadHeaders(message.Slice(12, headerLength)), message[(12 + headerLength)..(total - 4)].ToArray()));
        }

        return messages;
    }

    private static Dictionary<string, string> ReadHeaders(ReadOnlySpan<byte> headers)
    {
        var read = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;
        while (at + 1 <= headers.Length)
        {
            var nameLength = headers[at];
            at += 1;
            if (at + nameLength + 1 > headers.Length)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(headers.Slice(at, nameLength));
            at += nameLength;
            var type = headers[at];
            at += 1;
            if (type != StringType || at + 2 > headers.Length)
            {
                // Only strings are used by this service; anything else ends the parse rather than
                // guessing at a length that might not be there.
                break;
            }

            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(headers[at..]);
            at += 2;
            if (at + valueLength > headers.Length)
            {
                break;
            }

            read[name] = Encoding.UTF8.GetString(headers.Slice(at, valueLength));
            at += valueLength;
        }

        return read;
    }
}

/// <summary>The CRC-32 the event stream uses for its two checksums.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFF_FFFFu;
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (var i = 0u; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB8_8320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
