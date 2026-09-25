# Calls and media

🇮🇩 [Bahasa Indonesia](../id/calls-and-media.md) · Made by Gravicode Studios, led by Kang Fadhil

## Call lifecycle

| State | Meaning |
| --- | --- |
| `Calling` | INVITE sent, no response yet |
| `Ringing` / `EarlyMedia` | 180, or 183 with SDP (early media is played) |
| `Incoming` | a call is waiting for `AnswerAsync` or `Reject` |
| `Connected` | media flows both ways |
| `OnHold` / `RemoteHold` | this side or the other side holds |
| `Terminated` | ended; `Completion` completes, `FinalStatistics` is set |

```csharp
var call = client.Call("sip:1002@pbx");               // returns immediately
call.StateChanged += (_, e) => Console.WriteLine($"{e.State} {e.StatusCode} {e.Reason}");
try { await call.Connected; }                          // throws VoipException on 486, 603, 408…
catch (VoipException ex) { Console.WriteLine(ex.ErrorCode); }
var ended = await call.Completion;
```

Incoming calls:

```csharp
client.IncomingCall += async (_, e) =>
{
    if (e.From.Contains("spam")) { e.Call.Reject(603); return; }
    await e.Call.AnswerAsync();
};
```

## Audio in and out

```csharp
// Zero-copy, on the media thread (keep it fast)
call.AudioReceived += (c, direction, rate, samples) => meter.Push(samples);

// Async stream, frames dropped if you fall far behind
await foreach (var segment in call.ReadAudioAsync(AudioDirection.Inbound, ct)) { … }

// Send at any rate; bursts are paced by the engine
int queuedMs = call.SendAudio(pcm, sampleRate: 24000);
await call.SendAudioStreamAsync(ttsChunks, 24000, maxQueuedMs: 2000);
call.ClearAudio();                                     // barge-in
```

Pass-through codecs (G.729, SILK, Speex) are exchanged as encoded payloads:

```csharp
call.EncodedReceived += (c, payloadType, timestamp, marker, payload) => decoder.Feed(payload);
call.SendEncoded(96, rtpTimestamp, marker: true, h264Nal);
```

## Video

Set `Video = true` and the call carries an `m=video` stream next to the audio one, on its own RTP port
and with the same encryption and ICE as audio. The engine packetizes and reassembles whole frames
(H.264 FU-A/STAP-A per RFC 6184, VP8 per RFC 7741); encoding and decoding are yours to do, so pass
H.264 access units in Annex B form or VP8 frames:

```csharp
var options = new VoipClientOptions { Video = true, VideoCodecs = ["H264"] };

call.VideoFrameReceived += (c, timestamp, keyframe, frame) => decoder.Feed(frame, keyframe);
call.SendVideoFrame(rtpTimestamp90kHz, encodedFrame);   // split across as many packets as it needs
Console.WriteLine(call.VideoCodec);                     // "H264", or null on an audio-only call
```

A call can carry a second video stream showing a screen (RFC 4796 `a=content:slides`), offered with a
re-INVITE and withdrawn the same way:

```csharp
call.ShareScreen();                                        // offers a second m=video, marked as slides
call.SendVideoFrame(rtpTimestamp90kHz, encodedFrame, "slides");
Console.WriteLine(string.Join(", ", call.VideoStreams));   // "main, slides"
call.StopScreenShare();                                    // the m-line stays, offered with port 0
```

Received frames say which stream they came from, so a viewer can show the camera and the screen apart:

```csharp
call.VideoFrameReceived += (c, timestamp, keyframe, frame, content) =>
{
    if (content == "slides") { screenDecoder.Feed(frame, keyframe); } else { cameraDecoder.Feed(frame, keyframe); }
};
```

A frame that loses a packet is dropped rather than handed over damaged, so decoders never see a torn
frame; the engine then asks the sender for a keyframe (RTCP PLI, RFC 4585) so the picture comes back.
Ask for one yourself with `call.RequestKeyframe()`, and answer the peer's requests by encoding one:

```csharp
client.MediaNotification += (_, e) =>
{
    if (e.Kind == "keyframe-request")
    {
        encoder.ForceKeyframe();
    }
};
```

### How much to send

