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
src/                    VoipNet.Core, .Audio, .Video, .AI, .Enterprise
tools/                  VoipNet.Cli (voipnet), VoipNet.DocShots
samples/                Softphone, Gallery (Avalonia) · CallCenter, IvrStudio, WebPhone, Meeting (Blazor) · RealtimeAgent
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
3. **pack** — stages all six libraries, packs the six packages and checks that `VoipNet.Core` contains exactly one `runtimes/<rid>/native/` library per runtime;
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

### Nightly: the live providers

`voipnet-nightly.yml` runs the tests that talk to real services — LLMs, speech recognition and
synthesis, and a call through them — every night at 18:00 UTC, and on demand from the Actions tab.
They need keys, which the repository holds as one secret named `VOIPNET_TEST_KEYS` with exactly the
shape of the local `testkey.txt`: sections separated by blank lines, `name: value` inside each. The
job writes it to a file and points `VOIPNET_TEST_KEYS` at it.

Without the secret every live test skips itself, so the job still runs green and its summary says so
rather than pretending the providers were reached. The log is attached to the run either way.

## Containers and Kubernetes

`docker/Dockerfile` builds the Rust engine and one .NET app into a single image. Which app is a build
argument, so the same file produces the CLI, a sample or your own service:

```bash
cd VoipNet
docker build -f docker/Dockerfile -t voipnet/cli .
docker build -f docker/Dockerfile -t voipnet/meeting \
  --build-arg PROJECT=samples/VoipNet.Meeting --build-arg ENTRY=VoipNet.Meeting.dll .
```

Media is the awkward part of running SIP in a container: RTP uses a range of UDP ports, and the
addresses the engine writes into SDP have to be addresses the peer can reach. Host networking is the
straightforward answer:

```bash
docker run --rm --network host voipnet/cli sip listen --echo
docker run --rm --network host -p 8080:8080 voipnet/meeting
```

Without it, publish the signalling port and the RTP range together, and tell the engine the address
callers should use (`PublicAddress`, or `StunServer` / `TurnServer` behind NAT):

```bash
docker run --rm -p 5060:5060/udp -p 10000-10100:10000-10100/udp voipnet/cli sip listen --echo
```

The chart in `deploy/helm/voipnet` deploys one app the same way:

```bash
helm install room deploy/helm/voipnet \
  --set image.repository=voipnet/meeting \
  --set rtp.portMin=10000 --set rtp.portMax=10100
```

It runs with `hostNetwork: true` by default, because a Kubernetes Service forwards one port at a time
and media needs a range. That means one replica per node — two pods on one node would fight over the
same ports — so scale by adding nodes, or put a TURN server in front and turn host networking off.
Credentials belong in a secret: `--set envFromSecrets={voipnet-credentials}` passes one through.
