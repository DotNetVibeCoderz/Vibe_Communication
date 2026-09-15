# Building and packaging

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0.400+ (pinned in `global.json`) | Everything .NET |
| Rust | 1.85+ via `rustup` | The native core |
| MSVC build tools (Windows), Xcode CLT (macOS), `build-essential` + `libasound2-dev` (Linux) | | Linking `rumble_native` |
| MAUI workloads | `dotnet workload install maui` | RumbleApp only |
| Android NDK / Xcode | | Mobile native builds |

You don't need `protoc`, CMake or OpenSSL. Protobufs are compiled by `protox` and Opus is pure Rust.

## Native core

Building `src/Rumble.Net` runs `cargo build --release -p rumble-ffi` automatically (incrementally) for the host. It then copies the result to `src/Rumble.Net/runtimes/{host-rid}/native/` and into the output of every project that references the SDK. To use prebuilt binaries, pass `-p:RumbleSkipNativeBuild=true`.

To cross-build:

```powershell
./build/build-native.ps1 -Rids win-x64,win-arm64            # Windows
./build/build-native.sh linux-x64 linux-arm64               # Linux
./build/build-native.sh osx-arm64 osx-x64                   # macOS
./build/build-native.ps1 -Rids win-x64 -NoMockServer        # smaller library without the embedded test server
```

Release profile: `opt-level=3`, fat LTO, `codegen-units=1`, stripped symbols. Panics unwind so the FFI layer can catch them and turn them into error codes.

## NuGet

```powershell
./build/pack.ps1 -Rids win-x64            # runs cargo + dotnet tests, then packs into ./artifacts
```

Two packages are produced:

- **Rumble.Net** contains `lib/net10.0` (with XML docs), `runtimes/{rid}/native/*` for every staged RID, `native/include/rumble.h` and the README, plus a `.snupkg` symbols package.
- **Rumble.Net.Bots** contains the bot framework.

For a release that covers every platform, use the **CI workflow** (`.github/workflows/rumblenet-ci.yml` at the root of the Vibe_Communication repository). It builds the native library on Windows (x64 and arm64), Linux (x64 and arm64) and macOS (x64 and arm64), runs the tests on all three operating systems, stages every RID, and uploads the `nuget` artifact.

### Publishing from CI (recommended)

The workflow publishes to nuget.org with the `NUGET_API_KEY` repository secret. It only publishes after the following succeed:

- the native builds for all 6 RIDs
- the .NET tests on Windows, Linux and macOS
- the gallery smoke run
- a check that the package contains all 6 native binaries

There are two ways to trigger a release:

```bash
# 1. Tag a release. The version comes from the tag.
git tag rumblenet-v0.1.0-preview.2
git push origin rumblenet-v0.1.0-preview.2

# 2. Run the workflow manually with publish = true. The version comes from Directory.Build.props.
gh workflow run rumblenet-ci.yml -f publish=true
```

Pushes to `main` and pull requests build, test and pack, but never publish.

### Publishing locally

This publishes only the native binaries staged on your machine.

```powershell
./build/publish-nuget.ps1 -CredentialsFile C:\path\outside\repo\PackageCredentials.txt
```

The script reads the API key from a file that is **outside the repository** and pushes the packages with `--skip-duplicate`. Never commit keys. `.gitignore` excludes `PackageCredentials.txt`.

## Versioning

- The .NET version is set in `Directory.Build.props` (`<Version>`).
- The Rust version is set in `native/Cargo.toml` (`[workspace.package] version`).

Keep the two in sync when you release.
