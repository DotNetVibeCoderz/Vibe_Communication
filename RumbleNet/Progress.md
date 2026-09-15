# Progress

The roadmap is in [PLAN.md](PLAN.md). This file records what exists and how it was verified.

**Legend:**

- ✅ done and verified: automated test or observed run
- 🟡 implemented but not verified on a real target
- ⏳ not started

## Snapshot: 2026-09-15 (v0.1.0)

| Area | Status | Evidence |
|---|---|---|
| Rust workspace (5 crates) | ✅ | `cargo test --workspace`: protocol 24, audio 18, client 8 unit + 5 end-to-end, FFI 3, all passing |
| .NET SDK and Bots | ✅ | `dotnet test`: 20 of 20 passing (JSON contract, model, codec, filters, end-to-end voice, reconnect, bots, DI) |
| RumbleGallery | ✅ | `--run-all`: 20 of 20 samples pass. The UI was launched and a sample run was captured (`docs/images/rumblegallery.png`). |
| RumbleApp (Windows) | ✅ | Built for `net10.0-windows10.0.19041.0`. Driven through WebView2 DevTools: added a server, connected, joined a channel, chatted with EchoBot, sent microphone voice with echo playback, and checked the desktop and 400 px layouts. |
| **Live server** | ✅ | Tested against a production Murmur 1.5.0 server with a password and 83 channels. TLS login, sync, encrypted UDP (10 ms ping), and server-loopback voice (151 frames decoded) all worked through both the SDK and RumbleApp. |
| Benchmarks | ✅ | End-to-end latency p95 42.7 ms (< 50 ms target). Criterion and FFI numbers are in `docs/testing-and-benchmarks.md`. |
| Documentation | ✅ | `docs/` (12 guides with screenshots), README in English and Indonesian, THIRD-PARTY-NOTICES |
| CI workflow | ✅ | `.github/workflows/rumblenet-ci.yml`: run 34973752150 was green. It covers 6 native builds (win, linux and osx, each x64 and arm64), .NET tests and all gallery samples on Windows, Linux and macOS, and a pack step that checks all 6 native binaries are included. Pushing a `rumblenet-v*` tag also publishes with the `NUGET_API_KEY` secret. |
| Linux and macOS | ✅ (CI) | Native builds, Rust tests (linux-x64, osx-arm64), .NET tests and gallery samples pass on GitHub runners |
| NuGet packages | ✅ (preview) | **0.1.0-preview.2** was released by CI with all 6 desktop native binaries. 0.1.0-preview.1 was published locally with win-x64 only; before publishing it was installed into a fresh app from a local feed and tested there. |
| RumbleApp Android, iOS, macOS | 🟡 | Project, permissions and native library hooks are in place. The mobile native builds and on-device runs haven't happened yet. |
| Unity bridge | 🟡 | UPM package written against `rumble.h`, not yet compiled in Unity |
| Unreal bridge | 🟡 | UE plugin written against `rumble.h`, not yet compiled in Unreal |
| CELT/Speex decoding | ⏳ (non-goal) | Frames are parsed and concealed. Opus is required by all modern servers. |
| DTLS | n/a | Mumble uses OCB2-AES over UDP keyed through TLS, not DTLS. That scheme is implemented. See `docs/security.md`. |

## Log

### 2026-09-15 (CI release)
- CI publish job added (tag `rumblenet-v*` or manual run with `publish=true`, using the `NUGET_API_KEY` secret).
- osx-x64 moved to an Apple silicon runner (macos-13 jobs stalled in the queue).
- Fixed a race in the gallery event-stream sample that failed on all CI operating systems.
- Run 34973752150 was green. Tagged `rumblenet-v0.1.0-preview.2`.
- Removed the live test server entry from the local RumbleApp data.

### 2026-09-15 (release)
- Moved the solution into the `Vibe_Communication` repository under `RumbleNet/`. CI moved to `.github/workflows/rumblenet-ci.yml` at the repository root.
- Packaging fixes found by inspecting the `.nupkg`:
  - the native DLL was missing (SDK default `None` item and the build-copy `Content` item conflicted)
  - the native path came out doubled
- README images now use absolute URLs so they render on nuget.org.
- `build-native.ps1` and `pack.ps1` no longer treat cargo's stderr progress as an error in Windows PowerShell.
- Published `0.1.0-preview.1` (win-x64 native).

### 2026-09-15
- Live test against a Murmur 1.5.0 server through the SDK probe and RumbleApp. Login, 83-channel sync, UDP and loopback voice all pass.
- RumbleApp fixes found through screenshots:
  - chat and transmit strip weren't re-rendering, so incoming messages and the meter were stale
  - oversized chat icon
  - PTT label layout and wording
  - mobile header overflow
  - settings grid alignment and slider color
  - the Windows publisher placeholder (the app data folder is now `Gravicode Studios`)
- Bot usage replies are now HTML-encoded, so `<sides>` no longer disappears. A test was added.
- Added the gallery `--run-all` mode, the CI step, screenshots in the docs and README, PLAN.md and Progress.md.

### 2026-09-13
- Rust core: protocol, crypt, audio pipeline, client session/reconnect, FFI, mock server
- .NET SDK, Bots, tests and benchmarks. Latency probe: p95 42.7 ms.
- RumbleApp (MAUI Blazor Hybrid, broadcast-console design) and RumbleGallery (Avalonia, 20 samples)
- Unity and Unreal bridges, build and pack scripts, CI workflow, docs

## Known issues

- Audio device selection uses cpal's default stream configuration. Exclusive-mode WASAPI and custom buffer sizes aren't exposed yet.
- Voice sent in the first seconds after connecting goes through the TCP tunnel until the first UDP ping round-trip succeeds, matching Mumble's behavior. With `PingInterval` = 5 s, that can take up to about 5 s.
- `ServerInfo.Release` and `Os` are empty on servers that hide their version details.
- RumbleApp stores saved passwords and the certificate key in plain JSON under its app data folder. Moving them to platform secure storage is planned under M6.
