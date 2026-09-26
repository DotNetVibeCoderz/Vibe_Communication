using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.Audio;

namespace VoipNet.AI.Realtime;

/// <summary>Settings for a Gemini Live session.</summary>
public sealed class GeminiLiveOptions
{
    /// <summary>Google AI Studio API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Web socket endpoint of the Live API.</summary>
    public Uri BaseUri { get; set; } = new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");

    /// <summary>Model to talk to, with the <c>models/</c> prefix the API expects.</summary>
    public string Model { get; set; } = "models/gemini-2.0-flash-live-001";

    /// <summary>One of the prebuilt voices, for example <c>Puck</c>, <c>Charon</c> or <c>Kore</c>.</summary>
    public string Voice { get; set; } = "Puck";

    /// <summary>Instructions that define the agent's role and tone.</summary>
    public string Instructions { get; set; } = "You are a helpful phone agent. Answer briefly and naturally.";

    /// <summary>Spoken as soon as the call connects. Leave empty to wait for the caller.</summary>
    public string Greeting { get; set; } = string.Empty;

    /// <summary>The rate the API listens at. It takes 16 kHz mono PCM and nothing else.</summary>
    public int InputSampleRate { get; set; } = 16000;

    /// <summary>The rate the API speaks at; the engine resamples to the call's codec.</summary>
    public int OutputSampleRate { get; set; } = 24000;

    /// <summary>Ask for both sides of the conversation in writing as well as in sound.</summary>
    public bool Transcribe { get; set; } = true;

    /// <summary>
    /// Let the model decide when a turn has ended. Turning it off means the application marks the
    /// start and end of speech itself, which this agent does not do.
    /// </summary>
    public bool AutomaticTurnDetection { get; set; } = true;
}

