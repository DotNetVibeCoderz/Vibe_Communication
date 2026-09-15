# Rumble.Net roadmap

This file is the development plan. Current status is tracked in [Progress.md](Progress.md).

## Guiding goals

1. **Protocol-faithful:** interoperate with stock Mumble 1.2.4+ and 1.5 servers and clients.
2. **Fast and memory-safe:** Rust owns every byte on the wire and in the audio path, with no allocations on hot paths.
3. **Idiomatic .NET:** async/await, events, LINQ, DI and logging, and working AOT and trimming.
4. **Honest verification:** every feature ships with a test, a sample, or both.

## Milestones

### M1: Core protocol ✅ (0.1.0)
- Control framing, the full Mumble.proto, legacy and protobuf UDP formats, varints
- OCB2-AES128 crypt state with the XEX* countermeasures, verified against test vectors
- The mock server used for end-to-end tests

### M2: Client and resilience ✅ (0.1.0)
- tokio/rustls session, state cache, events and commands
- Heartbeat, UDP↔TCP fallback, crypt resync, supervisor reconnect with backoff
- Certificates, pinning, WebPKI, password and tokens

### M3: Audio ✅ (0.1.0)
- Opus encode/decode/PLC/FEC, PCM fallback, legacy codec concealment
- Adaptive jitter buffer, lock-free mixer, positional audio, DSP, resamplers
- Capture pipeline (VAD/PTT/continuous), cpal devices, headless mode

### M4: .NET SDK ✅ (0.1.0)
- Blittable P/Invoke, SafeHandles, source-generated JSON, event loop, ServerModel with LINQ
- Audio API, capture filters, Opus codec API, WAV and tone helpers, plugins, DI, logging
- Rumble.Net.Bots: command router, hosted service, AI chat over Microsoft.Extensions.AI

### M5: Samples, docs, tooling ✅ (0.1.0)
- RumbleApp (MAUI Blazor Hybrid) and RumbleGallery (Avalonia, 20 runnable samples)
- Documentation in `docs/`, bilingual README, Criterion and BenchmarkDotNet suites, latency probe, CI

### M6: Hardening and platform reach ⏳ (0.2.0)
- [ ] Interop matrix against Murmur 1.3, 1.4 and 1.5 and Grumble in CI (Docker service containers). A first manual check against a production Murmur 1.5.0 server passed.
- [ ] Android arm64 and iOS arm64 native builds in CI, and RumbleApp running on real devices
- [ ] Compile-verify the Unity package (IL2CPP) and the Unreal plugin (UE 5.4+) in CI
- [ ] Fuzz the control and voice decoders (`cargo fuzz`) and the OCB2 decryptor
- [ ] Android foreground service and iOS audio session handling for background voice in RumbleApp
- [ ] Echo cancellation and noise suppression (for example a WebRTC APM or RNNoise port)
- [ ] Audio device hot-plug and change notifications through to .NET

### M7: Advanced features 🗓 (0.3.0)
- [ ] Client-side ACL editor models and a group/ACL write API
- [ ] Recording to Ogg/Opus without re-encoding
- [ ] Mumble link / positional plugin context and identity
- [ ] Per-speaker stream API (`IAsyncEnumerable<AudioFrame>`) for AI transcription pipelines
- [ ] Multi-server: one runtime, many clients, benchmarked at 1,000 bot sessions
- [ ] Optional QUIC/UDP telemetry dashboard sample (ASP.NET Core and Blazor)

### M8: 1.0 🗓
- [ ] API review and freeze, public API analyzers, semantic versioning
- [ ] Signed NuGet packages, SBOM, reproducible native builds
- [ ] Performance budget gates in CI: p95 latency < 50 ms, and an 8-speaker mix under 5 % of the frame budget

## Non-goals

- A Mumble *server* for production. The mock server is only for tests and demos.
- Decoding CELT and Speex. Modern servers require Opus, and CELT frames are concealed.
