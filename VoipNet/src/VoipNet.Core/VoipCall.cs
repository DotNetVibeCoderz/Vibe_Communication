using System.Threading.Channels;

namespace VoipNet;

/// <summary>A block of PCM audio delivered by a call.</summary>
/// <param name="Samples">16-bit mono PCM.</param>
/// <param name="SampleRate">Sample rate in hertz.</param>
/// <param name="Direction">Whether the audio was received or transmitted.</param>
/// <param name="Timestamp">When the frame was produced.</param>
public readonly record struct AudioSegment(ReadOnlyMemory<short> Samples, int SampleRate, AudioDirection Direction, DateTimeOffset Timestamp)
{
    /// <summary>Duration of the segment.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)Math.Max(SampleRate, 1));
}

/// <summary>One SIP call: its state, media and control operations.</summary>
public sealed class VoipCall
{
    private readonly VoipClient _client;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<CallStateEventArgs> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _readerLock = new();
    private Channel<AudioSegment>? _inboundAudio;
    private Channel<AudioSegment>? _outboundAudio;

    internal VoipCall(VoipClient client, ulong id, bool outgoing, string remoteUri, string? remoteDisplayName)
    {
        _client = client;
        Id = id;
        IsOutgoing = outgoing;
        RemoteUri = remoteUri;
        RemoteDisplayName = remoteDisplayName;
        State = outgoing ? CallState.Calling : CallState.Incoming;

        // Nobody has to await Connected, so make sure a failure there is never an unobserved exception.
        _ = _connected.Task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <summary>Engine-assigned identifier, unique for the lifetime of the client.</summary>
    public ulong Id { get; }

    /// <summary>True when this endpoint placed the call.</summary>
    public bool IsOutgoing { get; }

    /// <summary>Remote address of record, for example <c>sip:bob@example.com</c>.</summary>
    public string RemoteUri { get; internal set; }

    /// <summary>Remote display name, when the peer sent one.</summary>
    public string? RemoteDisplayName { get; internal set; }

    /// <summary>Current state.</summary>
    public CallState State { get; internal set; }

    /// <summary>SIP status code of the last state change.</summary>
    public int LastStatusCode { get; internal set; }

    /// <summary>Negotiated codec, once media has started.</summary>
    public string? Codec { get; internal set; }

    /// <summary>Audio sample rate of the negotiated codec.</summary>
    public int SampleRate { get; internal set; } = 8000;

    /// <summary>When the call was answered.</summary>
    public DateTimeOffset? ConnectedAt { get; internal set; }

    /// <summary>When the call ended.</summary>
    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>How long the call has been (or was) connected.</summary>
    public TimeSpan Duration => ConnectedAt is null ? TimeSpan.Zero : (EndedAt ?? DateTimeOffset.UtcNow) - ConnectedAt.Value;

    /// <summary>Whether this side has the call on hold.</summary>
    public bool IsOnHold => State == CallState.OnHold;

    /// <summary>Whether the microphone is muted.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>True while the call can carry media.</summary>
    public bool IsActive => State is CallState.Connected or CallState.OnHold or CallState.RemoteHold or CallState.EarlyMedia;

    /// <summary>Media statistics captured when the call ended.</summary>
    public CallStatistics? FinalStatistics { get; internal set; }

    /// <summary>0 = not counted, 1 = counted as started, 2 = counted as ended.</summary>
    internal int MetricsStarted;

    /// <summary>Arbitrary per-call state for applications (queue entry, agent, AI session…).</summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised for every 20 ms audio frame, without copying.</summary>
    public event AudioFrameHandler? AudioReceived;

    /// <summary>Raised for payloads of codecs the engine does not decode (video, G.729, Opus).</summary>
    public event EncodedFrameHandler? EncodedReceived;

    /// <summary>Raised for every complete video frame received on the call's video stream.</summary>
    public event VideoFrameHandler? VideoFrameReceived;

    /// <summary>Raised when the call changes state.</summary>
    public event EventHandler<CallStateEventArgs>? StateChanged;

    /// <summary>Raised when a DTMF digit arrives.</summary>
    public event EventHandler<DtmfEventArgs>? DtmfReceived;

    /// <summary>Completes when the call is answered; faults when it fails before connecting.</summary>
    public Task Connected => _connected.Task;

    /// <summary>Completes when the call ends, with the final state change.</summary>
    public Task<CallStateEventArgs> Completion => _ended.Task;

    /// <summary>Answers an incoming call.</summary>
    /// <param name="cancellationToken">Cancels waiting for the call to be established.</param>
    public async Task AnswerAsync(CancellationToken cancellationToken = default)
    {
        _client.Answer(Id);
        await Connected.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects an incoming call.</summary>
    /// <param name="statusCode">SIP status code, for example 486 (Busy Here) or 603 (Decline).</param>
    public void Reject(int statusCode = 486) => _client.Reject(Id, statusCode);

    /// <summary>Ends the call: CANCEL while ringing, BYE once connected.</summary>
    public void Hangup() => _client.Hangup(Id);

    /// <summary>Ends the call and waits until it is fully terminated.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task HangupAsync(CancellationToken cancellationToken = default)
    {
        _client.Hangup(Id);
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts the call on hold or resumes it with a re-INVITE.</summary>
    /// <param name="hold">True to hold, false to resume.</param>
    public void SetHold(bool hold) => _client.SetHold(Id, hold);

    /// <summary>
    /// Restarts ICE with fresh credentials, for example after the network changed. Media keeps flowing on the
    /// current path until a new candidate pair is selected; <see cref="VoipClient.MediaNotification"/> reports
    /// <c>ice-connected</c> again. Only for calls that negotiated ICE.
    /// </summary>
    public void RestartIce() => _client.RestartIce(Id);

    /// <summary>Mutes or unmutes the transmitted audio. Muting keeps the RTP stream alive.</summary>
    /// <param name="mute">True to mute.</param>
    public void SetMute(bool mute)
    {
        _client.SetMute(Id, mute);
        IsMuted = mute;
    }

    /// <summary>Sends DTMF digits using the configured mode.</summary>
    /// <param name="digits">Digits to send (0-9, *, #, A-D).</param>
    /// <param name="durationMs">Duration of each digit in milliseconds.</param>
    public void SendDtmf(string digits, int durationMs = 120) => _client.SendDtmf(Id, digits, durationMs);

    /// <summary>Queues PCM audio for paced transmission, resampling when needed.</summary>
    /// <param name="samples">16-bit mono PCM.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="samples"/>.</param>
    /// <returns>Total audio queued for transmission, in milliseconds.</returns>
    public int SendAudio(ReadOnlySpan<short> samples, int sampleRate) => _client.SendAudio(Id, samples, sampleRate);

    /// <summary>Queues little-endian 16-bit PCM bytes for transmission.</summary>
    /// <param name="pcmBytes">Raw PCM bytes.</param>
    /// <param name="sampleRate">Sample rate of the audio.</param>
    /// <returns>Total audio queued for transmission, in milliseconds.</returns>
    public int SendAudio(ReadOnlySpan<byte> pcmBytes, int sampleRate) =>
        SendAudio(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcmBytes), sampleRate);

    /// <summary>
    /// Streams audio (for example from a text-to-speech service) into the call, pausing so the
    /// engine never queues more than <paramref name="maxQueuedMs"/> of audio.
    /// </summary>
    /// <param name="chunks">Chunks of little-endian 16-bit PCM.</param>
    /// <param name="sampleRate">Sample rate of the chunks.</param>
    /// <param name="maxQueuedMs">Back-pressure threshold in milliseconds.</param>
    /// <param name="cancellationToken">Stops streaming; already queued audio still plays.</param>
    public async Task SendAudioStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        int sampleRate,
        int maxQueuedMs = 2000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (!IsActive)
            {
                break;
            }

            try
            {
                var queued = SendAudio(chunk.Span, sampleRate);
                while (queued > maxQueuedMs && IsActive && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(Math.Min(queued - maxQueuedMs, 200), cancellationToken).ConfigureAwait(false);
                    queued = QueuedAudioMs;
                }
            }
            catch (VoipException) when (!IsActive)
            {
                // The call ended between the check and the send; nothing more to play.
                break;
            }
        }
    }

