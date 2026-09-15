# Rumble.Net for Unity

Mumble voice chat for Unity 2022.3+ that talks to the Rumble.Net Rust core through its C ABI.

This bridge doesn't use the .NET 10 `Rumble.Net` assembly. Unity's Mono and IL2CPP runtimes can't load it, so the bridge binds `rumble_native` directly.

> **Status:** this is a reference implementation. It was written against `native/include/rumble.h` but hasn't been verified inside the Unity editor yet (see `Progress.md`).

## Install

1. Build the native library for each target platform with `build/build-native.ps1` or `build/build-native.sh`.
2. Copy `bridges/unity/com.gravicode.rumble` into your project's `Packages/` folder.
3. Put the native binaries under `Packages/com.gravicode.rumble/Plugins/<platform>/`, using the file for each platform:
   - `x86_64/rumble_native.dll` for Windows
   - `librumble_native.so` for Linux and Android
   - `librumble_native.dylib` for macOS
   - `librumble_native.a` for iOS (static, loaded via `__Internal`)
4. Add `RumbleVoiceClient` to a GameObject. Set the host and username, and assign the player camera as `listenerTransform`.

## Usage

```csharp
var voice = GetComponent<RumbleVoiceClient>();
voice.onEvent.AddListener(json => Debug.Log(json)); // {"type":"UserJoined",...}
voice.JoinChannel(1);
voice.SendChannelMessage(1, "Hello from Unity!");
```

Push-to-talk defaults to <kbd>V</kbd>. Positional audio follows `listenerTransform` every frame.

---

Made by **Gravicode Studios**, led by **Kang Fadhil**.
