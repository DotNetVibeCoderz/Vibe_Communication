using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.Audio;

namespace VoipNet.AI.Realtime;

/// <summary>Settings for the OpenAI realtime voice API.</summary>
public sealed class RealtimeVoiceOptions
{
    /// <summary>API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Web socket endpoint.</summary>
    public Uri BaseUri { get; set; } = new("wss://api.openai.com/v1/realtime");

    /// <summary>Realtime model name.</summary>
    public string Model { get; set; } = "gpt-realtime";

    /// <summary>Voice used for the spoken answers.</summary>
    public string Voice { get; set; } = "alloy";

    /// <summary>Instructions that define the agent's role and tone.</summary>
    public string Instructions { get; set; } = "You are a helpful phone agent. Answer briefly and naturally.";

    /// <summary>Spoken as soon as the call connects. Leave empty to wait for the caller.</summary>
    public string Greeting { get; set; } = string.Empty;

    /// <summary>The API speaks and listens in 24 kHz PCM; the engine resamples to the call's codec.</summary>
    public int SampleRate { get; set; } = 24000;

    /// <summary>Let the server detect turns and interrupt the agent when the caller speaks.</summary>
    public bool ServerTurnDetection { get; set; } = true;
}

/// <summary>
/// Connects a call directly to a realtime speech-to-speech model: audio goes up, audio comes back,
/// and the model handles turn taking itself. Latency is far lower than a
/// speech-to-text, model, text-to-speech chain, at the cost of provider choice.
/// </summary>
/// <param name="options">Provider settings.</param>
/// <param name="logger">Optional logger.</param>
public sealed class RealtimeVoiceAgent(RealtimeVoiceOptions options, ILogger<RealtimeVoiceAgent>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<RealtimeVoiceAgent>.Instance;

    /// <summary>Raised with the caller's transcribed speech.</summary>
    public event EventHandler<string>? CallerSaid;

    /// <summary>Raised with the agent's transcribed speech.</summary>
    public event EventHandler<string>? AgentSaid;

    /// <summary>Runs the conversation until the call ends or the token is cancelled.</summary>
    /// <param name="call">A connected call.</param>
    /// <param name="cancellationToken">Stops the agent.</param>
    public async Task RunAsync(VoipCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnState(object? sender, CallStateEventArgs e)
        {
            if (e.State == CallState.Terminated)
            {
                lifetime.Cancel();
            }
        }

        call.StateChanged += OnState;
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {options.ApiKey}");
        socket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        var uri = new UriBuilder(options.BaseUri) { Query = $"model={Uri.EscapeDataString(options.Model)}" }.Uri;
        try
        {
            await socket.ConnectAsync(uri, lifetime.Token).ConfigureAwait(false);
            await ConfigureSessionAsync(socket, lifetime.Token).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.Greeting))
            {
                await SendAsync(socket, new JsonObject
                {
                    ["type"] = "response.create",
                    ["response"] = new JsonObject { ["instructions"] = $"Say exactly: {options.Greeting}" },
                }, lifetime.Token).ConfigureAwait(false);
            }

            var uplink = PumpCallAudioAsync(call, socket, lifetime.Token);
            await ReceiveAsync(call, socket, lifetime.Token).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            await uplink.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call ended.
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "The realtime session failed");
        }
        finally
        {
            call.StateChanged -= OnState;
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    // Closing is best effort.
                }
            }
        }
    }

    private async Task ConfigureSessionAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var session = new JsonObject
        {
            ["type"] = "session.update",
            ["session"] = new JsonObject
            {
                ["modalities"] = new JsonArray("audio", "text"),
                ["instructions"] = options.Instructions,
                ["voice"] = options.Voice,
                ["input_audio_format"] = "pcm16",
                ["output_audio_format"] = "pcm16",
                ["input_audio_transcription"] = new JsonObject { ["model"] = "whisper-1" },
                ["turn_detection"] = options.ServerTurnDetection
                    ? new JsonObject { ["type"] = "server_vad", ["threshold"] = 0.5, ["silence_duration_ms"] = 500 }
                    : null,
            },
        };
        await SendAsync(socket, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpCallAudioAsync(VoipCall call, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        AudioResampler? resampler = null;
        var buffer = new List<short>(4096);
        try
        {
            await foreach (var segment in call.ReadAudioAsync(AudioDirection.Inbound, cancellationToken).ConfigureAwait(false))
            {
                buffer.Clear();
                if (segment.SampleRate == options.SampleRate)
                {
                    buffer.AddRange(segment.Samples.Span);
                }
                else
                {
                    resampler ??= new AudioResampler(segment.SampleRate, options.SampleRate);
                    resampler.Process(segment.Samples.Span, buffer);
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                var pcm = Pcm.ToBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
                await SendAsync(socket, new JsonObject
                {
                    ["type"] = "input_audio_buffer.append",
                    ["audio"] = Convert.ToBase64String(pcm),
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The call ended or the socket closed.
        }
    }

    private async Task ReceiveAsync(VoipCall call, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        var message = new StringBuilder();
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
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
            Handle(call, json);
        }
    }

    private void Handle(VoipCall call, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "response.audio.delta" or "response.output_audio.delta":
                    if (root.TryGetProperty("delta", out var delta) && delta.GetString() is { Length: > 0 } audio)
                    {
                        call.SendAudio(Convert.FromBase64String(audio), options.SampleRate);
                    }

                    break;

                case "input_audio_buffer.speech_started":
                    // The caller interrupted: drop whatever the agent still had queued.
                    call.ClearAudio();
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    if (root.TryGetProperty("transcript", out var callerText) && callerText.GetString() is { Length: > 0 } said)
                    {
                        CallerSaid?.Invoke(this, said.Trim());
                    }

                    break;

                case "response.audio_transcript.done" or "response.output_audio_transcript.done":
                    if (root.TryGetProperty("transcript", out var agentText) && agentText.GetString() is { Length: > 0 } spoken)
                    {
                        AgentSaid?.Invoke(this, spoken.Trim());
                    }

                    break;

                case "error":
                    _logger.LogError("Realtime API error: {Error}", root.TryGetProperty("error", out var error) ? error.ToString() : json);
                    break;
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonNode payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
}
