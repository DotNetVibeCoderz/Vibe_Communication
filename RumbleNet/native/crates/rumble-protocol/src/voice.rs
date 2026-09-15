//! UDP voice channel packets.
//!
//! Two wire formats exist:
//!
//! * **Legacy** (all Mumble versions < 1.5): `header = codec << 5 | target`, followed by varints.
//! * **Protobuf** (Mumble >= 1.5): `header = 0 (audio) | 1 (ping)`, followed by a `MumbleUDP` message.
//!
//! The format to use is negotiated from the server `Version` message; decoding auto-detects
//! based on the [`VoiceFormat`] passed in.

use bytes::Bytes;
use prost::Message as _;

use crate::udp_proto;
use crate::varint::{self, Reader};
use crate::{ProtocolError, Result};

/// Audio codecs known by the Mumble protocol.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Codec {
    CeltAlpha = 0,
    Speex = 2,
    CeltBeta = 3,
    Opus = 4,
}

impl Codec {
    fn from_legacy(t: u8) -> Result<Self> {
        match t {
            0 => Ok(Codec::CeltAlpha),
            2 => Ok(Codec::Speex),
            3 => Ok(Codec::CeltBeta),
            4 => Ok(Codec::Opus),
            other => Err(ProtocolError::UnsupportedCodec(other)),
        }
    }
}

/// Which UDP packet format is in use.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum VoiceFormat {
    #[default]
    Legacy,
    Protobuf,
}

/// Legacy header type for UDP pings.
const LEGACY_PING: u8 = 1;
/// Protobuf-format header bytes.
const PROTOBUF_AUDIO: u8 = 0;
const PROTOBUF_PING: u8 = 1;

/// Opus terminator bit in the legacy size varint.
const OPUS_TERMINATOR: u64 = 0x2000;
const OPUS_LENGTH_MASK: u64 = 0x1FFF;

/// Target id for "server loopback" (echo own voice back).
pub const TARGET_SERVER_LOOPBACK: u8 = 31;

/// Context of received audio (protobuf format).
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum AudioContext {
    #[default]
    Normal = 0,
    Shout = 1,
    Whisper = 2,
    Listen = 3,
}

impl From<u32> for AudioContext {
    fn from(v: u32) -> Self {
        match v {
            1 => AudioContext::Shout,
            2 => AudioContext::Whisper,
            3 => AudioContext::Listen,
            _ => AudioContext::Normal,
        }
    }
}

/// A voice packet in a format-agnostic representation.
#[derive(Debug, Clone, PartialEq)]
pub struct VoicePacket {
    pub codec: Codec,
    /// Outgoing: voice target (0 = normal talking). Incoming: [`AudioContext`] value.
    pub target_or_context: u8,
    /// Sender session; `None` for client→server packets in the legacy format.
    pub session: Option<u32>,
    pub sequence: u64,
    /// Codec payload. For Opus this is exactly one Opus packet; for CELT/Speex it holds
    /// the length-prefixed frame list verbatim.
    pub payload: Bytes,
    pub position: Option<[f32; 3]>,
    pub is_terminator: bool,
    /// Server-side volume adjustment (protobuf format only, 0 = unset).
    pub volume_adjustment: f32,
}

impl VoicePacket {
    /// Builds an outgoing Opus packet.
    pub fn opus(target: u8, sequence: u64, payload: Bytes, is_terminator: bool) -> Self {
        Self {
            codec: Codec::Opus,
            target_or_context: target,
            session: None,
            sequence,
            payload,
            position: None,
            is_terminator,
            volume_adjustment: 0.0,
        }
    }

    pub fn context(&self) -> AudioContext {
        AudioContext::from(self.target_or_context as u32)
    }
}

/// UDP ping payload.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct PingPacket {
    pub timestamp: u64,
    pub request_extended_information: bool,
    pub server_version_v2: u64,
    pub user_count: u32,
    pub max_user_count: u32,
    pub max_bandwidth_per_user: u32,
}

/// A decoded UDP datagram.
#[derive(Debug, Clone, PartialEq)]
pub enum UdpPacket {
    Voice(VoicePacket),
    Ping(PingPacket),
}