    /// <summary>Drops audio queued for transmission — use this to let a caller barge in on a prompt.</summary>
    public void ClearAudio() => _client.ClearAudio(Id);

    /// <summary>Audio still queued for transmission, in milliseconds.</summary>
    public int QueuedAudioMs => GetStatistics().OutboundQueuedMs;

    /// <summary>Sends one encoded video frame on the call's video stream (H.264 Annex B access unit or VP8 frame).</summary>
    /// <param name="timestamp">Presentation timestamp in the 90 kHz video clock.</param>
    /// <param name="frame">The encoded frame; it is split across as many RTP packets as it needs.</param>
    public void SendVideoFrame(uint timestamp, ReadOnlySpan<byte> frame) => _client.SendVideoFrame(Id, timestamp, frame);

    /// <summary>Asks the peer for a keyframe. The engine already does this when a frame arrives with packets
    /// missing; call it yourself when a decoder loses its state. Requests arrive as a
    /// <c>keyframe-request</c> media notification.</summary>
    /// <param name="full">Send a Full Intra Request (RFC 5104) instead of a Picture Loss Indication (RFC 4585).</param>
    public void RequestKeyframe(bool full = false) => _client.RequestKeyframe(Id, full);

    /// <summary>The video codec negotiated for this call, or <c>null</c> when the call has no video stream.</summary>
    public string? VideoCodec => _client.VideoCodec(Id);

