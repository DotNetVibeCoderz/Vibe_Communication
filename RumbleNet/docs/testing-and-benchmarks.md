# Testing and benchmarks

## Test suites

| Suite | Command | Covers |
|---|---|---|
| Rust unit and integration tests | `cd native; cargo test --workspace` | varints, versions, control framing, voice packet formats, OCB2 test vectors, replay/loss/reorder handling, Opus round trips, jitter buffer, mixer, capture pipeline, positional gains, DSP, resamplers, state cache, JSON shapes, TLS identity, and **end-to-end** client tests against the mock server (chat, voice echo over UDP, TCP tunnel, reconnect, password rejection, server query) |
| .NET tests (xUnit v3) | `dotnet test --project tests/Rumble.Net.Tests/Rumble.Net.Tests.csproj` | JSON contract with the native core, LINQ model, options validation, Opus via P/Invoke, filters, WAV, benchmarks entry point, and end-to-end tests (connect, chat, create/remove channel, stats, voice frames and capture filters, reconnect, rejection, unreachable server, query, command bot, DI) |
| Gallery smoke test | `dotnet run --project samples/RumbleGallery -- --run-all` | Every documented sample runs against the mock server |

Run a single test:

```powershell
cd native; cargo test -p rumble-protocol crypt::tests::ocb2_test_vectors
dotnet test --project tests/Rumble.Net.Tests/Rumble.Net.Tests.csproj -- --filter-method "*Reconnects*"
```

The .NET suite uses Microsoft.Testing.Platform (configured in `global.json`), so filters go after `--`.

## Mock server

`MockMumbleServer` (.NET) and `rumble_mock_server::MockServer` (Rust) implement:

- TLS login, including password rejection and duplicate-name rejection
- CryptSetup and OCB2 UDP with ping echo, plus TCP tunnel relay
- channel create, update and remove
- moves, text, kicks, stats, permissions and plugin data
- the legacy server-info ping

**EchoBot** (session 1) sits in the Lobby (channel 1). It replies `echo: <text>` and plays back voice from users in its channel.

You can also run it standalone and point any Mumble client at it:

```powershell
cd native; cargo run -p rumble-mock-server -- 127.0.0.1:64738 [password]
```

## Benchmarks

### Rust (Criterion)

```powershell
cd native
cargo bench -p rumble-protocol      # crypt, varint, voice packets, control decode
cargo bench -p rumble-audio         # opus, jitter buffer, mixer (1/8/32 speakers), positional gains
```

### .NET (BenchmarkDotNet)

```powershell
dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- --filter *   # event parsing, AudioMath, Opus interop, client ops
dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- native       # core micro benchmarks via FFI
dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- latency 50   # end-to-end voice latency
```

The latency probe measures mouth-to-ear time on localhost. The clock starts at the first captured sample of a tone burst and stops when the decoded echo reaches the frame callback. The path in between covers:

- Opus look-ahead
- encryption
- UDP and the server relay
- the jitter buffer playout delay
- the 10 ms mixer clock

### Reference results

Measured on 2026-09-13 on Windows 11 x64 in Release. The Criterion runs used `--quick`, so treat them as indicative.

| Benchmark | Result |
|---|---|
| End-to-end latency (20 rounds, UDP) | min 22.3 · **p50 38.4 · p95 42.7** · max 43.8 ms. **PASS** (< 50 ms) |
| OCB2-AES128 encrypt+decrypt, 60 / 120 / 480 B | 426 ns / 655 ns / 3.09 µs |
| Voice packet encode+decode, legacy / protobuf | 153 ns / 435 ns |
| Decode 100 `UserState` control frames | 26 µs |
| Opus encode / decode / conceal, 10 ms | 269 µs / 32 µs / 32 µs |
| Jitter buffer, 1000 insert+pop with reordering | 64 µs (64 ns per packet) |
| Mixer per 10 ms frame, 1 / 8 / 32 speakers | 41 µs / 262 µs / 1.05 ms (0.4 % / 2.6 % / 10.5 % of the frame budget) |
| Positional gains, 256 sources | 5.2 µs |

CPU usage: a 32-speaker mix uses about 10 % of one core. Opus encoding dominates the capture side at about 2.7 % of one core with complexity 8.

## Live server check

The SDK and RumbleApp were also tested on 2026-09-15 against a production Murmur 1.5.0 server with a password and 83 channels:

- TLS login and full synchronization
- encrypted UDP with a 10 ms ping
- the channel tree rendered in RumbleApp, including another connected user
- **server-loopback voice**: a 1.5 s tone sent with voice target 31 came back as 151 decoded frames (1.51 s, including the terminator)
