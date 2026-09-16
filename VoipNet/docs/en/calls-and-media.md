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

Pass-through codecs (video, G.729, Opus) are exchanged as encoded payloads:

```csharp
call.EncodedReceived += (c, payloadType, timestamp, marker, payload) => decoder.Feed(payload);
call.SendEncoded(96, rtpTimestamp, marker: true, h264Nal);
```

## Codecs

| Codec | Payload | Rate | Implementation |
| --- | --- | --- | --- |
| G.722 | 9 | 16 kHz audio (8 kHz RTP clock) | native, fixed-point SB-ADPCM |
| PCMU / PCMA | 0 / 8 | 8 kHz | native, lookup tables |
| L16 | 97 | 16 kHz | native |
| telephone-event | 101 | — | RFC 4733 |
| G.729, Opus, SILK, Speex | 18, 111–113 | — | negotiated, pass-through |
| H.264, VP8, VP9 | 96, 98, 100 | 90 kHz | negotiated, pass-through |

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

## Recording

```csharp
using var recorder = CallRecorder.Start(call, "call.mp3", RecordingFormat.Mp3, RecordingLayout.Stereo);
```

Stereo puts the remote party on the left and this endpoint on the right. MP3 encoding uses the bundled LAME encoder, which is Windows-only; on Linux and macOS the recorder writes WAV and reports the new path in `recorder.Path`.

## Security

```csharp
new VoipClientOptions { Srtp = SrtpMode.Mandatory, Transport = SipTransport.Tcp };
call.GetStatistics().SecureRtp;   // true when both directions are protected
```

## NAT and connectivity

| Setting | Use it when |
| --- | --- |
| automatic `rport` learning | behind a typical NAT with a registrar (on by default) |
| `PublicAddress` | static 1:1 NAT |
| `StunServer` | media must advertise the public address |
| `TurnServer`, `TurnUsername`, `TurnPassword` | symmetric NAT or strict firewalls |
| `Ice = true` | talking to ICE-capable peers |
| `KeepaliveSecs` | keep UDP pinholes open between registrations |

## Quality statistics

`GetStatistics()` returns packets, bytes, loss, late packets, jitter, jitter-buffer depth, sample rate, an E-model MOS estimate, SRTP and ICE state, and the outbound queue. After the call ends, `FinalStatistics` keeps the last values.
