using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoipNet.AI.Realtime;
using Xunit;

namespace VoipNet.Tests;

/// <summary>
/// A Gemini Live session against a stand-in that speaks the same protocol.
/// </summary>
/// <remarks>
/// There is no Google key here, so this is not a test of the model: it is a test of the session —
/// what is sent to open it, what the call's audio turns into on the way up, what the model's audio
/// turns into on the way back, and what happens when the caller interrupts.
/// </remarks>
public sealed class GeminiLiveTests
{
    [Fact]
    public async Task ACallIsWiredToTheModelBothWays()
    {
        using var service = new FakeGeminiLive();
        await using var pair = await LoopbackPair.ConnectAsync("caller", "agent");
        var agent = new GeminiLiveVoiceAgent(new GeminiLiveOptions
        {
            ApiKey = "test-key",
            BaseUri = service.Endpoint,
            Instructions = "Answer in Indonesian.",
            Greeting = "Halo, ada yang bisa dibantu?",
            Voice = "Kore",
        });

        var said = new List<string>();
        agent.CallerSaid += (_, text) => said.Add($"caller: {text}");
        agent.AgentSaid += (_, text) => said.Add($"agent: {text}");

        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = agent.RunAsync(pair.CalleeLeg, stopping.Token);

        // What opened the session: the model, the voice, the instructions and both transcriptions.
        var setup = await service.SetupAsync();
        var settings = setup.GetProperty("setup");
        Assert.Equal("models/gemini-2.0-flash-live-001", settings.GetProperty("model").GetString());
        Assert.Equal("Kore", settings.GetProperty("generationConfig").GetProperty("speechConfig")
            .GetProperty("voiceConfig").GetProperty("prebuiltVoiceConfig").GetProperty("voiceName").GetString());
        Assert.Equal("AUDIO", settings.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
        Assert.Equal("Answer in Indonesian.", settings.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.True(settings.TryGetProperty("inputAudioTranscription", out _));
        Assert.True(settings.TryGetProperty("outputAudioTranscription", out _));

        // The greeting goes as a text turn, since the model speaks it in its own voice.
        var greeting = await service.NextAsync(m => m.TryGetProperty("clientContent", out _));
        Assert.Contains(
            "Halo, ada yang bisa dibantu?",
            greeting.GetProperty("clientContent").GetProperty("turns")[0].GetProperty("parts")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);

        // The caller speaks, and it arrives as base64 PCM at the rate the API listens at.
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 1500), 16000);
        var audio = await service.NextAsync(m => m.TryGetProperty("realtimeInput", out var input) && input.TryGetProperty("audio", out _));
        var blob = audio.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", blob.GetProperty("mimeType").GetString());
        Assert.NotEmpty(Convert.FromBase64String(blob.GetProperty("data").GetString()!));

        // The model answers with sound and a transcript of both sides.
        await service.SendAudioAsync(TestHelpers.Tone(24000, 300), 24000);
        await service.SendTranscriptAsync(input: "halo", output: "Selamat pagi");
        await TestHelpers.WaitUntilAsync(() => said.Count >= 2, TimeSpan.FromSeconds(10), "both transcripts");
        Assert.Contains("caller: halo", said);
        Assert.Contains("agent: Selamat pagi", said);
        await TestHelpers.ReceivedAudioAsync(pair.CallerLeg, 200);

        // And when the caller talks over it, whatever was queued is dropped rather than played on.
        pair.CalleeLeg.SendAudio(TestHelpers.Tone(16000, 3000), 16000);
        await service.SendInterruptedAsync();
        await TestHelpers.WaitUntilAsync(() => pair.CalleeLeg.QueuedAudioMs < 500, TimeSpan.FromSeconds(10), "the queued answer to be dropped");

        await stopping.CancelAsync();
        await running;
    }

    /// <summary>A stand-in for the Live API: it answers the setup and says what it is told to say.</summary>
    private sealed class FakeGeminiLive : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<JsonElement> _setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<JsonElement> _received = [];
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _stopping = new();
        private WebSocket? _socket;

        internal FakeGeminiLive()
        {
            var port = Random.Shared.Next(31000, 44000);
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/live/");
            _listener.Start();
            Endpoint = new Uri($"ws://127.0.0.1:{port}/live");
            _ = Task.Run(RunAsync);
        }

        internal Uri Endpoint { get; }

        internal Task<JsonElement> SetupAsync() => _setup.Task;

        /// <summary>Waits for the first message the client sent that matches.</summary>
        internal async Task<JsonElement> NextAsync(Func<JsonElement, bool> matches)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    foreach (var message in _received)
                    {
                        if (matches(message))
                        {
                            return message;
                        }
                    }
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            throw new TimeoutException("the client never sent a message like that");
        }

        internal Task SendAudioAsync(short[] samples, int rate)
        {
            var pcm = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcm, 0, pcm.Length);
            var json = "{\"serverContent\":{\"modelTurn\":{\"parts\":[{\"inlineData\":{\"mimeType\":\"audio/pcm;rate="
                + rate + "\",\"data\":\"" + Convert.ToBase64String(pcm) + "\"}}]}}}";
            return SendAsync(json);
        }

        internal Task SendTranscriptAsync(string input, string output) =>
            SendAsync("{\"serverContent\":{\"inputTranscription\":{\"text\":\"" + input + "\"},\"outputTranscription\":{\"text\":\"" + output + "\"}}}");

        internal Task SendInterruptedAsync() => SendAsync("{\"serverContent\":{\"interrupted\":true}}");

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task SendAsync(string json)
        {
            if (_socket is { State: WebSocketState.Open } socket)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stopping.Token).ConfigureAwait(false);
            }
        }

        private async Task RunAsync()
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                var socket = (await context.AcceptWebSocketAsync(null).ConfigureAwait(false)).WebSocket;
                _socket = socket;

                var buffer = new byte[64 * 1024];
                while (socket.State == WebSocketState.Open && !_stopping.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, _stopping.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    var message = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
                    lock (_gate)
                    {
                        _received.Add(message);
                    }

                    if (message.TryGetProperty("setup", out _))
                    {
                        _setup.TrySetResult(message);
                        await SendAsync("{\"setupComplete\":{}}").ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or WebSocketException or ObjectDisposedException)
            {
                _setup.TrySetCanceled();
            }
        }
    }
}
