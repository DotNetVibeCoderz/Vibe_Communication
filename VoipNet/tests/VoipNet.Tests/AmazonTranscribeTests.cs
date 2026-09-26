using System.Net;
using System.Net.WebSockets;
using System.Text;
using VoipNet.AI.Speech;
using Xunit;

namespace VoipNet.Tests;

/// <summary>
/// Amazon Transcribe streaming, against a stand-in that speaks the same framing as the service.
/// </summary>
/// <remarks>
/// There is no AWS account here, so the service itself is not what is being tested: the signing, the
/// event stream framing and the session are, which is everything this SDK is responsible for.
/// </remarks>
public sealed class AmazonTranscribeTests
{
    [Fact]
    public async Task AudioGoesUpAsEventsAndTranscriptsComeBack()
    {
        using var service = new FakeTranscribe();
        var recogniser = new AmazonTranscribeSpeechToText(new AmazonTranscribeOptions
        {
            Region = "ap-southeast-1",
            AccessKeyId = "AKIAEXAMPLE",
            SecretAccessKey = "secretexamplekey",
            Endpoint = service.Endpoint,
        });

        var segments = new List<TranscriptSegment>();
        await foreach (var segment in recogniser.TranscribeAsync(Audio()))
        {
            segments.Add(segment);
            if (segment.IsFinal)
            {
                break;
            }
        }

        Assert.Equal(2, segments.Count);
        Assert.Equal("selamat", segments[0].Text);
        Assert.False(segments[0].IsFinal);
        Assert.Equal("selamat pagi", segments[1].Text);
        Assert.True(segments[1].IsFinal);

        // What the service received: audio events with the samples inside them.
        var audio = await service.FirstAudioAsync();
        Assert.Equal("event", audio.Headers[":message-type"]);
        Assert.Equal("AudioEvent", audio.Headers[":event-type"]);
        Assert.Equal(640, audio.Payload.Length);

        // And the URL it was reached on carries the credentials, since a socket has no headers to.
        var query = System.Web.HttpUtility.ParseQueryString(service.RequestedQuery);
        Assert.Equal("AWS4-HMAC-SHA256", query["X-Amz-Algorithm"]);
        Assert.StartsWith("AKIAEXAMPLE/", query["X-Amz-Credential"], StringComparison.Ordinal);
        Assert.Equal("host", query["X-Amz-SignedHeaders"]);
        Assert.Equal("pcm", query["media-encoding"]);
        Assert.Equal("16000", query["sample-rate"]);
        Assert.Equal(64, query["X-Amz-Signature"]?.Length);
    }

    [Fact]
    public void TheSameRequestIsSignedTheSameWayEveryTime()
    {
        var options = new AmazonTranscribeOptions
        {
            Region = "us-east-1",
            AccessKeyId = "AKIAEXAMPLE",
            SecretAccessKey = "secretexamplekey",
            Language = "id-ID",
        };
        var recogniser = new AmazonTranscribeSpeechToText(options);
        var at = new DateTime(2026, 9, 26, 7, 0, 0, DateTimeKind.Utc);

        var first = recogniser.SignedUri(8000, null, at);
        var second = recogniser.SignedUri(8000, null, at);
        var later = recogniser.SignedUri(8000, null, at.AddSeconds(1));

        Assert.Equal(first, second);
        Assert.NotEqual(first, later);
        Assert.Equal("wss", first.Scheme);
        Assert.Contains("language-code=id-ID", first.Query, StringComparison.Ordinal);
        // The query a signature is computed over is sorted, so the parameters come back in that order.
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=", first.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageThatWasTamperedWithIsDropped()
    {
        var message = AwsEventStream.Encode(
            [(":message-type", "event"), (":event-type", "TranscriptEvent")],
            "{\"Transcript\":{}}"u8);

        Assert.Single(AwsEventStream.Decode(message));

        var damaged = message.ToArray();
        damaged[^6] ^= 0x40; // one bit of the payload
        Assert.Empty(AwsEventStream.Decode(damaged));
    }

    [Fact]
    public void MessagesInOneBufferAreReadOneAfterAnother()
    {
        var first = AwsEventStream.Encode([(":event-type", "TranscriptEvent")], "one"u8);
        var second = AwsEventStream.Encode([(":event-type", "TranscriptEvent")], "two"u8);
        var both = first.Concat(second).ToArray();

        var read = AwsEventStream.Decode(both);
        Assert.Equal(2, read.Count);
        Assert.Equal("one", Encoding.UTF8.GetString(read[0].Payload));
        Assert.Equal("two", Encoding.UTF8.GetString(read[1].Payload));
        Assert.Equal("TranscriptEvent", read[1].Headers[":event-type"]);

        // A truncated message is not half-read: it waits for the rest rather than being guessed at.
        Assert.Single(AwsEventStream.Decode(both.AsSpan(0, both.Length - 3).ToArray()));
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        for (var i = 0; i < 20; i++)
        {
            yield return new AudioChunk(new byte[640], 16000);
            await Task.Delay(10);
        }
    }

    /// <summary>A stand-in for the service: it accepts the socket and answers with two transcripts.</summary>
    private sealed class FakeTranscribe : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<(Dictionary<string, string> Headers, byte[] Payload)> _audio =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stopping = new();

        internal FakeTranscribe()
        {
            var port = Random.Shared.Next(31000, 44000);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/stream-transcription-websocket/");
            _listener.Start();
            Endpoint = new Uri($"ws://127.0.0.1:{port}/stream-transcription-websocket");
            _ = Task.Run(RunAsync);
        }

        internal Uri Endpoint { get; }

        internal string RequestedQuery { get; private set; } = string.Empty;

        internal Task<(Dictionary<string, string> Headers, byte[] Payload)> FirstAudioAsync() => _audio.Task;

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                RequestedQuery = context.Request.Url?.Query.TrimStart('?') ?? string.Empty;
                var socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;

                var buffer = new byte[16 * 1024];
                var result = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);
                var message = AwsEventStream.Decode(buffer.AsSpan(0, result.Count).ToArray()).First();
                _audio.TrySetResult((message.Headers, message.Payload));

                await SendAsync(socket, "selamat", partial: true).ConfigureAwait(false);
                await SendAsync(socket, "selamat pagi", partial: false).ConfigureAwait(false);

                // Then simply wait: the caller closes when it has what it came for.
                await Task.Delay(Timeout.Infinite, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or WebSocketException or ObjectDisposedException)
            {
                _audio.TrySetCanceled();
            }
        }

        private async Task SendAsync(WebSocket socket, string text, bool partial)
        {
            var partialFlag = partial ? "true" : "false";
            var json = "{\"Transcript\":{\"Results\":[{\"IsPartial\":" + partialFlag
                + ",\"Alternatives\":[{\"Transcript\":\"" + text + "\"}]}]}}";
            var message = AwsEventStream.Encode(
                [(":message-type", "event"), (":event-type", "TranscriptEvent"), (":content-type", "application/json")],
                Encoding.UTF8.GetBytes(json));
            await socket.SendAsync(message, WebSocketMessageType.Binary, true, _stopping.Token).ConfigureAwait(false);
        }
    }
}
