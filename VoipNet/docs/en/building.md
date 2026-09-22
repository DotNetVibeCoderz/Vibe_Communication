# Building and packaging

🇮🇩 [Bahasa Indonesia](../id/building.md) · Made by Gravicode Studios, led by Kang Fadhil

## Requirements

| Tool | Version | Used for |
| --- | --- | --- |
| .NET SDK | 10.0 (see `global.json`) | SDK, samples, tests |
| Rust | 1.80 or newer, with `cargo` | native engine |
| C toolchain | MSVC build tools (Windows), clang/gcc (Linux), Xcode CLT (macOS) | linking the native library |
| CMake | 3.x or newer | building the bundled libopus (Opus codec); `cargo build --no-default-features` skips Opus |

The CI workflow also runs the benchmarks and a browser interop test: Chrome and Firefox place a real call through `samples/VoipNet.WebPhone`, and the build fails unless encrypted audio flows both ways.

## Repository layout

```
native/voipnet-core     Rust engine (cdylib + rlib), unit and loopback tests
src/                    VoipNet.Core, .Audio, .AI, .Enterprise
tools/                  VoipNet.Cli (voipnet), VoipNet.DocShots
samples/                Softphone, Gallery (Avalonia) · CallCenter, IvrStudio, WebPhone (Blazor) · RealtimeAgent
tests/VoipNet.Tests     xUnit v3 end-to-end tests over loopback
docs/en, docs/id        documentation
build/                  build.ps1, build.sh
```

## Build everything

```powershell
./build/build.ps1                       # engine, solution, all tests
./build/build.ps1 -SkipTests
./build/build.ps1 -Pack                 # NuGet packages in artifacts/packages
```

```bash
./build/build.sh --pack                 # Linux / macOS
```

## Step by step

```bash
cd native
cargo build --release                   # produces target/release/voipnet_core.{dll,so,dylib}
cargo test --lib                        # 53 engine tests incl. real calls over loopback
cd ..
dotnet build Voip.Net.slnx -c Release
dotnet run --project tests/VoipNet.Tests -c Release    # xUnit v3 test host
```

`VoipNet.Core` copies the freshly built library next to every project's output, so samples and tests run straight from the repository.

> On the .NET 10 SDK, run the xUnit v3 tests with `dotnet run --project tests/VoipNet.Tests` (the project is a self-hosting test executable).

### Tests that use real services

`LiveLlmTests` call Azure OpenAI and DeepSeek when a key file is available and are skipped otherwise. Point `VOIPNET_TEST_KEYS` at a text file with sections such as:

```
Azure OpenAI
apikey: …
endpoint: https://your-resource.openai.azure.com/
model: gpt-5-mini

DeepSeek
Deep Seek Api Key: …
Deep Seek Endpoint: https://api.deepseek.com
Model Id: deepseek-chat
```

Never commit this file; `.gitignore` excludes `testkey.txt`.

## Cross-platform native libraries

The package includes whatever native libraries exist when you pack:

| Runtime | Source |
| --- | --- |
| host platform | `native/target/release` |
| other runtimes | `artifacts/native/<rid>/native/` |

Build extra targets with rustup and let the script stage them:

```powershell
rustup target add aarch64-pc-windows-msvc
./build/build.ps1 -Targets x86_64-pc-windows-msvc,aarch64-pc-windows-msvc -Pack
```

Linux and macOS libraries are best built on those systems (or in CI runners) with `build.sh`, which stages the host library under `artifacts/native/<rid>/native`; copy the `artifacts/native` folders together before the final `dotnet pack`.

The .NET runtime finds the library through the standard `runtimes/<rid>/native` probing; `NativeMethods` also looks next to the application and in the repository's `native/target/release` folder.

## Continuous integration and publishing to NuGet

The repository workflow `.github/workflows/voipnet-ci.yml` (in [Vibe_Communication](https://github.com/DotNetVibeCoderz/Vibe_Communication)) runs on every change under `VoipNet/`:

1. **native** — builds and tests the engine for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64 and osx-arm64;
2. **dotnet** — stages the matching library into `src/VoipNet.Core/runtimes/<rid>/native`, builds the solution and runs the tests on Windows, Linux and macOS;
3. **pack** — stages all six libraries, packs the five packages and checks that `VoipNet.Core` contains exactly one `runtimes/<rid>/native/` library per runtime;
4. **publish** — pushes to nuget.org with the `NUGET_API_KEY` repository secret.

Publishing happens only for a release tag or a manual run with *publish* ticked:

```bash
git tag voipnet-v1.0.0          # the version comes from the tag
git push origin voipnet-v1.0.0
```

To publish from a workstation instead:

```bash
dotnet nuget push "artifacts/packages/*.nupkg" --api-key <key> --source https://api.nuget.org/v3/index.json --skip-duplicate
```

Keep the key outside the repository (a CI secret or a credentials file).

## Performance notes

- Release engine builds use `lto = "fat"`, `codegen-units = 1`, `panic = "abort"`.
- Audio callbacks pass a pointer into engine buffers: handlers see a `ReadOnlySpan<short>` with no allocation. Copy only what you keep.
- The receive path reuses buffers; the jitter buffer pools payload vectors; G.711 uses lookup tables; codecs run in loops the compiler vectorises.
- `ReadAudioAsync` allocates one array per frame for convenience; prefer `AudioReceived` on hot paths.

### Benchmarks

```bash
cd native && cargo bench --bench media
pwsh build/bench-report.ps1            # table, compared with benchmarks/baseline.json
pwsh build/bench-report.ps1 -Update    # adopt the current numbers as the baseline
```

CI runs the benchmarks on every build and prints the comparison in the job summary. A result more than
50% slower than the baseline is warned about rather than failed: shared runners are noisy enough that a
tighter threshold would cry wolf. Refresh the baseline deliberately, on an idle machine, when a change
is meant to move the numbers.