/// Encodes a voice packet into `out` (cleared first). `include_session` selects the
/// server→client legacy layout.
pub fn encode_voice(packet: &VoicePacket, format: VoiceFormat, include_session: bool, out: &mut Vec<u8>) {
    out.clear();
    match format {
        VoiceFormat::Legacy => {
            out.push(((packet.codec as u8) << 5) | (packet.target_or_context & 0x1F));
            if include_session {
                varint::write(out, packet.session.unwrap_or(0) as u64);
            }
            varint::write(out, packet.sequence);
            match packet.codec {
                Codec::Opus => {
                    let mut header = packet.payload.len() as u64 & OPUS_LENGTH_MASK;
                    if packet.is_terminator {
                        header |= OPUS_TERMINATOR;
                    }
                    varint::write(out, header);
                    out.extend_from_slice(&packet.payload);
                }
                _ => out.extend_from_slice(&packet.payload),
            }
            if let Some(pos) = packet.position {
                for f in pos {
                    out.extend_from_slice(&f.to_le_bytes());
                }
            }
        }
        VoiceFormat::Protobuf => {
            let msg = udp_proto::Audio {
                header: Some(if include_session {
                    udp_proto::audio::Header::Context(packet.target_or_context as u32)
                } else {
                    udp_proto::audio::Header::Target(packet.target_or_context as u32)
                }),
                sender_session: packet.session.unwrap_or(0),
                frame_number: packet.sequence,
                opus_data: packet.payload.clone(),
                positional_data: packet.position.map(|p| p.to_vec()).unwrap_or_default(),
                volume_adjustment: packet.volume_adjustment,
                is_terminator: packet.is_terminator,
            };
            out.reserve(1 + msg.encoded_len());
            out.push(PROTOBUF_AUDIO);
            msg.encode_raw(out);
        }
    }
}

/// Encodes a UDP ping.
pub fn encode_ping(ping: &PingPacket, format: VoiceFormat, out: &mut Vec<u8>) {
    out.clear();
    match format {
        VoiceFormat::Legacy => {
            out.push(LEGACY_PING << 5);
            varint::write(out, ping.timestamp);
        }
        VoiceFormat::Protobuf => {
            let msg = udp_proto::Ping {
                timestamp: ping.timestamp,
                request_extended_information: ping.request_extended_information,
                server_version_v2: ping.server_version_v2,
                user_count: ping.user_count,
                max_user_count: ping.max_user_count,
                max_bandwidth_per_user: ping.max_bandwidth_per_user,
            };
            out.push(PROTOBUF_PING);
            msg.encode_raw(out);
        }
    }
}

/// Decodes a (decrypted) UDP datagram. `from_server` selects whether a session id is present
/// in the legacy layout.
pub fn decode_udp(data: &[u8], format: VoiceFormat, from_server: bool) -> Result<UdpPacket> {
    let header = *data.first().ok_or(ProtocolError::Truncated)?;
    match format {
        VoiceFormat::Protobuf => {
            let body = &data[1..];
            match header {
                PROTOBUF_AUDIO => {
                    let msg = udp_proto::Audio::decode(body)?;
                    let target_or_context = match msg.header {
                        Some(udp_proto::audio::Header::Target(t)) => t,
                        Some(udp_proto::audio::Header::Context(c)) => c,
                        None => 0,
                    } as u8;
                    let position = (msg.positional_data.len() >= 3)
                        .then(|| [msg.positional_data[0], msg.positional_data[1], msg.positional_data[2]]);
                    Ok(UdpPacket::Voice(VoicePacket {
                        codec: Codec::Opus,
                        target_or_context,
                        session: from_server.then_some(msg.sender_session),
                        sequence: msg.frame_number,
                        payload: msg.opus_data,
                        position,
                        is_terminator: msg.is_terminator,
                        volume_adjustment: msg.volume_adjustment,
                    }))
                }
                PROTOBUF_PING => {
                    let msg = udp_proto::Ping::decode(body)?;
                    Ok(UdpPacket::Ping(PingPacket {
                        timestamp: msg.timestamp,
                        request_extended_information: msg.request_extended_information,
                        server_version_v2: msg.server_version_v2,
                        user_count: msg.user_count,
                        max_user_count: msg.max_user_count,
                        max_bandwidth_per_user: msg.max_bandwidth_per_user,
                    }))
                }
                _ => Err(ProtocolError::MalformedVoicePacket),
            }
        }
        VoiceFormat::Legacy => {
            let kind = header >> 5;
            let target = header & 0x1F;
            let mut r = Reader::new(&data[1..]);
            if kind == LEGACY_PING {
                return Ok(UdpPacket::Ping(PingPacket { timestamp: r.varint()?, ..Default::default() }));
            }
            let codec = Codec::from_legacy(kind)?;
            let session = if from_server { Some(r.varint()? as u32) } else { None };
            let sequence = r.varint()?;
            let (payload, is_terminator) = match codec {
                Codec::Opus => {
                    let size = r.varint()?;
                    let len = (size & OPUS_LENGTH_MASK) as usize;
                    (r.bytes(len)?, size & OPUS_TERMINATOR != 0)
                }
                _ => {
                    // CELT/Speex: sequence of frames, each prefixed with `continuation << 7 | len`.
                    let start = r.remaining();
                    let mut consumed = 0;
                    let mut terminator = false;
                    loop {
                        let h = *r.bytes(1)?.first().unwrap();
                        let len = (h & 0x7F) as usize;
                        r.bytes(len)?;
                        consumed += 1 + len;
                        if len == 0 {
                            terminator = true;
                        }
                        if h & 0x80 == 0 {
                            break;
                        }
                    }
                    (&start[..consumed], terminator)
                }
            };
            let rest = r.remaining();
            let position = (rest.len() >= 12).then(|| {
                let f = |i: usize| f32::from_le_bytes([rest[i], rest[i + 1], rest[i + 2], rest[i + 3]]);
                [f(0), f(4), f(8)]
            });
            Ok(UdpPacket::Voice(VoicePacket {
                codec,
                target_or_context: target,
                session,
                sequence,
                payload: Bytes::copy_from_slice(payload),
                position,
                is_terminator,
                volume_adjustment: 0.0,
            }))
        }
    }
}