/// <summary>
/// Connects a call to Gemini Live: audio goes up, audio comes back, and the model takes its own
/// turns.
/// </summary>
/// <remarks>
/// The same idea as <see cref="RealtimeVoiceAgent"/> with a different protocol underneath. Google's
/// Live API opens with a <c>setup</c> message and answers <c>setupComplete</c>; audio goes up as
/// base64 blobs inside <c>realtimeInput</c> and comes back inside <c>serverContent.modelTurn</c>. A
/// caller who interrupts is reported as <c>interrupted</c>, and whatever the agent still had queued
/// on the call is dropped so the two are not talking over each other.
///
/// This has been tested against a stand-in that speaks the protocol; there is no Google key here to
/// run it against the service itself.
/// </remarks>
/// <param name="options">Provider settings.</param>
/// <param name="logger">Optional logger.</param>
public sealed class GeminiLiveVoiceAgent(GeminiLiveOptions options, ILogger<GeminiLiveVoiceAgent>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<GeminiLiveVoiceAgent>.Instance;

    /// <summary>Raised with the caller's transcribed speech.</summary>
    public event EventHandler<string>? CallerSaid;

    /// <summary>Raised with the agent's transcribed speech.</summary>
    public event EventHandler<string>? AgentSaid;

    /// <summary>Raised with whatever the service says went wrong.</summary>
    public event EventHandler<string>? ErrorReceived;

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
        var uri = new UriBuilder(options.BaseUri) { Query = $"key={Uri.EscapeDataString(options.ApiKey)}" }.Uri;
        try
        {
            await socket.ConnectAsync(uri, lifetime.Token).ConfigureAwait(false);
            await SendAsync(socket, Setup(), lifetime.Token).ConfigureAwait(false);

            // Nothing may be sent until the session is up, so the greeting and the audio both wait.
            var uplink = Task.CompletedTask;
            await ReceiveAsync(call, socket, lifetime, () => uplink = PumpCallAudioAsync(call, socket, lifetime.Token)).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            await uplink.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The call ended.
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "The Gemini Live session failed");
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

    /// <summary>The opening message: model, voice, instructions and what to transcribe.</summary>
    private JsonObject Setup()
    {
        var setup = new JsonObject
        {
            ["model"] = options.Model,
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray("AUDIO"),
                ["speechConfig"] = new JsonObject
                {
                    ["voiceConfig"] = new JsonObject
                    {
                        ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = options.Voice },
                    },
                },
            },
            ["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = options.Instructions }),
            },
        };

        if (options.Transcribe)
        {
            // Both are empty objects: their presence is the request.
            setup["inputAudioTranscription"] = new JsonObject();
            setup["outputAudioTranscription"] = new JsonObject();
        }

        if (!options.AutomaticTurnDetection)
        {
            setup["realtimeInputConfig"] = new JsonObject
            {
                ["automaticActivityDetection"] = new JsonObject { ["disabled"] = true },
            };
        }

        return new JsonObject { ["setup"] = setup };
    }

    /// <summary>Sends the call's audio up, resampled to the rate the API listens at.</summary>
    private async Task PumpCallAudioAsync(VoipCall call, ClientWebSocket socket, CancellationToken cancellationToken)
    {
        AudioResampler? resampler = null;
        var buffer = new List<short>(4096);
        try
        {
            await foreach (var segment in call.ReadAudioAsync(AudioDirection.Inbound, cancellationToken).ConfigureAwait(false))
            {
                buffer.Clear();
                if (segment.SampleRate == options.InputSampleRate)
                {
                    buffer.AddRange(segment.Samples.Span);
                }
                else
                {
                    resampler ??= new AudioResampler(segment.SampleRate, options.InputSampleRate);
                    resampler.Process(segment.Samples.Span, buffer);
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                var pcm = Pcm.ToBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
                await SendAsync(
                    socket,
                    new JsonObject
                    {
                        ["realtimeInput"] = new JsonObject
                        {
                            ["audio"] = new JsonObject
                            {
                                ["mimeType"] = $"audio/pcm;rate={options.InputSampleRate}",
                                ["data"] = Convert.ToBase64String(pcm),
                            },
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            // The call ended: tell the model the audio is finished rather than dropping the socket.
            await SendAsync(socket, new JsonObject { ["realtimeInput"] = new JsonObject { ["audioStreamEnd"] = true } }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The call ended or the socket closed.
        }
    }

    private async Task ReceiveAsync(VoipCall call, ClientWebSocket socket, CancellationTokenSource lifetime, Action onReady)
    {
        var buffer = new byte[64 * 1024];
        var message = new List<byte>();
        var started = false;
        while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, lifetime.Token).ConfigureAwait(false);
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

            var json = Encoding.UTF8.GetString(message.ToArray());
            message.Clear();
            if (Handle(call, json) && !started)
            {
                started = true;
                if (!string.IsNullOrWhiteSpace(options.Greeting))
                {
                    await SendAsync(socket, Greeting(), lifetime.Token).ConfigureAwait(false);
                }

                onReady();
            }
        }
    }

    /// <summary>A text turn that asks for the opening line, spoken in the model's own voice.</summary>
    private JsonObject Greeting() => new()
    {
        ["clientContent"] = new JsonObject
        {
            ["turns"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = $"Say exactly: {options.Greeting}" }),
            }),
            ["turnComplete"] = true,
        },
    };

    /// <summary>Acts on one server message. Returns true when the session has just become usable.</summary>
    private bool Handle(VoipCall call, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("setupComplete", out _))
            {
                _logger.LogDebug("Gemini Live session ready with {Model}", options.Model);
                return true;
            }

            if (root.TryGetProperty("goAway", out var goAway))
            {
                _logger.LogInformation("Gemini Live is closing the session: {Detail}", goAway.ToString());
                return false;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var detail = error.ToString();
                _logger.LogError("Gemini Live error: {Error}", detail);
                ErrorReceived?.Invoke(this, detail);
                return false;
            }

            if (!root.TryGetProperty("serverContent", out var content))
            {
                return false;
            }

            if (content.TryGetProperty("interrupted", out var interrupted) && interrupted.GetBoolean())
            {
                // The caller spoke over the agent: drop whatever it still had queued on the call.
                call.ClearAudio();
            }

            if (content.TryGetProperty("inputTranscription", out var input)
                && input.TryGetProperty("text", out var said)
                && said.GetString() is { Length: > 0 } callerText)
            {
                CallerSaid?.Invoke(this, callerText.Trim());
            }

            if (content.TryGetProperty("outputTranscription", out var output)
                && output.TryGetProperty("text", out var spoken)
                && spoken.GetString() is { Length: > 0 } agentText)
            {
                AgentSaid?.Invoke(this, agentText.Trim());
            }

            if (content.TryGetProperty("modelTurn", out var turn) && turn.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (!part.TryGetProperty("inlineData", out var inline)
                        || !inline.TryGetProperty("data", out var data)
                        || data.GetString() is not { Length: > 0 } audio)
                    {
                        continue;
                    }

                    // The rate is in the mime type, since a model may answer at a rate of its own.
                    var rate = options.OutputSampleRate;
                    if (inline.TryGetProperty("mimeType", out var mime)
                        && mime.GetString() is { Length: > 0 } type
                        && type.Split("rate=") is { Length: > 1 } split
                        && int.TryParse(split[1].Split(';')[0], out var stated))
                    {
                        rate = stated;
                    }

                    if (!call.IsActive)
                    {
                        continue;
                    }

                    try
                    {
                        call.SendAudio(Convert.FromBase64String(audio), rate);
                    }
                    catch (VoipException) when (!call.IsActive)
                    {
                        // The call ended while audio was still arriving.
                    }
                }
            }
        }

        return false;
    }

    private static async Task SendAsync(ClientWebSocket socket, JsonNode payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
}
