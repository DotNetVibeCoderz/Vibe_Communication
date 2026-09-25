using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VoipNet.Interop;

namespace VoipNet;

/// <summary>
/// A SIP user agent: registers with a PBX, places and receives calls, and carries audio.
/// </summary>
/// <example>
/// <code>
/// await using var client = new VoipClient(new VoipClientOptions { Domain = "pbx.local", Username = "1001", Password = "secret" });
/// await client.StartAsync();
/// var call = await client.CallAsync("sip:1002@pbx.local");
/// </code>
/// </example>
public sealed class VoipClient : IAsyncDisposable, IDisposable
{
    private readonly VoipClientOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, VoipCall> _calls = new();
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SipRequestResult>> _pendingRequests = new();

    // On fast paths (loopback, LAN) the answer can arrive before the caller has registered its
    // waiter, so results without a waiter are parked here for a moment.
    private readonly ConcurrentDictionary<ulong, SipRequestResult> _earlyResults = new();
    private readonly TaskCompletionSource<RegistrationEventArgs> _firstRegistration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SynchronizationContext? _syncContext;
    private GCHandle _self;
    private nint _handle;
    private int _disposed;

    /// <summary>Creates a client. Call <see cref="StartAsync"/> to bind the transport.</summary>
    /// <param name="options">Client configuration.</param>
    /// <param name="logger">Optional logger for engine diagnostics.</param>
    public VoipClient(VoipClientOptions options, ILogger<VoipClient>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<VoipClient>.Instance;
        _syncContext = options.EventSynchronizationContext;
    }

