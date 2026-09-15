# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Rumble.Net is a Mumble voice SDK for .NET 10 with a Rust core, which is a rewrite of libmumble's scope. `requirements.md` (in Indonesian) is the original spec. `PLAN.md` holds the roadmap and `Progress.md` the verified status. **Update Progress.md** whenever you finish or verify something.

## Commands

The .NET SDK is pinned in `global.json` (10.0.400 or newer). Tests use **Microsoft.Testing.Platform**, so the syntax is `dotnet test --project …` and filters go after `--`.

```powershell
# Rust core (run from native/)
cargo test --workspace
cargo test -p rumble-protocol crypt::tests::ocb2_test_vectors       # single test
cargo test -p rumble-client --test integration                      # end-to-end against the mock server
cargo bench -p rumble-audio                                          # Criterion (rumble-protocol too)
cargo run -p rumble-mock-server -- 127.0.0.1:64738 [password]        # standalone test server

# .NET (run from the repo root; building src/Rumble.Net runs `cargo build --release -p rumble-ffi` automatically)
dotnet test --project tests/Rumble.Net.Tests/Rumble.Net.Tests.csproj
dotnet test --project tests/Rumble.Net.Tests/Rumble.Net.Tests.csproj -- --filter-method "*Reconnects*"
dotnet run --project samples/RumbleGallery                           # UI
dotnet run --project samples/RumbleGallery -- --run-all              # run all samples headless
dotnet build samples/RumbleApp -f net10.0-windows10.0.19041.0
dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- latency   # or: native | --filter *

# Packaging
./build/build-native.ps1 -Rids win-x64      # or build-native.sh on Linux/macOS
./build/pack.ps1                            # tests + NuGet packages into ./artifacts
```

Pass `-p:RumbleSkipNativeBuild=true` to use prebuilt binaries staged under `src/Rumble.Net/runtimes/{rid}/native`.

## Architecture (big picture)

```
.NET RumbleClient ──JSON commands/events──┐                ┌─ rumble-client (tokio + rustls)
.NET RumbleAudio  ──raw float* / callbacks┼─ rumble-ffi ───┤   supervisor → session select! loop
                                          │  (C ABI,       ├─ rumble-audio (Opus, jitter, mixer, cpal)
Unity / Unreal bridges ───────────────────┘   rumble.h)    └─ rumble-protocol (no I/O: framing, voice, OCB2)
```

- **JSON across the ABI.** Config, commands, events and snapshots cross the boundary as camelCase JSON. The Rust types are `command.rs`, `event.rs` and `state.rs` with serde. The .NET mirrors are `Interop/RumbleJsonContext.cs` for commands and config and `Events/RumbleEvents.cs` for events, both source-generated. **When you add a command or event, change both sides** and extend `ModelAndJsonTests`.
- **Event flow.** The native callback parses events on the native thread and writes them into a channel. A single .NET event loop applies each one to `ServerModel` and then raises the typed event. Request/response methods (for example `JoinChannelAsync`) wait for the confirming event and fail on `PermissionDenied` or a disconnect.
- **Audio threading.** Network → `VoiceRouter` (SPSC ring buffers) → `Mixer` on the audio thread, which is either the cpal callback or, in headless mode, a 10 ms clock thread. The audio thread must stay lock-free and allocation-free. Capture drives `CapturePipeline` directly, and encoded packets reach the session with `try_send`.
- **UDP formats.** The protobuf format is used when the server version is 1.5 or newer, and the legacy format otherwise. Decoding falls back to the other format. Voice goes through the TCP tunnel until a UDP ping round-trip succeeds.
- **Mock server.** `rumble-mock-server` is a real-protocol server with EchoBot, which echoes text and voice in the Lobby (channel 1). The Rust integration tests, the .NET tests, RumbleGallery and the RumbleApp demo all use it. .NET code reaches it through `Rumble.Net.Testing.MockMumbleServer` over FFI.
- **Callback lifetime.** `rumble_client_destroy` guarantees that no callbacks run after it returns, which is why `RumbleClientSafeHandle.ReleaseHandle` can free the `GCHandle` right after calling it.
- **Panics.** The release profile keeps `panic = "unwind"` on purpose, because FFI entry points turn panics into `RUMBLE_ERR_PANIC` with `catch_unwind`.

## Samples

- **RumbleApp** (MAUI Blazor Hybrid). `Services/VoiceSession.cs` is the app-wide wrapper around `RumbleClient`. The design tokens live in `wwwroot/css/app.css` ("broadcast console": slate panels, amber = on air, teal = controls; Chakra Petch, IBM Plex Sans and Plex Mono are bundled). Use the project `frontend-design` skill for UI work. Components without parameters must subscribe to `VoiceSession.Changed` and `Tick` themselves, because a parent re-render does not refresh them.
- **RumbleGallery** (Avalonia 12). A sample is a static class in `Samples/` with `[Sample(...)]` and `public static Task RunAsync(SampleContext)`. Its source is embedded and shown as-is, so keep sample code clean.
- **Verifying the UI.** Start RumbleApp with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333`, then drive it and take screenshots over the Chrome DevTools Protocol. UI Automation cannot see inside WebView2. The gallery exposes regular UIA controls.

## Conventions

- C# uses standard naming (`_camelCase` private fields, `s_` for private statics), file-scoped namespaces and nullable enabled. Keep P/Invoke signatures blittable (`LibraryImport`, `SafeHandle`).
- Rust uses edition 2024. Every `unsafe` block needs a `// SAFETY:` comment. Keep `rumble-protocol` free of I/O.
- Bot and chat text is HTML on the wire. Encode outgoing text (`WebUtility.HtmlEncode`) and read `TextMessage.PlainText`.
- Credit "Made by Gravicode Studios, led by Kang Fadhil" in apps and docs. The README exists in English (`README.md`) and Indonesian (`README.id.md`), so keep both in sync.
- Documentation lives in `docs/`, with screenshots in `docs/images/`. Take screenshots against the demo server, never a real server with personal user names.
- The NuGet API key is in `C:\Users\mifma\Documents\CodeSandbox\PackageCredentials.txt`, outside the repo. `build/publish-nuget.ps1` reads it. Never copy it into the repo, docs or commits, and publish only when the user asks.
