namespace VoipNet;

/// <summary>Lifecycle of a call.</summary>
public enum CallState
{
    /// <summary>An outgoing INVITE was sent and no response has arrived yet.</summary>
    Calling,

    /// <summary>The remote party is ringing.</summary>
    Ringing,

    /// <summary>The remote party is sending media before answering (ringback, announcements).</summary>
    EarlyMedia,

    /// <summary>An incoming call is waiting to be answered or rejected.</summary>
    Incoming,

    /// <summary>Media is flowing in both directions.</summary>
    Connected,

    /// <summary>This side put the call on hold.</summary>
    OnHold,

    /// <summary>The remote party put the call on hold.</summary>
    RemoteHold,

    /// <summary>The call has ended.</summary>
    Terminated,
}

/// <summary>Direction of an audio frame relative to this endpoint.</summary>
public enum AudioDirection
{
    /// <summary>Audio received from the remote party.</summary>
    Inbound,

    /// <summary>Audio this endpoint transmitted.</summary>
    Outbound,
}

/// <summary>How a DTMF digit reached this endpoint.</summary>
public enum DtmfSource
{
    /// <summary>RTP telephone-event (RFC 4733).</summary>
    Rfc4733,

    /// <summary>Detected as an audible tone in the audio stream.</summary>
    InBand,

    /// <summary>SIP INFO message.</summary>
    SipInfo,
}

/// <summary>Registration status with the registrar.</summary>
public enum RegistrationState
{
    /// <summary>No registration has been attempted, or the last one was removed.</summary>
    Unregistered,

    /// <summary>The registrar accepted the binding.</summary>
    Registered,

    /// <summary>The registrar rejected the binding or did not answer.</summary>
    Failed,
}

/// <summary>Receives audio frames without copying. Handlers run on engine threads and must return quickly.</summary>
/// <param name="call">The call the audio belongs to.</param>
/// <param name="direction">Whether the audio was received or transmitted.</param>
/// <param name="sampleRate">Sample rate of <paramref name="samples"/>, in hertz.</param>
/// <param name="samples">16-bit mono PCM for one packetization interval.</param>
public delegate void AudioFrameHandler(VoipCall call, AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples);

/// <summary>Receives payloads of codecs the engine does not decode itself (video, G.729, Opus…).</summary>
/// <param name="call">The call the payload belongs to.</param>
/// <param name="payloadType">RTP payload type.</param>
/// <param name="timestamp">RTP timestamp.</param>
/// <param name="marker">RTP marker bit.</param>
/// <param name="payload">Encoded payload bytes.</param>
public delegate void EncodedFrameHandler(VoipCall call, int payloadType, uint timestamp, bool marker, ReadOnlySpan<byte> payload);

/// <summary>Who a conference's participants see.</summary>
public enum ConferenceLayout
{
    /// <summary>Everyone sees whoever is speaking.</summary>
    SpeakerFocus,

    /// <summary>Everyone sees one pinned participant.</summary>
    Pinned,
}

/// <summary>Handles a complete video frame received on a call.</summary>
/// <param name="call">The call the frame belongs to.</param>
/// <param name="timestamp">RTP timestamp in the 90 kHz video clock.</param>
/// <param name="keyframe">True when the frame can be decoded on its own.</param>
/// <param name="frame">The frame bytes: one H.264 access unit in Annex B form, or one VP8 frame.</param>
/// <param name="content">Which stream it came from: <c>main</c> for the camera, <c>slides</c> for a shared screen.</param>
public delegate void VideoFrameHandler(VoipCall call, uint timestamp, bool keyframe, ReadOnlySpan<byte> frame, string content);

/// <summary>Registration state change.</summary>
/// <param name="State">New registration state.</param>
/// <param name="StatusCode">SIP status code that caused the change.</param>
/// <param name="Reason">Reason phrase from the registrar.</param>
/// <param name="ExpiresSeconds">Granted registration lifetime.</param>
public sealed record RegistrationEventArgs(RegistrationState State, int StatusCode, string Reason, int ExpiresSeconds);

/// <summary>A call changed state.</summary>
/// <param name="Call">The call.</param>
/// <param name="State">New state.</param>
/// <param name="StatusCode">SIP status code, when one caused the change.</param>
/// <param name="Reason">Human readable reason.</param>
public sealed record CallStateEventArgs(VoipCall Call, CallState State, int StatusCode, string Reason);

/// <summary>Media started flowing for a call.</summary>
/// <param name="Call">The call.</param>
/// <param name="Codec">Negotiated codec name.</param>
/// <param name="SampleRate">Audio sample rate in hertz.</param>
/// <param name="RemoteEndPoint">Remote RTP address.</param>
/// <param name="SecureRtp">Whether SRTP is active.</param>
public sealed record MediaStartedEventArgs(VoipCall Call, string Codec, int SampleRate, string RemoteEndPoint, bool SecureRtp);

/// <summary>A DTMF digit was received.</summary>
/// <param name="Call">The call.</param>
/// <param name="Digit">The digit (0-9, *, #, A-D).</param>
/// <param name="Source">How the digit was signalled.</param>
public sealed record DtmfEventArgs(VoipCall Call, char Digit, DtmfSource Source);

