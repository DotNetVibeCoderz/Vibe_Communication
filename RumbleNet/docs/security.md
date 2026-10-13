# Security

## Transport

- **Control channel.** TLS 1.2/1.3 via **rustls** (ring crypto provider), with no OpenSSL anywhere in the stack.
- **Voice channel.** UDP datagrams are protected with **OCB2-AES128**, which is byte-compatible with Mumble's `CryptStateOCB2`. The implementation includes:
  - replay protection, using a 256-entry decrypt history
  - late and lost packet accounting
  - nonce resync
  - the countermeasures against the XEX\* attack (eprint 2019/311, section 9)

  It is verified against the OCB2 test vectors and uses AES-NI when the CPU has it.

  One consequence of the countermeasure is worth knowing: when a packet is digital silence, the
  sender flips the lowest bit of one sample block rather than refusing to send, exactly as upstream
  Mumble does. The receiver therefore gets the payload back with that single bit changed, which is
  inaudible. `digital_silence_is_delivered_with_a_single_flipped_bit` pins this behavior down.

  The sender's rewrite rule and the receiver's rejection rule do not cover exactly the same inputs,
  so a rare payload is encrypted happily and then dropped on arrival. Upstream Mumble behaves the
  same way, and for voice it costs a single packet, which the jitter buffer conceals. The fuzzer
  found such a payload; `payload_matching_the_attack_pattern_is_dropped_by_the_receiver` keeps the
  behavior documented, including that the session carries on normally afterwards.
- **Fallback.** When UDP is blocked, voice travels inside the TLS control channel (`UDPTunnel`). Set `ForceTcpVoice = true` to always use the tunnel.

A note on the spec: Mumble itself does **not** use DTLS. Its voice security is the OCB2 scheme above, keyed through the TLS channel with `CryptSetup`. Rumble.Net implements that scheme so it stays interoperable with every Mumble server.

## Server verification

Most Mumble servers use self-signed certificates, so there are three modes:

| `TlsVerification` | Behavior |
|---|---|
| `AcceptAll` (default) | Accepts any certificate. `ServerCertificateReceived` reports its SHA-1 and SHA-256 fingerprints so you can apply trust-on-first-use. |
| `Pinned` | Accepts only the certificate whose SHA-1 fingerprint matches `PinnedFingerprint`. Colons and letter case are ignored. |
| `WebPki` | Standard validation against the Mozilla root store, for servers with CA-issued certificates. |

A recommended trust-on-first-use flow:

```csharp
client.ServerCertificateReceived += (_, cert) =>
{
    var known = store.GetFingerprint(host);
    if (known is null) store.Save(host, cert.Sha1Fingerprint);
    else if (known != cert.Sha1Fingerprint) _ = client.DisconnectAsync(); // warn the user: the certificate changed
};
// …and on later connections: options.TlsVerification = TlsVerification.Pinned; options.PinnedFingerprint = known;
```

A certificate mismatch is fatal and is never retried.

## Authentication

| Method | How |
|---|---|
| Password | `Password` holds the server password or a registered account password. |
| Certificate | `CertificatePem` and `PrivateKeyPem` hold a PKCS#8, SEC1 or PKCS#1 key. The server identifies registered users by the certificate's SHA-1 hash, which you get from `RumbleNative.GenerateCertificate("name")`. Call `RegisterSelf()` to register. |
| Tokens | `Tokens` holds access tokens that grant membership in ACL groups. |

Store private keys and passwords the way your platform expects. RumbleApp does this through `SecretStore`: MAUI `SecureStorage` (Keychain on Apple platforms, Keystore on Android, DPAPI on Windows), falling back to a DPAPI-encrypted file on Windows configurations where SecureStorage is unavailable. Its `servers.json` and `settings.json` hold no passwords and no private key; secrets written by older versions are migrated on first start.

## Memory safety

The protocol parsers, the crypto code and the audio pipeline are safe Rust. `unsafe` appears only in:

- the Opus bindings, which wrap pointer APIs of the Rust libopus translation
- the FFI boundary, where every entry point checks for null pointers, validates lengths and UTF-8, and converts panics into `RUMBLE_ERR_PANIC` with `catch_unwind`

Control frames are capped at 8 MiB, and malformed voice packets are dropped.
