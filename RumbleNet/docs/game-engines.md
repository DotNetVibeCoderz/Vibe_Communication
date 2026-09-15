# Game engines

The .NET 10 SDK can't be loaded by Unity's Mono or IL2CPP runtimes or by Unreal Engine. Both bridges therefore call the C ABI in `rumble_native` directly.

> **Status:** both bridges are reference implementations written against `rumble.h`. They haven't been compiled inside the Unity editor or an Unreal project yet (see [Progress.md](../Progress.md)).

## Unity

Package: [`bridges/unity/com.gravicode.rumble`](../bridges/unity/com.gravicode.rumble). See its README for installation.

- `RumbleVoiceClient` (a MonoBehaviour) connects on `Start`, plays voice through the OS audio device, maps push-to-talk to a `KeyCode`, and calls `rumble_audio_set_listener` every frame from `listenerTransform`.
- Native events are queued from the native thread and dispatched through the `onEvent` UnityEvent on the main thread in `Update`.
- The IL2CPP callback uses `[MonoPInvokeCallback]`. On iOS the static library is linked through `__Internal`.
- Unity's coordinate system matches Mumble's (left-handed, Y up, Z forward, meters), so positions pass through unchanged.

## Unreal Engine

Plugin: [`bridges/unreal/RumbleNet`](../bridges/unreal/RumbleNet).

- `URumbleVoiceSubsystem` (a GameInstance subsystem and tickable) exposes Blueprint functions: `Connect`, `Disconnect`, `JoinChannel`, `SendChannelMessage`, `SetTransmitMode`, `SetPushToTalk`, `GetInputLevelDb`, and the `OnRumbleEvent(Type, Json)` delegate.
- Each tick it converts the first player's camera to Mumble space: Unreal's X-forward, Z-up centimeters become Mumble's Z-forward, Y-up meters.

## Positional audio for everyone

Set `positionalTransmit: true` in the config so each voice packet carries your position. Other clients, including the official Mumble client with positional audio enabled, then place your voice in 3D.
