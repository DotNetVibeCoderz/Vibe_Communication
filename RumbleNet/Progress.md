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
| CI workflow | 🟡 | `.github/workflows/rumblenet-ci.yml` at the repository root (6 native targets, tests on 3 OSes, gallery run, pack). It triggers on changes under `RumbleNet/`; the first GitHub run is pending. |
| NuGet packages | ✅ (preview) | `Rumble.Net` and `Rumble.Net.Bots` **0.1.0-preview.1** on nuget.org. Before publishing, both were installed from a local feed into a new console app, which connected to the mock server and got an EchoBot reply. The native binary is included for **win-x64 only**. win-arm64 needs clang on the build machine, and Linux and macOS need the CI runners. |
| RumbleApp Android, iOS, macOS | 🟡 | Project, permissions and native library hooks are in place. The mobile native builds and on-device runs haven't happened yet. |
| Linux and macOS native | 🟡 | Build scripts and CI matrix exist. Only Windows x64 has been built locally. |
| Unity bridge | 🟡 | UPM package written against `rumble.h`, not yet compiled in Unity |
| Unreal bridge | 🟡 | UE plugin written against `rumble.h`, not yet compiled in Unreal |
| CELT/Speex decoding | ⏳ (non-goal) | Frames are parsed and concealed. Opus is required by all modern servers. |
| DTLS | n/a | Mumble uses OCB2-AES over UDP keyed through TLS, not DTLS. That scheme is implemented. See `docs/security.md`. |

## Log

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