    /// <summary>Sends an already encoded payload for a pass-through codec.</summary>
    /// <param name="payloadType">RTP payload type.</param>
    /// <param name="timestamp">RTP timestamp.</param>
    /// <param name="marker">RTP marker bit.</param>
    /// <param name="payload">Encoded payload.</param>
    public void SendEncoded(int payloadType, uint timestamp, bool marker, ReadOnlySpan<byte> payload) =>
        _client.SendEncoded(Id, payloadType, timestamp, marker, payload);

    /// <summary>Reads current media statistics.</summary>
    public CallStatistics GetStatistics() => _client.GetStatistics(Id);

    /// <summary>Transfers the remote party to another destination (blind transfer, SIP REFER).</summary>
    /// <param name="target">Transfer target, for example <c>sip:support@example.com</c>.</param>
    public void Transfer(string target) => _client.Transfer(Id, target);

    /// <summary>Transfers this call to the party of a consultation call (attended transfer).</summary>
    /// <param name="consultationCall">The consultation call that will replace this one.</param>
    public void TransferTo(VoipCall consultationCall)
    {
        ArgumentNullException.ThrowIfNull(consultationCall);
        _client.TransferAttended(Id, consultationCall.Id);
    }

    /// <summary>Adds the call to a conference bridge.</summary>
    /// <param name="conference">The conference to join.</param>
    public void JoinConference(VoipConference conference)
    {
        ArgumentNullException.ThrowIfNull(conference);
        conference.Add(this);
    }

    /// <summary>Removes the call from any conference it is part of.</summary>
    public void LeaveConference() => _client.ConferenceRemove(Id);

    /// <summary>
    /// Streams received (or transmitted) audio as an async sequence, which suits
    /// speech-to-text pipelines. Frames are dropped if the consumer falls far behind.
    /// </summary>
    /// <param name="direction">Which side of the conversation to read.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    public IAsyncEnumerable<AudioSegment> ReadAudioAsync(AudioDirection direction = AudioDirection.Inbound, CancellationToken cancellationToken = default)
    {
        var channel = GetOrCreateChannel(direction);
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    private Channel<AudioSegment> GetOrCreateChannel(AudioDirection direction)
    {
        lock (_readerLock)
        {
            ref var slot = ref direction == AudioDirection.Inbound ? ref _inboundAudio : ref _outboundAudio;
            return slot ??= Channel.CreateBounded<AudioSegment>(new BoundedChannelOptions(500)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
            });
        }
    }

    internal void RaiseAudio(AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples)
    {
        SampleRate = sampleRate;
        AudioReceived?.Invoke(this, direction, sampleRate, samples);

        var channel = direction == AudioDirection.Inbound ? _inboundAudio : _outboundAudio;
        if (channel is not null)
        {
            channel.Writer.TryWrite(new AudioSegment(samples.ToArray(), sampleRate, direction, DateTimeOffset.UtcNow));
        }
    }

    internal void RaiseVideoFrame(uint timestamp, bool keyframe, ReadOnlySpan<byte> frame) =>
        VideoFrameReceived?.Invoke(this, timestamp, keyframe, frame);

    internal void RaiseEncoded(int payloadType, uint timestamp, bool marker, ReadOnlySpan<byte> payload) =>
        EncodedReceived?.Invoke(this, payloadType, timestamp, marker, payload);

    internal void RaiseDtmf(DtmfEventArgs args) => DtmfReceived?.Invoke(this, args);

    internal void RaiseStateChanged(CallStateEventArgs args)
    {
        StateChanged?.Invoke(this, args);
        switch (args.State)
        {
            case CallState.Connected:
                _connected.TrySetResult();
                break;
            case CallState.Terminated:
                _connected.TrySetException(new VoipException($"Call ended before it was answered: {args.StatusCode} {args.Reason}")
                {
                    ErrorCode = args.StatusCode,
                });
                _ended.TrySetResult(args);
                _inboundAudio?.Writer.TryComplete();
                _outboundAudio?.Writer.TryComplete();
                break;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{(IsOutgoing ? "→" : "←")} {RemoteUri} [{State}]";
}