A video receiver measures what it can take and tells the sender over RTCP (REMB), which is how a
browser decides the bitrate to encode at. This engine does both ends of that: it sends an estimate
for every video stream it receives, and reports the peer's estimate so an application that encodes
can follow it.

```csharp
client.MediaNotification += (_, e) =>
{
    if (e.Kind == "bandwidth-estimate")
    {
        Console.WriteLine(e.Detail);                     // "450 kbit/s", when it changes by a tenth
    }
};

var allowed = call.GetStatistics().RemoteEstimateBps;    // 0 until the peer says
encoder.SetBitrate((int)(allowed * 0.9));                // leave room for audio and overhead
```

The estimate starts at 600 kbit/s, grows by 8 % while frames arrive whole, holds through a little
loss, and is cut in proportion to heavy loss — never above one and a half times what is actually
arriving, and never below 64 kbit/s. `a=rtcp-fb:<pt> goog-remb` is offered on video lines and kept in
answers that offered it, alongside `nack`, `nack pli` and `ccm fir`; a peer that negotiates none of
them is never sent feedback it did not ask for.

Camera capture and encoding are still the application's own — see
[PLAN 1.3](../../PLAN.md#13---video--fitur-video).

## Data channels

Set `DataChannels = true` and the call offers an `m=application` stream beside the audio: SCTP inside
its own DTLS tunnel (RFC 8831/8832), which is what a browser's `RTCDataChannel` speaks. It needs
`SrtpKeying.Dtls`, because the DTLS tunnel is what carries it.

```csharp
var options = new VoipClientOptions
{
    DataChannels = true,
    Srtp = SrtpMode.Mandatory,
    SrtpKeying = SrtpKeying.Dtls,
};

call.OpenDataChannel("chat");                  // queued until the association is up, then opened
client.MediaNotification += (_, e) =>
{
    if (e.Kind == "data-channel-open")
    {
        Console.WriteLine(e.Detail);           // "0 chat" — stream number and label
    }
};

call.DataMessageReceived += (c, stream, text, data) =>
{
    Console.WriteLine(text ? Encoding.UTF8.GetString(data) : $"{data.Length} bytes");
};

var channel = call.DataChannels[0];            // stream number and label, once open
call.SendData(channel.Stream, "halo dunia");   // text
call.SendData(channel.Stream, fileBytes);      // binary, split and put back together for the peer
```

Channels are reliable and ordered, and messages up to 256 KB are fragmented across SCTP packets and
reassembled at the other end. The side that opened DTLS uses even stream numbers and the other odd
ones, so both ends can open channels without agreeing on numbers first. `samples/VoipNet.WebPhone`
sends a line of text from the browser to the gateway and back, next to the audio and video.

Partial reliability (`maxRetransmits`, `maxPacketLifeTime`) and unordered delivery are not
implemented: every channel is reliable and ordered.

## Session timers and reliable provisionals

Calls carry a session timer (RFC 4028) by default: `Session-Expires: 1800`, refreshed by a re-INVITE
halfway through the interval. A call nobody refreshes within the interval is hung up, which is what
keeps a dialog from outliving a dead peer behind a proxy. Set `SessionExpires = 0` to leave the header
out; `MinSessionExpires` is the shortest interval accepted, and shorter offers get a 422 naming it.

Provisional responses are sent reliably (RFC 3262) to callers that advertise `100rel`: the 180 carries
`RSeq` and is resent until the caller PRACKs it. Callers that put `100rel` in `Require` always get
reliable provisionals; set `ReliableProvisional = false` to answer the rest the plain way.

## Codecs

| Codec | Payload | Rate | Implementation |
| --- | --- | --- | --- |
| Opus | 111 | 48 kHz (`opus/48000/2`, mono voice) | native (libopus): 32 kbit/s, in-band FEC, loss recovery from the next packet |
| G.722 | 9 | 16 kHz audio (8 kHz RTP clock) | native, fixed-point SB-ADPCM |
| PCMU / PCMA | 0 / 8 | 8 kHz | native, lookup tables |
| L16 | 97 | 16 kHz | native |
| telephone-event | 101, 110 | 8 kHz, 48 kHz | RFC 4733, at the audio codec's clock rate |
| G.729, SILK, Speex | 18, 112, 113 | — | negotiated, pass-through |
| H.264, VP8 | 96, 98 | 90 kHz | packetized video stream (see Video below); frames are encoded by the application |
| VP9 | 100 | 90 kHz | negotiated, pass-through |

Order `AudioCodecs` by preference. Answers follow the offerer's order (RFC 3264).

## DTMF

```csharp
var options = new VoipClientOptions { DtmfMode = DtmfMode.Rfc4733, DetectInbandDtmf = true };
call.SendDtmf("1234#", durationMs: 120);
client.DtmfReceived += (_, e) => Console.WriteLine($"{e.Digit} via {e.Source}");
```

`Rfc4733` falls back to in-band tones when the peer did not negotiate telephone-events.

## Hold, mute, transfer

```csharp
call.SetHold(true);          // re-INVITE a=sendonly; remote sees RemoteHold
call.SetMute(true);          // queued audio is transmitted as silence; the stream keeps running
call.Transfer("sip:2002@pbx");               // blind: REFER
consult = await client.CallAsync("sip:2002@pbx");
call.TransferTo(consult);                    // attended: REFER with Replaces
client.TransferProgress += (_, e) => …;      // NOTIFY sipfrag progress
```

When this endpoint receives a REFER and `AcceptTransfers` is true, it places the new call itself, reports progress with NOTIFY, and raises `TransferRequested`.

## Conferences

```csharp
using var conference = client.CreateConference();
conference.Add(call1);
conference.Add(call2);       // each participant hears everyone else
conference.Add(supervisor); supervisor.SetHold(true);   // listen-only: the bridge sends, ignores their audio
```

Mixing happens at 16 kHz inside the engine, per participant (mix-minus), with saturation.

Video in a conference is forwarded, not mixed — mixing would mean decoding and re-encoding every
stream, which needs codecs this engine does not carry. Each participant therefore sees one other
participant at a time:

```csharp
conference.FollowSpeaker();                    // the default: everyone sees whoever is talking
conference.Pin(conference.Participants[0]);    // or everyone sees one participant
Console.WriteLine(conference.ActiveSpeaker?.RemoteUri);
```

The speaker is chosen from the audio the bridge already mixes: the loudest participant takes the floor,
keeps it for a moment after they stop, and only loses it to somebody clearly louder — so the picture
does not flick between two people talking over each other. When the source changes, the bridge asks the
new speaker for a keyframe and holds the switch back until it arrives, because a decoder cannot start
mid-picture. A grid view needs one stream per participant and is not offered.

## Recording

```csharp
using var recorder = CallRecorder.Start(call, "call.mp3", RecordingFormat.Mp3, RecordingLayout.Stereo);
```

Stereo puts the remote party on the left and this endpoint on the right. MP3 encoding uses the bundled LAME encoder, which is Windows-only; on Linux and macOS the recorder writes WAV and reports the new path in `recorder.Path`.

A video call records to AVI, with the video stored exactly as the peer encoded it next to PCM audio —
nothing is re-encoded, so recording costs almost no CPU:

```csharp
using var recorder = CallRecorder.Start(call, "call.avi", RecordingFormat.Avi);
```

The frame rate is measured from the call and written into the header when the file is closed. A call
without video falls back to WAV. MP4 with AAC audio would need an AAC encoder this SDK does not carry
(see [PLAN 1.3](../../PLAN.md#13---video--fitur-video)).

## Security

```csharp
new VoipClientOptions
{
    Transport = SipTransport.Tls,          // SIP over TLS 1.2/1.3 (rustls), port 5061
    TlsPinnedFingerprints = ["3F:A2:…"],   // optional: accept only this certificate
    Srtp = SrtpMode.Mandatory,             // SDES keys now travel inside TLS
};
call.GetStatistics().SecureRtp;   // true when both directions are protected
```

Renewed certificates are picked up with `client.ReloadTls()`, which affects new connections only, so a renewal never drops a call. Set `TlsRequireClientCertificate` to make callers prove who they are as well (mutual TLS): the same certificate and pinning rules then apply in both directions. Server certificates are checked against the Mozilla root store plus `TlsCaFile`, including the host name from the target URI, registrar or proxy. Pinning replaces chain validation, which suits PBXs with self-signed certificates. Each endpoint presents `TlsCertificateFile`/`TlsPrivateKeyFile`, or a generated self-signed certificate whose fingerprint is `client.TlsFingerprint`. A failed handshake ends the request at once with `503`.

## WebRTC browsers

Browsers call a `VoipClient` directly: SIP over WebSocket (RFC 7118) for signaling, ICE for the media path and DTLS-SRTP for keys.

```csharp
var gateway = new VoipClient(new VoipClientOptions
{
    Transport = SipTransport.Ws,           // or Wss with a TLS certificate
    SipPort = 5090,
    Srtp = SrtpMode.Mandatory,
    SrtpKeying = SrtpKeying.Dtls,          // a=fingerprint + a=setup, UDP/TLS/RTP/SAVP
    Ice = true,
});
gateway.MediaNotification += (_, e) => Console.WriteLine($"{e.Kind} {e.Detail}"); // ice-connected, dtls-connected AES_CM_128_HMAC_SHA1_80
```

The engine accepts `UDP/TLS/RTP/SAVPF` offers, answers with ICE candidates, `a=mid` and BUNDLE, verifies the peer certificate against the signaled fingerprint, and sends no media until the DTLS keys are in place. SRTP runs with AES-CM-128-HMAC-SHA1-80 or AEAD-AES-128/256-GCM, whichever the handshake selects. Incoming offers are accepted with SDES or DTLS keys whatever `SrtpKeying` says; the setting decides what this client offers. The browser side can be any SIP-over-WebSocket client; `samples/VoipNet.WebPhone` includes a small one and bridges the browser to a plain SIP phone.

## NAT and connectivity

| Setting | Use it when |
| --- | --- |
| automatic `rport` learning | behind a typical NAT with a registrar (on by default) |
| `PublicAddress` | static 1:1 NAT |
| `StunServer` | media must advertise the public address |
| `TurnServer`, `TurnUsername`, `TurnPassword` | symmetric NAT or strict firewalls |
| `Ice = true` | talking to ICE-capable peers, or several network paths (see ICE below) |
| `KeepaliveSecs` | keep UDP pinholes open between registrations |

### ICE

With `Ice = true` (always on for DTLS-SRTP and whenever the peer offers ICE), each call runs a full ICE agent (RFC 8445): it pairs local host, server-reflexive and relayed candidates with the peer's, paces connectivity checks, answers checks with triggered checks, learns peer-reflexive candidates (which covers browsers that hide addresses behind mDNS names), resolves role conflicts, and nominates one pair. The offerer is the controlling agent. Candidates the peer trickles later in SIP INFO (`application/trickle-ice-sdpfrag`, RFC 8840) join the checks, and ours travel the same way: an ICE call sends its STUN probe without waiting for the answer, so a slow or unreachable STUN server never delays the call, and the reflexive candidate is sent to the peer as soon as it arrives. (A call without ICE still waits: there the reflexive address is what the SDP advertises.) Once a pair is selected, consent is refreshed every few seconds (RFC 7675).

```csharp
client.MediaNotification += (_, e) => Console.WriteLine($"{e.Kind}: {e.Detail}");
// ice-candidates: host 192.168.1.20:50901     (trickled by the peer)
// ice-connected: host → prflx 192.168.1.20:50901
// ice-disconnected: consent expired            ice-failed: no candidate pair worked

call.RestartIce();   // after a network change: re-INVITE with new credentials; media continues until a new pair is chosen
```

## Quality statistics

`GetStatistics()` returns packets, bytes, loss, late packets, jitter, jitter-buffer depth, sample rate, an E-model MOS estimate, SRTP and ICE state, and the outbound queue. After the call ends, `FinalStatistics` keeps the last values.

The engine also exchanges RTCP sender and receiver reports (RFC 3550), so the same statistics say what the **peer** receives: `RemoteLossPercent`, `RemoteJitterMs` and `RoundTripMs`. The first report goes out a second into the call and repeats every few seconds, so these stay zero at the very start. Opus uses them directly: as the reported loss rises it lowers its bitrate and asks for more forward error correction.

Alongside each report the engine sends RTCP XR VoIP metrics (RFC 3611) — loss and discard rate, delays, R factor, MOS — and reads the peer's, which appears as `RemoteMos`: the quality the other side actually hears.
