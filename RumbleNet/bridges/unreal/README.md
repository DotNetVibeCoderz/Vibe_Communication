# Rumble.Net for Unreal Engine

`RumbleNet` is a UE5 plugin that calls the Rust core directly through its C header (`native/include/rumble.h`). It gives a game-instance subsystem Blueprint functions for connecting, changing channels, text chat and push-to-talk, and it keeps positional audio in sync with the player camera.

> **Status:** this is a reference implementation. It hasn't been compiled inside an Unreal Engine project in this repository's CI yet (see `Progress.md`).

## Install

1. Build the native library with `build/build-native.ps1`.
2. Copy `bridges/unreal/RumbleNet` into `<YourProject>/Plugins/`.
3. Create `Plugins/RumbleNet/Source/ThirdParty/rumble/` with this layout:
   ```
   include/rumble.h                   (from native/include)
   lib/Win64/rumble_native.dll        (+ rumble_native.dll.lib from native/target/release)
   lib/Linux/librumble_native.so
   lib/Mac/librumble_native.dylib
   ```
4. Regenerate the project files and build.

## Blueprint usage

```
Get Game Instance -> Get Subsystem (RumbleVoiceSubsystem)
  -> Connect("voice.example.org", 64738, "Player1", "", true)
  -> Bind Event to OnRumbleEvent (Type, Json)
```

Unreal and Mumble use different coordinate systems. The subsystem converts Unreal's X-forward, Z-up centimeters to Mumble's Z-forward, Y-up meters.

---

Made by **Gravicode Studios**, led by **Kang Fadhil**.