    /// <summary>Creates a client from dependency-injected options.</summary>
    /// <param name="options">Configured options.</param>
    /// <param name="logger">Logger.</param>
    public VoipClient(IOptions<VoipClientOptions> options, ILogger<VoipClient>? logger = null)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, logger)
    {
    }

    /// <summary>Version of the native engine.</summary>
    public static string EngineVersion
    {
        get
        {
            NativeMethods.EnsureLoaded();
            return Marshal.PtrToStringUTF8(NativeMethods.Version()) ?? "unknown";
        }
    }

    /// <summary>Local SIP address the client is bound to, once started.</summary>
    public string LocalAddress { get; private set; } = string.Empty;

    /// <summary>SHA-256 fingerprint (<c>AA:BB:…</c>) of the certificate presented on the TLS transport, once started.</summary>
    public string? TlsFingerprint { get; private set; }

    /// <summary>Current registration state.</summary>
    public RegistrationState RegistrationState { get; private set; } = RegistrationState.Unregistered;

    /// <summary>Calls known to the client, including ringing and recently ended ones.</summary>
    public IReadOnlyCollection<VoipCall> Calls => _calls.Values.ToArray();

    /// <summary>Raised when a call arrives. Answer or reject it from the handler.</summary>
    public event EventHandler<IncomingCallEventArgs>? IncomingCall;

    /// <summary>Raised whenever any call changes state.</summary>
    public event EventHandler<CallStateEventArgs>? CallStateChanged;

    /// <summary>Raised when media starts flowing on a call.</summary>
    public event EventHandler<MediaStartedEventArgs>? MediaStarted;

    /// <summary>Raised when registration with the registrar changes.</summary>
    public event EventHandler<RegistrationEventArgs>? RegistrationChanged;

    /// <summary>Raised for DTMF digits on any call.</summary>
    public event EventHandler<DtmfEventArgs>? DtmfReceived;

    /// <summary>Raised when a SIP MESSAGE arrives.</summary>
    public event EventHandler<SipMessageEventArgs>? MessageReceived;

    /// <summary>Raised when the remote party asks for a transfer.</summary>
    public event EventHandler<TransferRequestedEventArgs>? TransferRequested;

    /// <summary>Raised while a transfer this endpoint requested progresses.</summary>
    public event EventHandler<TransferProgressEventArgs>? TransferProgress;

    /// <summary>Raised for media notifications such as ICE connectivity or RTP timeouts.</summary>
    public event EventHandler<MediaEventArgs>? MediaNotification;

    /// <summary>Raised for every SIP message when <see cref="VoipClientOptions.TraceSip"/> is enabled.</summary>
    public event EventHandler<SipTraceEventArgs>? SipTrace;

    /// <summary>Starts the engine and binds the SIP transport.</summary>
    /// <param name="cancellationToken">Cancels waiting for the initial registration.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_handle != nint.Zero)
        {
            return;
        }

        CreateEndpoint();
        LocalAddress = NativeMethods.ConsumeString(NativeMethods.LocalAddress(_handle)) ?? string.Empty;
        TlsFingerprint = NativeMethods.ConsumeString(NativeMethods.TlsFingerprint(_handle));
        _logger.LogInformation("Voip.NET listening on {LocalAddress} (engine {Version})", LocalAddress, EngineVersion);

        if (_options.RegisterOnStart)
        {
            await WaitForRegistrationAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private unsafe void CreateEndpoint()
    {
        NativeMethods.EnsureLoaded();
        _self = GCHandle.Alloc(this, GCHandleType.Normal);
        var callbacks = new NativeMethods.Callbacks
        {
            OnEvent = &NativeCallbacks.OnEvent,
            OnAudio = &NativeCallbacks.OnAudio,
            OnDtmf = &NativeCallbacks.OnDtmf,
            OnEncoded = &NativeCallbacks.OnEncoded,
            OnVideo = &NativeCallbacks.OnVideo,
            OnData = &NativeCallbacks.OnData,
            UserData = GCHandle.ToIntPtr(_self),
        };

        var error = stackalloc byte[512];
        var code = NativeMethods.EndpointCreate(_options.ToJson(), callbacks, out var handle, error, 512);
        if (code != 0)
        {
            _self.Free();
            var message = Marshal.PtrToStringUTF8((nint)error) ?? $"engine error {code}";
            throw new VoipException($"Cannot start the VoIP endpoint: {message}") { ErrorCode = code };
        }

        _handle = handle;
    }

    /// <summary>Registers with the registrar and waits for the result.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<RegistrationEventArgs> RegisterAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        var completion = new TaskCompletionSource<RegistrationEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, RegistrationEventArgs args) => completion.TrySetResult(args);

        RegistrationChanged += Handler;
        try
        {
            Check(NativeMethods.Register(_handle), "register");
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RegistrationChanged -= Handler;
        }
    }

    /// <summary>Removes the registration binding.</summary>
    public void Unregister() => Check(NativeMethods.Unregister(_handle), "unregister");

    private async Task WaitForRegistrationAsync(CancellationToken cancellationToken)
    {
        var result = await _firstRegistration.Task.WaitAsync(TimeSpan.FromSeconds(32), cancellationToken).ConfigureAwait(false);
        if (result.State != RegistrationState.Registered)
        {
            throw new VoipException($"Registration failed: {result.StatusCode} {result.Reason}") { ErrorCode = result.StatusCode };
        }
    }

    /// <summary>Places a call and waits until the remote party answers.</summary>
    /// <param name="target">Destination: a SIP URI, <c>user@host</c>, or an extension resolved against the configured domain.</param>
    /// <param name="cancellationToken">Cancels the call attempt; the call is hung up if it is still ringing.</param>
    /// <returns>The connected call.</returns>
    public async Task<VoipCall> CallAsync(string target, CancellationToken cancellationToken = default)
    {
        var call = Call(target);
        try
        {
            await call.Connected.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            call.Hangup();
            throw;
        }

        return call;
    }

    /// <summary>Places a call and returns immediately, so the caller can follow ringing and early media.</summary>
    /// <param name="target">Destination URI or extension.</param>
    /// <returns>The call, in the <see cref="CallState.Calling"/> state.</returns>
    public VoipCall Call(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        EnsureStarted();
        Check(NativeMethods.MakeCall(_handle, target, out var id), "place call");
        // State events can beat this method to the dictionary, so keep whichever instance is there.
        var call = _calls.GetOrAdd(id, _ => new VoipCall(this, id, outgoing: true, target, null));
        if (string.IsNullOrEmpty(call.RemoteUri))
        {
            call.RemoteUri = target;
        }

        TrackStarted(call);
        return call;
    }

    /// <summary>Sends an OPTIONS ping, which is the usual way to check whether a peer is alive.</summary>
    /// <param name="target">Destination URI.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public Task<SipRequestResult> PingAsync(string target, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        Check(NativeMethods.SendOptions(_handle, target, out var requestId), "send OPTIONS");
        return AwaitRequestAsync(requestId, cancellationToken);
    }

    /// <summary>Sends a SIP MESSAGE (instant message).</summary>
    /// <param name="target">Destination URI.</param>
    /// <param name="body">Message text.</param>
    /// <param name="contentType">MIME type of the body.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public Task<SipRequestResult> SendMessageAsync(string target, string body, string contentType = "text/plain", CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        Check(NativeMethods.SendMessage(_handle, target, contentType, body, out var requestId), "send MESSAGE");
        return AwaitRequestAsync(requestId, cancellationToken);
    }

    private async Task<SipRequestResult> AwaitRequestAsync(ulong requestId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<SipRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = completion;
        if (_earlyResults.TryRemove(requestId, out var early))
        {
            completion.TrySetResult(early);
        }

        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(35), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>Creates a conference bridge that mixes the audio of the calls added to it.</summary>
    public VoipConference CreateConference()
    {
        EnsureStarted();
        Check(NativeMethods.ConferenceCreate(_handle, out var id), "create conference");
        return new VoipConference(this, id);
    }

    /// <summary>Looks up a call by its identifier.</summary>
    /// <param name="callId">The call identifier.</param>
    public VoipCall? FindCall(ulong callId) => _calls.GetValueOrDefault(callId);

    // ---- Operations used by VoipCall ----------------------------------------------------------

    internal void Answer(ulong id) => Check(NativeMethods.Answer(_handle, id), "answer");

    internal void Reject(ulong id, int code) => Check(NativeMethods.Reject(_handle, id, (ushort)code), "reject");

    internal void Hangup(ulong id) => Check(NativeMethods.Hangup(_handle, id), "hang up");

    internal void SetHold(ulong id, bool hold) => Check(NativeMethods.SetHold(_handle, id, hold ? 1 : 0), "hold");

    internal void SetMute(ulong id, bool mute) => Check(NativeMethods.SetMute(_handle, id, mute ? 1 : 0), "mute");

    /// <summary>
    /// Re-reads <see cref="VoipClientOptions.TlsCertificateFile"/>, its key and the CA file, and uses them
    /// for new connections. Calls already up keep the connection they have, so renewing a certificate
    /// never drops a call.
    /// </summary>
    public void ReloadTls() => Check(NativeMethods.ReloadTls(_handle), "reload TLS");

    internal void RestartIce(ulong id) => Check(NativeMethods.RestartIce(_handle, id), "restart ICE");

    internal void SendDtmf(ulong id, string digits, int durationMs) =>
        Check(NativeMethods.SendDtmf(_handle, id, digits, (uint)durationMs), "send DTMF");

    internal unsafe int SendAudio(ulong id, ReadOnlySpan<short> samples, int sampleRate)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        fixed (short* pcm = samples)
        {
            Check(NativeMethods.SendAudio(_handle, id, pcm, samples.Length, (uint)sampleRate, out var queued), "send audio");
            return (int)queued;
        }
    }

    internal void ClearAudio(ulong id) => Check(NativeMethods.ClearAudio(_handle, id), "clear audio");

    internal unsafe void SendEncoded(ulong id, int payloadType, uint timestamp, bool marker, ReadOnlySpan<byte> payload)
    {
        fixed (byte* data = payload)
        {
            Check(NativeMethods.SendEncoded(_handle, id, (byte)payloadType, timestamp, marker ? 1 : 0, data, payload.Length), "send encoded frame");
        }
    }

    internal void Transfer(ulong id, string target) => Check(NativeMethods.Transfer(_handle, id, target), "transfer");

    internal void TransferAttended(ulong id, ulong consultId) =>
        Check(NativeMethods.TransferAttended(_handle, id, consultId), "attended transfer");

    internal void ConferenceAdd(ulong conferenceId, ulong callId) =>
        Check(NativeMethods.ConferenceAdd(_handle, conferenceId, callId), "add to conference");

    internal void ConferenceLayout(ulong conferenceId, ulong pinnedCallId) =>
        Check(NativeMethods.ConferenceLayout(_handle, conferenceId, pinnedCallId), "set the conference layout");

    internal ulong ConferenceActiveSpeaker(ulong conferenceId)
    {
        Check(NativeMethods.ConferenceActiveSpeaker(_handle, conferenceId, out var callId), "read the active speaker");
        return callId;
    }

    internal void ConferenceRemove(ulong callId) => Check(NativeMethods.ConferenceRemove(_handle, callId), "leave conference");

    internal void ConferenceDestroy(ulong conferenceId) => Check(NativeMethods.ConferenceDestroy(_handle, conferenceId), "destroy conference");

    internal CallStatistics GetStatistics(ulong id)
    {
        Check(NativeMethods.CallStats(_handle, id, out var s), "read statistics");
        return new CallStatistics(
            (long)s.PacketsSent,
            (long)s.PacketsReceived,
            (long)s.BytesSent,
            (long)s.BytesReceived,
            (long)s.PacketsLost,
            (long)s.PacketsLate,
            s.JitterMs,
            (int)s.JitterBufferMs,
            (int)s.PayloadType,
            (int)s.SampleRate,
            s.Mos,
            s.SrtpActive != 0,
            s.IceConnected != 0,
            (int)s.OutboundQueuedMs,
            s.RemoteLossPercent,
            s.RemoteJitterMs,
            s.RoundTripMs,
            s.RemoteMos,
            (long)s.RemoteEstimateBps);
    }

    // ---- Native event plumbing ----------------------------------------------------------------

    internal unsafe void HandleAudio(ulong callId, int direction, int sampleRate, short* samples, int count)
    {
        if (_calls.TryGetValue(callId, out var call))
        {
            call.RaiseAudio((AudioDirection)direction, sampleRate, new ReadOnlySpan<short>(samples, count));
        }
    }

    internal unsafe void SendVideoFrame(ulong id, uint timestamp, ReadOnlySpan<byte> frame, string content)
    {
        fixed (byte* data = frame)
        {
            Check(NativeMethods.SendVideoFrame(_handle, id, timestamp, data, frame.Length, content), "send video frame");
        }
    }

    internal void OpenDataChannel(ulong id, string label) =>
        Check(NativeMethods.OpenDataChannel(_handle, id, label), "open the data channel");

    internal unsafe void SendDataMessage(ulong id, ushort stream, bool text, ReadOnlySpan<byte> data)
    {
        fixed (byte* bytes = data)
        {
            // An empty message is legal (RFC 8831 6.6), and a null pointer is not: point at the span.
            var pointer = bytes is null ? (byte*)1 : bytes;
            Check(NativeMethods.SendDataMessage(_handle, id, stream, text ? 1 : 0, pointer, data.Length), "send the data channel message");
        }
    }

    internal unsafe DataChannel[] DataChannels(ulong id)
    {
        var buffer = stackalloc byte[512];
        Check(NativeMethods.DataChannels(_handle, id, buffer, 512), "read the data channels");
        var channels = Marshal.PtrToStringUTF8((nint)buffer);
        if (string.IsNullOrEmpty(channels))
        {
            return [];
        }

        var open = new List<DataChannel>();
        foreach (var entry in channels.Split(','))
        {
            var split = entry.IndexOf(':');
            if (split > 0 && ushort.TryParse(entry[..split], out var stream))
            {
                open.Add(new DataChannel(stream, entry[(split + 1)..]));
            }
        }

        return [.. open];
    }

    internal DateTimeOffset? PresentationTime(ulong id, string stream, uint rtpTimestamp)
    {
        Check(NativeMethods.PresentationTime(_handle, id, stream, rtpTimestamp, out var ntp), "read the presentation time");
        if (ntp == 0)
        {
            return null;
        }

        return FromNtp(ntp);
    }

    /// <summary>NTP counts seconds since 1900 in the high half, fractions of a second in the low half.</summary>
    private static DateTimeOffset FromNtp(ulong ntp) =>
        DateTimeOffset.FromUnixTimeSeconds((long)(ntp >> 32) - 2_208_988_800L)
        + TimeSpan.FromSeconds((ntp & 0xFFFF_FFFF) / 4_294_967_296.0);

    internal DateTimeOffset? PlayoutTime(ulong id)
    {
        Check(NativeMethods.PlayoutTime(_handle, id, out var ntp), "read the playout time");
        return ntp == 0 ? null : FromNtp(ntp);
    }

    internal unsafe string? CallSas(ulong id)
    {
        var buffer = stackalloc byte[16];
        Check(NativeMethods.CallSas(_handle, id, buffer, 16), "read the authentication string");
        var sas = Marshal.PtrToStringUTF8((nint)buffer);
        return string.IsNullOrEmpty(sas) ? null : sas;
    }

    internal void ShareScreen(ulong id, bool on) =>
        Check(NativeMethods.ShareScreen(_handle, id, on ? 1 : 0), on ? "start the screen share" : "stop the screen share");

    internal void SetStreamDelay(ulong id, int delayMs) =>
        Check(NativeMethods.SetStreamDelay(_handle, id, (uint)Math.Clamp(delayMs, 0, 5000)), "report the audio delay");

    internal void RequestKeyframe(ulong id, bool full) =>
        Check(NativeMethods.RequestKeyframe(_handle, id, full ? 1 : 0), "request a keyframe");

    internal unsafe string[] VideoStreams(ulong id)
    {
        var buffer = stackalloc byte[128];
        Check(NativeMethods.VideoStreams(_handle, id, buffer, 128), "read the video streams");
        var streams = Marshal.PtrToStringUTF8((nint)buffer);
        return string.IsNullOrEmpty(streams) ? [] : streams.Split(',');
    }

    internal unsafe VideoLayer[] VideoLayers(ulong id)
    {
        var buffer = stackalloc byte[256];
        Check(NativeMethods.VideoLayers(_handle, id, buffer, 256), "read the video layers");
        var text = Marshal.PtrToStringUTF8((nint)buffer);
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var layers = new List<VideoLayer>();
        foreach (var entry in text.Split(','))
        {
            var parts = entry.Split(':');
            if (parts.Length == 3 && long.TryParse(parts[1], out var bitrate))
            {
                layers.Add(new VideoLayer(parts[0], bitrate, parts[2] == "1"));
            }
        }

        return [.. layers];
    }

    internal unsafe string? VideoCodec(ulong id)
    {
        var buffer = stackalloc byte[32];
        Check(NativeMethods.VideoCodec(_handle, id, buffer, 32), "read video codec");
        var codec = Marshal.PtrToStringUTF8((nint)buffer);
        return string.IsNullOrEmpty(codec) ? null : codec;
    }

    internal unsafe void HandleVideoFrame(ulong callId, uint timestamp, bool keyframe, byte* data, int length, string content)
    {
        if (_calls.TryGetValue(callId, out var call))
        {
            call.RaiseVideoFrame(timestamp, keyframe, new ReadOnlySpan<byte>(data, length), content);
        }
    }

    internal unsafe void HandleDataMessage(ulong callId, ushort stream, bool text, byte* data, int length)
    {
        if (_calls.TryGetValue(callId, out var call))
        {
            call.RaiseDataMessage(stream, text, new ReadOnlySpan<byte>(data, length));
        }
    }

    internal unsafe void HandleEncoded(ulong callId, byte payloadType, uint timestamp, bool marker, byte* data, int length)
    {
        if (_calls.TryGetValue(callId, out var call))
        {
            call.RaiseEncoded(payloadType, timestamp, marker, new ReadOnlySpan<byte>(data, length));
        }
    }

    internal void HandleDtmf(ulong callId, char digit, DtmfSource source)
    {
        if (!_calls.TryGetValue(callId, out var call))
        {
            return;
        }

        var args = new DtmfEventArgs(call, digit, source);
        Post(() =>
        {
            call.RaiseDtmf(args);
            DtmfReceived?.Invoke(this, args);
        });
    }

    internal void HandleEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "incomingCall":
                    OnIncomingCall(root);
                    break;
                case "callState":
                    OnCallState(root);
                    break;
                case "mediaStarted":
                    OnMediaStarted(root);
                    break;
                case "registrationChanged":
                    OnRegistration(root);
                    break;
                case "messageReceived":
                    RaiseMessage(root);
                    break;
                case "transferRequested":
                    OnTransferRequested(root);
                    break;
                case "transferProgress":
                    OnTransferProgress(root);
                    break;
                case "requestResult":
                    OnRequestResult(root);
                    break;
                case "mediaEvent":
                    OnMediaEvent(root);
                    break;
                case "sipTrace":
                    OnSipTrace(root);
                    break;
                case "log":
                    OnLog(root);
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Cannot interpret engine event: {Json}", json);
        }
    }

    private void OnIncomingCall(JsonElement root)
    {
        var id = root.GetProperty("callId").GetUInt64();
        var from = GetString(root, "from");
        var display = GetString(root, "fromDisplay");
        var call = _calls.GetOrAdd(id, _ => new VoipCall(this, id, outgoing: false, from, display));
        call.RemoteUri = from;
        call.RemoteDisplayName = display;
        TrackStarted(call);
        var replaces = root.TryGetProperty("replacesCallId", out var r) && r.ValueKind == JsonValueKind.Number
            ? FindCall(r.GetUInt64())
            : null;
        var args = new IncomingCallEventArgs(call, from, display, GetString(root, "to"), GetBool(root, "hasVideo"), replaces);
        Post(() => IncomingCall?.Invoke(this, args));
    }

    private void OnCallState(JsonElement root)
    {
        var id = root.GetProperty("callId").GetUInt64();
        var state = ParseState(GetString(root, "state"));
        var call = _calls.GetOrAdd(id, _ => new VoipCall(this, id, outgoing: true, string.Empty, null));
        call.State = state;
        call.LastStatusCode = GetInt(root, "code");
        if (state == CallState.Connected && call.ConnectedAt is null)
        {
            call.ConnectedAt = DateTimeOffset.UtcNow;
            Diagnostics.VoipMetrics.CallsAnswered.Add(1);
        }

        if (state == CallState.Terminated && call.EndedAt is null)
        {
            call.EndedAt = DateTimeOffset.UtcNow;
            TrackEnded(call);
        }

        var args = new CallStateEventArgs(call, state, call.LastStatusCode, GetString(root, "reason"));
        Post(() =>
        {
            call.RaiseStateChanged(args);
            CallStateChanged?.Invoke(this, args);
        });

        if (state == CallState.Terminated)
        {
            // Keep ended calls around briefly so applications can still read their details.
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(task => _calls.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    private void OnMediaStarted(JsonElement root)
    {
        var id = root.GetProperty("callId").GetUInt64();
        if (!_calls.TryGetValue(id, out var call))
        {
            return;
        }

        call.Codec = GetString(root, "codec");
        call.SampleRate = GetInt(root, "sampleRate");
        var args = new MediaStartedEventArgs(call, call.Codec, call.SampleRate, GetString(root, "remote"), GetBool(root, "srtp"));
        Post(() => MediaStarted?.Invoke(this, args));
    }

    private void OnRegistration(JsonElement root)
    {
        var state = GetString(root, "state") switch
        {
            "registered" => RegistrationState.Registered,
            "failed" => RegistrationState.Failed,
            _ => RegistrationState.Unregistered,
        };
        RegistrationState = state;
        Diagnostics.VoipMetrics.Registrations.Add(1, new KeyValuePair<string, object?>("state", state.ToString()));
        var args = new RegistrationEventArgs(state, GetInt(root, "code"), GetString(root, "reason"), GetInt(root, "expires"));
        _firstRegistration.TrySetResult(args);
        Post(() => RegistrationChanged?.Invoke(this, args));
    }

    private void RaiseMessage(JsonElement root)
    {
        var args = new SipMessageEventArgs(GetString(root, "from"), GetString(root, "contentType"), GetString(root, "body"));
        Post(() => MessageReceived?.Invoke(this, args));
    }

    private void OnTransferRequested(JsonElement root)
    {
        var id = root.GetProperty("callId").GetUInt64();
        if (!_calls.TryGetValue(id, out var call))
        {
            return;
        }

        var newCall = root.TryGetProperty("newCallId", out var n) && n.ValueKind == JsonValueKind.Number ? FindCall(n.GetUInt64()) : null;
        var args = new TransferRequestedEventArgs(call, GetString(root, "target"), newCall);
        Post(() => TransferRequested?.Invoke(this, args));
    }

    private void OnTransferProgress(JsonElement root)
    {
        var id = root.GetProperty("callId").GetUInt64();
        if (!_calls.TryGetValue(id, out var call))
        {
            return;
        }

        var args = new TransferProgressEventArgs(call, GetInt(root, "code"), GetString(root, "reason"));
        Post(() => TransferProgress?.Invoke(this, args));
    }

    private void OnRequestResult(JsonElement root)
    {
        var requestId = root.GetProperty("requestId").GetUInt64();
        var result = new SipRequestResult(
            GetInt(root, "code"),
            GetString(root, "reason"),
            TimeSpan.FromMilliseconds(GetInt(root, "latencyMs")),
            root.TryGetProperty("userAgent", out var ua) && ua.ValueKind == JsonValueKind.String ? ua.GetString() : null);
        if (_pendingRequests.TryGetValue(requestId, out var completion))
        {
            completion.TrySetResult(result);
            return;
        }

        _earlyResults[requestId] = result;
        _ = Task.Delay(TimeSpan.FromSeconds(40)).ContinueWith(task => _earlyResults.TryRemove(requestId, out _), TaskScheduler.Default);
    }

    private void OnMediaEvent(JsonElement root)
    {
        var id = root.GetProperty("callId").GetUInt64();
        if (!_calls.TryGetValue(id, out var call))
        {
            return;
        }

        var args = new MediaEventArgs(call, GetString(root, "kind"), GetString(root, "detail"));
        if (args.Kind == "keyframe-request")
        {
            // An application that encodes video needs this one directly, not by reading event names.
            var full = args.Detail == "fir";
            Post(() => call.RaiseKeyframeRequest(full));
        }

        Post(() => MediaNotification?.Invoke(this, args));
    }

    private void OnSipTrace(JsonElement root)
    {
        var args = new SipTraceEventArgs(GetString(root, "direction") == "out", GetString(root, "remote"), GetString(root, "message"));
        Post(() => SipTrace?.Invoke(this, args));
    }

    private void OnLog(JsonElement root)
    {
        var level = GetString(root, "level") switch
        {
            "error" => LogLevel.Error,
            "warn" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Information,
        };
        _logger.Log(level, "{Message}", GetString(root, "message"));
    }

    private static void TrackStarted(VoipCall call)
    {
        if (Interlocked.Exchange(ref call.MetricsStarted, 1) == 0)
        {
            var direction = new KeyValuePair<string, object?>("direction", call.IsOutgoing ? "outbound" : "inbound");
            Diagnostics.VoipMetrics.CallsStarted.Add(1, direction);
            Diagnostics.VoipMetrics.ActiveCalls.Add(1);
            // An outbound call starts on the caller's thread, so its span continues whatever placed it.
            call.Activity = Diagnostics.VoipTelemetry.StartCall(call);
        }
    }

    private void TrackEnded(VoipCall call)
    {
        if (Interlocked.Exchange(ref call.MetricsStarted, 2) != 1)
        {
            return;
        }

        Diagnostics.VoipMetrics.ActiveCalls.Add(-1);
        if (call.ConnectedAt is null)
        {
            Diagnostics.VoipMetrics.CallsFailed.Add(1, new KeyValuePair<string, object?>("code", call.LastStatusCode));
            Diagnostics.VoipTelemetry.EndCall(call, call.Activity);
            call.Activity = null;
            return;
        }

        Diagnostics.VoipMetrics.CallDuration.Record(call.Duration.TotalSeconds);
        try
        {
            var stats = GetStatistics(call.Id);
            call.FinalStatistics = stats;
            if (stats.Mos > 0)
            {
                Diagnostics.VoipMetrics.CallMos.Record(stats.Mos);
            }

            Diagnostics.VoipMetrics.PacketLoss.Record(stats.LossPercent);
        }
        catch (VoipException)
        {
            // The call had no media (for example it was rejected after answering).
        }

        Diagnostics.VoipTelemetry.EndCall(call, call.Activity);
        call.Activity = null;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static CallState ParseState(string state) => state switch
    {
        "calling" => CallState.Calling,
        "ringing" => CallState.Ringing,
        "early-media" => CallState.EarlyMedia,
        "incoming" => CallState.Incoming,
        "connected" => CallState.Connected,
        "on-hold" => CallState.OnHold,
        "remote-hold" => CallState.RemoteHold,
        _ => CallState.Terminated,
    };

    private void Post(Action action)
    {
        if (_syncContext is null)
        {
            action();
            return;
        }

        _syncContext.Post(static state => ((Action)state!)(), action);
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_handle == nint.Zero)
        {
            throw new InvalidOperationException($"Call {nameof(StartAsync)} before using the client.");
        }
    }

    private void Check(int code, string operation)
    {
        if (code == 0)
        {
            return;
        }

        // A null handle means the client was disposed while a handler still held a call.
        ObjectDisposedException.ThrowIf(_disposed != 0 || _handle == nint.Zero, this);

        var reason = code switch
        {
            -1 => "invalid argument",
            -2 => "invalid configuration",
            -3 => "network error",
            -4 => "call not found",
            -5 => "the call is not in a state that allows this",
            _ => $"engine error {code}",
        };
        throw new VoipException($"Cannot {operation}: {reason}.") { ErrorCode = code };
    }

    /// <summary>Stops the engine, ending active calls and removing the registration.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_handle != nint.Zero)
        {
            NativeMethods.EndpointDestroy(_handle);
            _handle = nint.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    /// <summary>Stops the engine without blocking the caller's thread.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed != 0)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(Task.Run(Dispose));
    }
}

/// <summary>A conference bridge that mixes the audio of several calls.</summary>
public sealed class VoipConference : IDisposable
{
    private readonly VoipClient _client;
    private readonly List<VoipCall> _participants = [];
    private bool _disposed;

    internal VoipConference(VoipClient client, ulong id)
    {
        _client = client;
        Id = id;
    }

    /// <summary>Engine-assigned conference identifier.</summary>
    public ulong Id { get; }

    /// <summary>Calls currently mixed into the conference.</summary>
    public IReadOnlyList<VoipCall> Participants
    {
        get
        {
            lock (_participants)
            {
                return _participants.ToArray();
            }
        }
    }

    /// <summary>Who the participants see. Video is forwarded, never mixed, so everyone watches one
    /// participant at a time: whoever is speaking, or the call pinned with <see cref="Pin"/>.</summary>
    public ConferenceLayout Layout { get; private set; } = ConferenceLayout.SpeakerFocus;

    /// <summary>The participant currently holding the floor, or <c>null</c> while the room is silent.</summary>
    public VoipCall? ActiveSpeaker
    {
        get
        {
            var id = _client.ConferenceActiveSpeaker(Id);
            return id == 0 ? null : Participants.FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>Everyone sees whoever is speaking.</summary>
    public void FollowSpeaker()
    {
        _client.ConferenceLayout(Id, 0);
        Layout = ConferenceLayout.SpeakerFocus;
    }

    /// <summary>Everyone sees this participant, whatever the room sounds like.</summary>
    /// <param name="call">The call to pin.</param>
    public void Pin(VoipCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _client.ConferenceLayout(Id, call.Id);
        Layout = ConferenceLayout.Pinned;
    }

    /// <summary>Adds a call. Each participant hears everyone except themselves.</summary>
    /// <param name="call">The call to add.</param>
    public void Add(VoipCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _client.ConferenceAdd(Id, call.Id);
        lock (_participants)
        {
            _participants.Add(call);
        }
    }

    /// <summary>Removes a call from the conference.</summary>
    /// <param name="call">The call to remove.</param>
    public void Remove(VoipCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _client.ConferenceRemove(call.Id);
        lock (_participants)
        {
            _participants.Remove(call);
        }
    }

    /// <summary>Removes every participant and releases the bridge.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var call in Participants)
        {
            try
            {
                _client.ConferenceRemove(call.Id);
            }
            catch (VoipException)
            {
                // The call may already be gone.
            }
        }

        _client.ConferenceDestroy(Id);
    }
}
