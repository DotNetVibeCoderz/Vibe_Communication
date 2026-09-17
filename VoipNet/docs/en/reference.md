# Configuration and API reference

🇮🇩 [Bahasa Indonesia](../id/reference.md) · Made by Gravicode Studios, led by Kang Fadhil

## `VoipClientOptions`

| Property | Default | Description |
| --- | --- | --- |
| `BindAddress` | `0.0.0.0` | Local address for SIP and RTP. |
| `SipPort` | `5060` | Local SIP port; `0` picks a free port. |
| `Transport` | `Udp` | `Udp`, `Tcp`, `Tls` (default remote port 5061), `Ws` or `Wss` (SIP over WebSocket). |
| `PublicAddress` | — | Address advertised in Via, Contact and SDP (static NAT). |
| `DisplayName` | — | Display name in From. |
| `Username` | `voipnet` | User part of the address of record. |
| `AuthUsername` | — | Digest user name when different from `Username`. |
| `Password` | — | Digest password. |
| `Domain` | — | SIP domain; extensions resolve against it. |
| `Registrar` | — | Registrar host when different from the domain. |
| `OutboundProxy` | — | Every request is sent here. |
| `RegisterOnStart` | `false` | Register during `StartAsync` and wait for the result. |
| `RegisterExpires` | `600` | Requested lifetime; refreshed at 85%. |
| `UserAgent` | `Voip.NET/1.0 (Gravicode Studios)` | User-Agent / Server header. |
| `AudioCodecs` | `opus, G722, PCMU, PCMA` | Preference order. Native: opus, G722, PCMU, PCMA, L16. |
| `Srtp` | `Disabled` | `Disabled`, `Optional`, `Mandatory`. |
| `SrtpKeying` | `Sdes` | `Sdes` (`a=crypto`) or `Dtls` (DTLS-SRTP with ICE, as WebRTC uses). Incoming offers may use either. |
| `DtmfMode` | `Rfc4733` | `Rfc4733`, `InBand`, `SipInfo`. |
| `Ice` | `false` | Run a full ICE agent (candidates, checks, nomination, trickle, restart). Implied by DTLS keying or an ICE offer. |
| `StunServer` | — | `host:port` for the server-reflexive media address. |
| `TurnServer`, `TurnUsername`, `TurnPassword` | — | TURN relay. |
| `RtpPortMin`, `RtpPortMax` | `10000`–`20000` | RTP port range. |
| `PtimeMs` | `20` | Packetization interval. |
| `JitterMinMs`, `JitterMaxMs` | `40`, `300` | Jitter buffer bounds. |
| `DetectInbandDtmf` | `false` | Run the in-band DTMF detector on received audio. |
| `AutoRinging` | `true` | Send 180 Ringing for incoming calls. |
| `AcceptTransfers` | `true` | Act on incoming REFER. |
| `TraceSip` | `false` | Raise `SipTrace` for every message. |
| `KeepaliveSecs` | `25` | CRLF keep-alives to the registrar over UDP. |
| `RtpTimeoutMs` | `0` | Raise a `rtp-timeout` media event after this much silence on the wire. |
| `TlsVerifyServer` | `true` | Validate the server certificate chain and host name (Mozilla roots plus `TlsCaFile`). |
| `TlsCaFile` | — | PEM file with extra trust anchors, e.g. a private PBX CA. |
| `TlsPinnedFingerprints` | — | SHA-256 fingerprints to accept; only these certificates pass, self-signed included. |
| `TlsCertificateFile`, `TlsPrivateKeyFile` | — | PEM certificate chain and key presented to peers; a self-signed certificate is generated otherwise. |
| `EventSynchronizationContext` | — | Marshal events to a UI thread. |

## `VoipClient`

| Member | Description |
| --- | --- |
| `StartAsync()` | Start the engine; waits for registration when `RegisterOnStart` is set. |
| `RegisterAsync()` / `Unregister()` | Manage the registration. |
| `CallAsync(target)` | Call and wait until answered. |
| `Call(target)` | Call and return immediately. |
| `PingAsync(target)` | OPTIONS; returns status and latency. |
| `SendMessageAsync(target, body, contentType)` | SIP MESSAGE. |
| `CreateConference()` | New `VoipConference`. |
| `Calls`, `FindCall(id)` | Current and recently ended calls. |
| `LocalAddress`, `RegistrationState`, `EngineVersion`, `TlsFingerprint` | State. |
| Events | `IncomingCall`, `CallStateChanged`, `MediaStarted`, `RegistrationChanged`, `DtmfReceived`, `MessageReceived`, `TransferRequested`, `TransferProgress`, `MediaNotification` (`ice-candidates`, `ice-connected`, `ice-disconnected`, `ice-failed`, `dtls-connected`, `dtls-failed`, `rtp-timeout`), `SipTrace`. |

## `VoipCall`

