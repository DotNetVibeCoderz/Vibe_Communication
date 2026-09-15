# Troubleshooting

## Connection

| Symptom | Fix |
|---|---|
| `RumbleConnectionException: could not resolve host` | Check the host name and DNS. The failure happens during resolution, before TLS starts. |
| `operation timed out` | Check that the TCP port is reachable (Mumble's default is 64738) and that no firewall blocks it. Increase `ConnectTimeout` on slow links. |
| `RejectType = WrongServerPw` or `WrongUserPw` | Set `Password`. For registered names, use the account's certificate or password. |
| `RejectType = UsernameInUse` | Choose another name, or wait until the old session times out on the server. |
| `server certificate rejected: fingerprint mismatch` | The server's certificate changed. Verify it out of band before updating `PinnedFingerprint`. |
| Voice uses `TCP tunnel` instead of `UDP` | UDP is blocked or unconfirmed. This is normal for the first seconds after connecting. If it persists, open UDP on the same port. |
| Reconnects in a loop | `Disconnected.Reason` gives the cause. Rejections are never retried, but network errors are. Set `MaxReconnectAttempts` to cap retries. |

## Audio

| Symptom | Fix |
|---|---|
| `SendPcm` throws `Unsupported` | PCM push only works in `AudioMode.Headless`. |
| No sound in `Devices` mode | Call `RumbleNative.GetAudioDevices()` and check that input and output devices exist. On Android and iOS, grant microphone permission before connecting. |
| Others can't hear you | Check `Audio.IsTransmitting`. In `VoiceActivity` mode, lower `VoiceActivityThresholdDb`. The server may also have you suppressed (`User.Suppress`) or muted. |
| Choppy audio | Look at `PingUpdated.Local.Lost` and `Local.Late`. Increase `FramesPerPacket` or keep `InbandFec` on. Heavy work inside `AudioFrameReceived` blocks the audio thread, so keep handlers fast. |
| Echo or feedback | Use headphones, or use push-to-talk. |

## Native library

| Symptom | Fix |
|---|---|
| `DllNotFoundException: rumble_native` | Make sure `runtimes/{rid}/native/` exists in the output, or that `rumble_native.dll` sits next to your app. From source, build `src/Rumble.Net` once so the native build runs, or run `build/build-native.*`. |
| Build fails at `cargo build` | Install Rust (`rustup`) and the platform linker (MSVC, Xcode CLT or gcc). On Linux, install `libasound2-dev`. |
| `RUMBLE_ERR_PANIC` | This is a bug in the native core. Please report it together with the logs you get by setting `LoggerFactory` and `NativeLogLevel = Debug`. |

## Diagnostics

```csharp
options.LoggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
options.NativeLogLevel = LogLevel.Debug;   // forwards the Rust core's tracing output (category "Rumble.Native")
```