/// Builds the 12-byte legacy "server info" ping sent to an arbitrary server without connecting.
pub fn encode_server_info_request(ident: u64) -> [u8; 12] {
    let mut buf = [0u8; 12];
    buf[4..].copy_from_slice(&ident.to_be_bytes());
    buf
}

/// Response to [`encode_server_info_request`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ServerInfo {
    pub version: crate::version::Version,
    pub ident: u64,
    pub users: u32,
    pub max_users: u32,
    pub max_bandwidth: u32,
}

/// Parses the 24-byte legacy server info response.
pub fn decode_server_info_response(data: &[u8]) -> Result<ServerInfo> {
    if data.len() < 24 {
        return Err(ProtocolError::Truncated);
    }
    let u32_at = |i: usize| u32::from_be_bytes([data[i], data[i + 1], data[i + 2], data[i + 3]]);
    let mut ident = [0u8; 8];
    ident.copy_from_slice(&data[4..12]);
    Ok(ServerInfo {
        version: crate::version::Version::from_v1(u32_at(0)),
        ident: u64::from_be_bytes(ident),
        users: u32_at(12),
        max_users: u32_at(16),
        max_bandwidth: u32_at(20),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample(position: Option<[f32; 3]>) -> VoicePacket {
        VoicePacket {
            codec: Codec::Opus,
            target_or_context: 2,
            session: Some(17),
            sequence: 123456,
            payload: Bytes::from_static(&[1, 2, 3, 4, 5, 6, 7, 8, 9]),
            position,
            is_terminator: true,
            volume_adjustment: 0.0,
        }
    }

    #[test]
    fn legacy_roundtrip() {
        for pos in [None, Some([1.5, -2.0, 3.25])] {
            let p = sample(pos);
            let mut buf = Vec::new();
            encode_voice(&p, VoiceFormat::Legacy, true, &mut buf);
            assert_eq!(buf[0], (4 << 5) | 2);
            assert_eq!(decode_udp(&buf, VoiceFormat::Legacy, true).unwrap(), UdpPacket::Voice(p));
        }
    }

    #[test]
    fn protobuf_roundtrip() {
        let p = sample(Some([0.0, 1.0, 2.0]));
        let mut buf = Vec::new();
        encode_voice(&p, VoiceFormat::Protobuf, true, &mut buf);
        assert_eq!(decode_udp(&buf, VoiceFormat::Protobuf, true).unwrap(), UdpPacket::Voice(p));
    }

    #[test]
    fn client_packet_has_no_session() {
        let mut p = sample(None);
        p.session = None;
        let mut buf = Vec::new();
        encode_voice(&p, VoiceFormat::Legacy, false, &mut buf);
        assert_eq!(decode_udp(&buf, VoiceFormat::Legacy, false).unwrap(), UdpPacket::Voice(p));
    }

    #[test]
    fn ping_roundtrip() {
        let ping = PingPacket { timestamp: 987654321, ..Default::default() };
        let mut buf = Vec::new();
        for f in [VoiceFormat::Legacy, VoiceFormat::Protobuf] {
            encode_ping(&ping, f, &mut buf);
            assert_eq!(decode_udp(&buf, f, true).unwrap(), UdpPacket::Ping(ping));
        }
    }

    #[test]
    fn legacy_celt_frames() {
        let data = [0u8 << 5, 5, 10, 0x83, 1, 2, 3, 0x02, 9, 9];
        match decode_udp(&data, VoiceFormat::Legacy, true).unwrap() {
            UdpPacket::Voice(v) => {
                assert_eq!(v.codec, Codec::CeltAlpha);
                assert_eq!(v.session, Some(5));
                assert_eq!(v.sequence, 10);
                assert_eq!(&v.payload[..], &[0x83, 1, 2, 3, 0x02, 9, 9]);
            }
            _ => panic!(),
        }
    }

    #[test]
    fn server_info() {
        let mut resp = [0u8; 24];
        resp[0..4].copy_from_slice(&0x010500u32.to_be_bytes());
        resp[4..12].copy_from_slice(&42u64.to_be_bytes());
        resp[12..16].copy_from_slice(&3u32.to_be_bytes());
        resp[16..20].copy_from_slice(&100u32.to_be_bytes());
        resp[20..24].copy_from_slice(&72000u32.to_be_bytes());
        let info = decode_server_info_response(&resp).unwrap();
        assert_eq!(info.ident, 42);
        assert_eq!(info.users, 3);
        assert_eq!(info.version.minor, 5);
        assert_eq!(&encode_server_info_request(42)[4..], &42u64.to_be_bytes());
    }
}
