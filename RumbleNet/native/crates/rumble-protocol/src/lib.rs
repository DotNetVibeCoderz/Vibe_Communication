//! # rumble-protocol
//!
//! Pure, allocation-conscious implementation of the Mumble wire protocol:
//!
//! * [`control`]: TCP/TLS control channel framing (`type:u16 | length:u32 | protobuf`).
//! * [`voice`]: UDP voice/ping packets in both the legacy (< 1.5) binary format and the
//!   protobuf-based format introduced in Mumble 1.5.
//! * [`crypt`]: OCB2-AES128 crypt state used to protect UDP datagrams (with the
//!   XEX* counter-cryptanalysis countermeasures used by upstream Mumble).
//! * [`varint`]: Mumble's variable length integer encoding (`PacketDataStream`).
//! * [`version`]: version number helpers for the v1 and v2 formats.
//!
//! The crate is `no-IO`: it never touches sockets, making it trivial to test and benchmark.

pub mod control;
pub mod crypt;
pub mod varint;
pub mod version;
pub mod voice;

/// Generated protobuf types for the TCP control channel (`Mumble.proto`).
#[allow(clippy::all, missing_docs)]
pub mod proto {
    include!(concat!(env!("OUT_DIR"), "/mumble_proto.rs"));
}

/// Generated protobuf types for the Mumble >= 1.5 UDP channel (`MumbleUDP.proto`).
#[allow(clippy::all, missing_docs)]
pub mod udp_proto {
    include!(concat!(env!("OUT_DIR"), "/mumble_udp.rs"));
}

/// Errors produced while encoding or decoding protocol data.
#[derive(Debug, thiserror::Error)]
pub enum ProtocolError {
    #[error("buffer too short")]
    Truncated,
    #[error("invalid varint encoding")]
    InvalidVarint,
    #[error("unknown control message type {0}")]
    UnknownMessageType(u16),
    #[error("control message too large ({0} bytes)")]
    MessageTooLarge(usize),
    #[error("protobuf decode error: {0}")]
    Decode(#[from] prost::DecodeError),
    #[error("unsupported voice codec {0}")]
    UnsupportedCodec(u8),
    #[error("malformed voice packet")]
    MalformedVoicePacket,
}

pub type Result<T> = std::result::Result<T, ProtocolError>;
