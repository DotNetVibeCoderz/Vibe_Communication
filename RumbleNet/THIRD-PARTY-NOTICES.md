# Third-party notices

Rumble.Net includes or depends on the following third-party work.

| Component | Used for | License |
|---|---|---|
| Mumble protocol definitions (`native/crates/rumble-protocol/proto/*.proto`), © The Mumble Developers | Wire format of the control and UDP channels | BSD-3-Clause |
| [libopus](https://opus-codec.org) via [`unsafe-libopus`](https://crates.io/crates/unsafe-libopus) (a Rust translation) | Opus encoding and decoding | BSD-3-Clause |
| [rustls](https://github.com/rustls/rustls), [ring](https://github.com/briansmith/ring), [tokio](https://tokio.rs), [prost](https://github.com/tokio-rs/prost), [cpal](https://github.com/RustAudio/cpal), [rtrb](https://github.com/mgeier/rtrb), [aes](https://github.com/RustCrypto/block-ciphers) (RustCrypto) and other crates listed in `native/Cargo.lock` | TLS, async I/O, protobuf, audio devices, lock-free queues, AES | Apache-2.0 / MIT / ISC |
| [webpki-roots](https://github.com/rustls/webpki-roots) | Mozilla CA root store for `TlsVerification.WebPki` | MPL-2.0 (data) |
| Chakra Petch, IBM Plex Sans, IBM Plex Mono (Google Fonts) | Typography in RumbleApp and RumbleGallery | SIL Open Font License 1.1 |
| Avalonia, AvaloniaEdit, TextMateSharp, CommunityToolkit.Mvvm | RumbleGallery UI | MIT |
| .NET MAUI, Microsoft.Extensions.* | RumbleApp and SDK integrations | MIT |
| BenchmarkDotNet, Criterion.rs, xUnit | Benchmarks and tests | MIT / Apache-2.0 |

To list the licenses of every Rust dependency, run `cargo about` or `cargo license` in `native/`.