/// <summary>An incoming call is ringing.</summary>
/// <param name="Call">The call, which can be answered or rejected.</param>
/// <param name="From">Caller address of record.</param>
/// <param name="DisplayName">Caller display name, when present.</param>
/// <param name="To">Called address of record.</param>
/// <param name="HasVideo">Whether the caller offered a video stream.</param>
/// <param name="ReplacesCall">The call this one replaces during an attended transfer.</param>
public sealed record IncomingCallEventArgs(VoipCall Call, string From, string? DisplayName, string To, bool HasVideo, VoipCall? ReplacesCall);

/// <summary>A SIP MESSAGE was received.</summary>
/// <param name="From">Sender address of record.</param>
/// <param name="ContentType">MIME type of the body.</param>
/// <param name="Body">Message body.</param>
public sealed record SipMessageEventArgs(string From, string ContentType, string Body);

/// <summary>The remote party asked this endpoint to transfer the call.</summary>
/// <param name="Call">The call carrying the REFER.</param>
/// <param name="Target">Transfer target URI.</param>
/// <param name="NewCall">The call placed to the target, when transfers are accepted automatically.</param>
public sealed record TransferRequestedEventArgs(VoipCall Call, string Target, VoipCall? NewCall);

/// <summary>Progress of a transfer this endpoint requested.</summary>
/// <param name="Call">The transferred call.</param>
/// <param name="StatusCode">Latest SIP status code reported by the transferee.</param>
/// <param name="Reason">Reason phrase.</param>
public sealed record TransferProgressEventArgs(VoipCall Call, int StatusCode, string Reason);

/// <summary>A media-level notification such as ICE connectivity or an RTP timeout.</summary>
/// <param name="Call">The call.</param>
/// <param name="Kind">Event kind, for example <c>ice-connected</c> or <c>rtp-timeout</c>.</param>
/// <param name="Detail">Additional detail.</param>
public sealed record MediaEventArgs(VoipCall Call, string Kind, string Detail);

/// <summary>A SIP message crossing the wire, when tracing is enabled.</summary>
/// <param name="Outgoing">True when this endpoint sent the message.</param>
/// <param name="RemoteEndPoint">Peer address.</param>
/// <param name="Message">The full SIP message.</param>
public sealed record SipTraceEventArgs(bool Outgoing, string RemoteEndPoint, string Message);

/// <summary>Quality statistics for one call.</summary>
/// <param name="PacketsSent">RTP packets transmitted.</param>
/// <param name="PacketsReceived">RTP packets received.</param>
/// <param name="BytesSent">Payload bytes transmitted.</param>
/// <param name="BytesReceived">Payload bytes received.</param>
/// <param name="PacketsLost">Packets never played out.</param>
/// <param name="PacketsLate">Packets that arrived after their playout time.</param>
/// <param name="JitterMs">Inter-arrival jitter estimate in milliseconds.</param>
/// <param name="JitterBufferMs">Current jitter buffer depth in milliseconds.</param>
/// <param name="PayloadType">Negotiated RTP payload type.</param>
/// <param name="SampleRate">Audio sample rate in hertz.</param>
/// <param name="Mos">Estimated mean opinion score (1.0–4.5, E-model).</param>
/// <param name="SecureRtp">Whether SRTP is protecting the stream.</param>
/// <param name="IceConnected">Whether an ICE candidate pair was nominated.</param>
/// <param name="OutboundQueuedMs">Audio queued for paced transmission.</param>
/// <param name="RemoteLossPercent">Loss the peer reports on the stream this side sends (RTCP).</param>
/// <param name="RemoteJitterMs">Jitter the peer reports, in milliseconds.</param>
/// <param name="RoundTripMs">Round-trip time from RTCP reports; zero until the peer reports.</param>
/// <param name="RemoteMos">MOS the peer reports for the audio it hears (RTCP XR); zero when it sends no extended reports.</param>
public sealed record CallStatistics(
    long PacketsSent,
    long PacketsReceived,
    long BytesSent,
    long BytesReceived,
    long PacketsLost,
    long PacketsLate,
    double JitterMs,
    int JitterBufferMs,
    int PayloadType,
    int SampleRate,
    double Mos,
    bool SecureRtp,
    bool IceConnected,
    int OutboundQueuedMs,
    double RemoteLossPercent = 0,
    double RemoteJitterMs = 0,
    double RoundTripMs = 0,
    double RemoteMos = 0)
{
    /// <summary>Packet loss as a percentage of expected packets.</summary>
    public double LossPercent => PacketsReceived + PacketsLost == 0 ? 0 : PacketsLost * 100.0 / (PacketsReceived + PacketsLost);
}

/// <summary>Result of an out-of-dialog request such as OPTIONS or MESSAGE.</summary>
/// <param name="StatusCode">Final SIP status code, or 408 on timeout.</param>
/// <param name="Reason">Reason phrase.</param>
/// <param name="Latency">Round trip time.</param>
/// <param name="UserAgent">User-Agent or Server header of the responder.</param>
public sealed record SipRequestResult(int StatusCode, string Reason, TimeSpan Latency, string? UserAgent)
{
    /// <summary>True for 2xx responses.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>Thrown when the engine rejects an operation.</summary>
public sealed class VoipException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public VoipException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner exception.</summary>
    public VoipException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Native error code, when the failure came from the engine.</summary>
    public int ErrorCode { get; init; }
}
