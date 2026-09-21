using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoipNet;

/// <summary>SIP signaling transport.</summary>
public enum SipTransport
{
    /// <summary>UDP (default for SIP).</summary>
    [JsonStringEnumMemberName("udp")]
    Udp,

    /// <summary>TCP, for large messages or firewalls that block UDP.</summary>
    [JsonStringEnumMemberName("tcp")]
    Tcp,

    /// <summary>TLS over TCP (SIPS signaling, default port 5061).</summary>
    [JsonStringEnumMemberName("tls")]
    Tls,

    /// <summary>SIP over WebSocket (RFC 7118), as used by browser softphones.</summary>
    [JsonStringEnumMemberName("ws")]
    Ws,

    /// <summary>SIP over secure WebSocket (TLS).</summary>
    [JsonStringEnumMemberName("wss")]
    Wss,
}

/// <summary>How media encryption is negotiated.</summary>
public enum SrtpMode
{
    /// <summary>Plain RTP only; encrypted offers are rejected with 488.</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,

    /// <summary>Offer RTP/AVP with an SDES key so peers may upgrade to SRTP.</summary>
    [JsonStringEnumMemberName("optional")]
    Optional,

    /// <summary>Require RTP/SAVP; unencrypted offers are rejected with 488.</summary>
    [JsonStringEnumMemberName("mandatory")]
    Mandatory,
}

/// <summary>How SRTP master keys are exchanged.</summary>
public enum SrtpKeying
{
    /// <summary>Keys in SDP <c>a=crypto</c> lines (SDES, RFC 4568). Protect the signaling with TLS.</summary>
    [JsonStringEnumMemberName("sdes")]
    Sdes,

    /// <summary>DTLS-SRTP handshake on the media path (RFC 5764), as used by WebRTC. Offers include ICE candidates.</summary>
    [JsonStringEnumMemberName("dtls")]
    Dtls,
}

/// <summary>How DTMF digits are transmitted.</summary>
public enum DtmfMode
{
    /// <summary>RTP telephone-events (RFC 4733). Recommended.</summary>
    [JsonStringEnumMemberName("rfc4733")]
    Rfc4733,

    /// <summary>Audible tones mixed into the audio stream.</summary>
    [JsonStringEnumMemberName("inBand")]
    InBand,

    /// <summary>SIP INFO with <c>application/dtmf-relay</c>.</summary>
    [JsonStringEnumMemberName("sipInfo")]
    SipInfo,
}

/// <summary>Configuration for a <see cref="VoipClient"/>.</summary>
public sealed class VoipClientOptions
{
    /// <summary>Local address to bind the SIP transport to. <c>0.0.0.0</c> binds every interface.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Local SIP port. Use 0 to let the operating system pick one.</summary>
    public int SipPort { get; set; } = 5060;

    /// <summary>Signaling transport.</summary>
    public SipTransport Transport { get; set; } = SipTransport.Udp;

    /// <summary>Address to advertise in Via, Contact and SDP when behind static NAT.</summary>
    public string? PublicAddress { get; set; }

    /// <summary>Display name shown to the remote party.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>SIP user name (the user part of the address of record).</summary>
    public string Username { get; set; } = "voipnet";

    /// <summary>Authentication user name when it differs from <see cref="Username"/>.</summary>
    public string? AuthUsername { get; set; }

    /// <summary>Password for digest authentication.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>SIP domain (for example <c>pbx.example.com</c>).</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>Registrar host when it differs from <see cref="Domain"/>.</summary>
    public string? Registrar { get; set; }

    /// <summary>Outbound proxy that receives every request.</summary>
    public string? OutboundProxy { get; set; }

    /// <summary>Register automatically when the client starts.</summary>
    public bool RegisterOnStart { get; set; }

    /// <summary>Registration lifetime in seconds. The client refreshes at 85% of this value.</summary>
    public int RegisterExpires { get; set; } = 600;

    /// <summary>Value of the User-Agent header.</summary>
    public string UserAgent { get; set; } = "Voip.NET/1.0 (Gravicode Studios)";

    /// <summary>Audio codecs to offer, in preference order. Supported natively: opus (48 kHz, in-band FEC), G722, PCMU, PCMA, L16.</summary>
    public IList<string> AudioCodecs { get; set; } = ["opus", "G722", "PCMU", "PCMA"];

    /// <summary>Media encryption policy.</summary>
    public SrtpMode Srtp { get; set; } = SrtpMode.Disabled;

    /// <summary>How SRTP keys are exchanged when <see cref="Srtp"/> is enabled. Incoming offers are accepted with either method.</summary>
    public SrtpKeying SrtpKeying { get; set; } = SrtpKeying.Sdes;

    /// <summary>How outgoing DTMF is sent.</summary>
    public DtmfMode DtmfMode { get; set; } = DtmfMode.Rfc4733;

    /// <summary>Advertise ICE candidates and answer connectivity checks.</summary>
    public bool Ice { get; set; }