| Member | Description |
| --- | --- |
| `AnswerAsync()`, `Reject(code)`, `Hangup()`, `HangupAsync()` | Control. |
| `SetHold(bool)`, `SetMute(bool)`, `SendDtmf(digits, ms)`, `RestartIce()` | Control. |
| `Transfer(target)`, `TransferTo(call)` | Blind and attended transfer. |
| `SendAudio(samples, rate)`, `SendAudio(bytes, rate)`, `SendAudioStreamAsync(...)`, `ClearAudio()`, `QueuedAudioMs` | Outbound audio. |
| `AudioReceived`, `ReadAudioAsync(direction)` | Audio from the call. |
| `SendEncoded(...)`, `EncodedReceived` | Pass-through payloads. |
| `GetStatistics()`, `FinalStatistics` | Quality. |
| `JoinConference(c)`, `LeaveConference()` | Conferencing. |
| `Connected`, `Completion` | Tasks for linear async code. |
| `Id`, `IsOutgoing`, `RemoteUri`, `RemoteDisplayName`, `State`, `Codec`, `SampleRate`, `Duration`, `Items` | Information. |

## Dependency injection

| Extension | Registers |
| --- | --- |
| `AddVoipClient(configure)` | singleton `VoipClient` with options |
| `AddVoipClientHostedService()` | starts and stops the client with the host |
| `AddOpenAiChatClient`, `AddAnthropicChatClient`, `AddGeminiChatClient` | `IChatClient` with logging and function invocation |
| `AddDeepgramSpeechToText`, `AddOpenAiSpeech`, `AddGoogleCloudSpeech`, `AddElevenLabsTextToSpeech`, `AddAmazonPollyTextToSpeech`, `AddElBrunoRealtimeSpeech` | speech providers |
| `AddVoiceAgent(configure)` | `VoiceAgentOptions`, `IConversationStore` (in memory), `VoiceAgentFactory` |
| `AddRealtimeVoiceAgent(configure)` | `RealtimeVoiceAgent` |
| `AddVoipNetEnterprise(configureRecording)` | `IvrRunner`, `CallCenterService`, `RecordingService`, `ICrmConnector` (in memory) |

## Native C API

For bindings in other languages, `voipnet_core` exports (all return `0` on success or a negative error):

```c
typedef void (*vn_event_cb)(void* user, const char* json);
typedef void (*vn_audio_cb)(void* user, uint64_t call, int direction, uint32_t rate, const int16_t* pcm, int samples);
typedef void (*vn_dtmf_cb)(void* user, uint64_t call, uint32_t digit, int source);
typedef void (*vn_encoded_cb)(void* user, uint64_t call, uint8_t pt, uint32_t ts, int marker, const uint8_t* data, int len);
typedef struct { vn_event_cb on_event; vn_audio_cb on_audio; vn_dtmf_cb on_dtmf; vn_encoded_cb on_encoded; void* user; } vn_callbacks;

int  voipnet_endpoint_create(const char* config_json, vn_callbacks cb, void** handle, char* err, int err_len);
int  voipnet_endpoint_destroy(void* handle);
int  voipnet_register(void* h);                       int voipnet_unregister(void* h);
int  voipnet_make_call(void* h, const char* target, uint64_t* call);
int  voipnet_answer(void* h, uint64_t call);          int voipnet_reject(void* h, uint64_t call, uint16_t code);
int  voipnet_hangup(void* h, uint64_t call);
int  voipnet_set_hold(void* h, uint64_t call, int on); int voipnet_set_mute(void* h, uint64_t call, int on);
int  voipnet_transfer(void* h, uint64_t call, const char* target);
int  voipnet_transfer_attended(void* h, uint64_t call, uint64_t consult);
int  voipnet_send_dtmf(void* h, uint64_t call, const char* digits, uint32_t ms);
int  voipnet_send_audio(void* h, uint64_t call, const int16_t* pcm, int samples, uint32_t rate, uint32_t* queued_ms);
int  voipnet_clear_audio(void* h, uint64_t call);
int  voipnet_send_encoded(void* h, uint64_t call, uint8_t pt, uint32_t ts, int marker, const uint8_t* data, int len);
int  voipnet_call_stats(void* h, uint64_t call, vn_media_stats* out);
char* voipnet_call_info_json(void* h, uint64_t call); char* voipnet_calls_json(void* h);
int  voipnet_send_options(void* h, const char* target, uint64_t* request);
int  voipnet_send_message(void* h, const char* target, const char* type, const char* body, uint64_t* request);
int  voipnet_conference_create(void* h, uint64_t* id); int voipnet_conference_add(void* h, uint64_t id, uint64_t call);
int  voipnet_conference_remove(void* h, uint64_t call); int voipnet_conference_destroy(void* h, uint64_t id);
char* voipnet_local_address(void* h);                  void voipnet_string_free(char* s);
const char* voipnet_version(void);
```

Error codes: `-1` invalid argument, `-2` invalid configuration, `-3` I/O, `-4` call not found, `-5` invalid state. Configuration JSON uses the camelCase names of `VoipClientOptions`. Events are JSON objects with a `type` of `registrationChanged`, `incomingCall`, `callState`, `mediaStarted`, `transferRequested`, `transferProgress`, `messageReceived`, `requestResult`, `mediaEvent`, `sipTrace` or `log`.