    /// <summary>STUN server (<c>host:port</c>) used to discover the public media address.</summary>
    public string? StunServer { get; set; }

    /// <summary>TURN server (<c>host:port</c>) used to relay media when direct paths fail.</summary>
    public string? TurnServer { get; set; }

    /// <summary>TURN user name.</summary>
    public string TurnUsername { get; set; } = string.Empty;

    /// <summary>TURN password.</summary>
    public string TurnPassword { get; set; } = string.Empty;

    /// <summary>Lowest RTP port the engine may bind.</summary>
    public int RtpPortMin { get; set; } = 10000;

    /// <summary>Highest RTP port the engine may bind.</summary>
    public int RtpPortMax { get; set; } = 20000;

    /// <summary>Packetization interval in milliseconds (10–60).</summary>
    public int PtimeMs { get; set; } = 20;

    /// <summary>Minimum jitter buffer depth in milliseconds.</summary>
    public int JitterMinMs { get; set; } = 40;

    /// <summary>Maximum jitter buffer depth in milliseconds.</summary>
    public int JitterMaxMs { get; set; } = 300;

    /// <summary>Detect in-band DTMF tones in received audio (costs a little CPU per call).</summary>
    public bool DetectInbandDtmf { get; set; }

    /// <summary>Send 180 Ringing automatically for incoming calls.</summary>
    public bool AutoRinging { get; set; } = true;

    /// <summary>Accept REFER and place the transfer call automatically.</summary>
    public bool AcceptTransfers { get; set; } = true;

    /// <summary>Raise <see cref="VoipClient.SipTrace"/> for every SIP message sent and received.</summary>
    public bool TraceSip { get; set; }

    /// <summary>Interval of NAT keep-alive packets to the registrar, in seconds. 0 disables them.</summary>
    public int KeepaliveSecs { get; set; } = 25;

    /// <summary>Report a media event when no RTP arrives for this long. 0 disables the check.</summary>
    public int RtpTimeoutMs { get; set; }

    /// <summary>
    /// Let Opus stop sending while the caller is silent (discontinuous transmission). It saves bandwidth,
    /// but some gateways treat the gap as a dead stream.
    /// </summary>
    public bool OpusDtx { get; set; }

    /// <summary>
    /// Remove the echo of the call's own playback from the audio this client sends. Useful when the
    /// application plays through a speaker and captures with a microphone; harmless on a headset.
    /// </summary>
    public bool EchoCancellation { get; set; }

    /// <summary>Suppress steady background noise (fans, traffic) in the audio this client sends.</summary>
    public bool NoiseSuppression { get; set; }

    /// <summary>Even out the level of the audio this client sends.</summary>
    public bool AutoGain { get; set; }

    /// <summary>Validate the server certificate chain and host name for <see cref="SipTransport.Tls"/>. Ignored when <see cref="TlsPinnedFingerprints"/> is set.</summary>
    public bool TlsVerifyServer { get; set; } = true;

    /// <summary>PEM file with extra trust anchors (for example a private PBX CA), added to the public roots.</summary>
    public string? TlsCaFile { get; set; }

    /// <summary>SHA-256 certificate fingerprints to accept (<c>AA:BB:…</c>). When set, only these certificates are accepted, even if self-signed.</summary>
    public IList<string> TlsPinnedFingerprints { get; set; } = [];

    /// <summary>PEM certificate chain presented to peers. A self-signed certificate is generated when omitted.</summary>
    public string? TlsCertificateFile { get; set; }

    /// <summary>PEM private key for <see cref="TlsCertificateFile"/>.</summary>
    public string? TlsPrivateKeyFile { get; set; }

    /// <summary>
    /// Ask TLS and WSS callers for a certificate and refuse the connection unless it is trusted or
    /// pinned (mutual TLS). This client always presents its own certificate when a server asks.
    /// </summary>
    public bool TlsRequireClientCertificate { get; set; }

    /// <summary>Raise events on this context, so UI applications can update controls directly.</summary>
    [JsonIgnore]
    public SynchronizationContext? EventSynchronizationContext { get; set; }

    internal string ToJson() => JsonSerializer.Serialize(this, VoipJsonContext.Default.VoipClientOptions);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VoipClientOptions))]
[JsonSerializable(typeof(CallDescription))]
[JsonSerializable(typeof(CallDescription[]))]
internal sealed partial class VoipJsonContext : JsonSerializerContext;

/// <summary>Snapshot of a call as reported by the engine.</summary>
internal sealed class CallDescription
{
    public ulong CallId { get; set; }
    public string SipCallId { get; set; } = string.Empty;
    public bool Outgoing { get; set; }
    public string State { get; set; } = "terminated";
    public string RemoteUri { get; set; } = string.Empty;
    public string? RemoteDisplay { get; set; }
    public string LocalUri { get; set; } = string.Empty;
    public string? Codec { get; set; }
    public long DurationMs { get; set; }
    public bool LocalHold { get; set; }
    public bool RemoteHold { get; set; }
    public bool Muted { get; set; }
    public bool Srtp { get; set; }
}
