//! TCP control channel framing.
//!
//! Every control message is prefixed with a 6-byte header:
//! `message_type: u16 (big endian) | payload_length: u32 (big endian)` followed by the
//! protobuf-encoded payload. The [`FrameDecoder`] is incremental and works with any
//! partially-filled byte buffer, so the network layer can feed it straight from socket reads.

use bytes::{Buf, BufMut, Bytes, BytesMut};
use prost::Message as _;

use crate::proto;
use crate::{ProtocolError, Result};

/// Size of the control frame header.
pub const HEADER_LEN: usize = 6;
/// Upper bound for a single control message (Mumble servers cap at 8 MiB).
pub const MAX_PAYLOAD_LEN: usize = 8 * 1024 * 1024;

/// Control message type identifiers as defined by the Mumble protocol.
#[repr(u16)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MessageType {
    Version = 0,
    UdpTunnel = 1,
    Authenticate = 2,
    Ping = 3,
    Reject = 4,
    ServerSync = 5,
    ChannelRemove = 6,
    ChannelState = 7,
    UserRemove = 8,
    UserState = 9,
    BanList = 10,
    TextMessage = 11,
    PermissionDenied = 12,
    Acl = 13,
    QueryUsers = 14,
    CryptSetup = 15,
    ContextActionModify = 16,
    ContextAction = 17,
    UserList = 18,
    VoiceTarget = 19,
    PermissionQuery = 20,
    CodecVersion = 21,
    UserStats = 22,
    RequestBlob = 23,
    ServerConfig = 24,
    SuggestConfig = 25,
    PluginDataTransmission = 26,
}

impl TryFrom<u16> for MessageType {
    type Error = ProtocolError;

    fn try_from(value: u16) -> Result<Self> {
        use MessageType::*;
        Ok(match value {
            0 => Version,
            1 => UdpTunnel,
            2 => Authenticate,
            3 => Ping,
            4 => Reject,
            5 => ServerSync,
            6 => ChannelRemove,
            7 => ChannelState,
            8 => UserRemove,
            9 => UserState,
            10 => BanList,
            11 => TextMessage,
            12 => PermissionDenied,
            13 => Acl,
            14 => QueryUsers,
            15 => CryptSetup,
            16 => ContextActionModify,
            17 => ContextAction,
            18 => UserList,
            19 => VoiceTarget,
            20 => PermissionQuery,
            21 => CodecVersion,
            22 => UserStats,
            23 => RequestBlob,
            24 => ServerConfig,
            25 => SuggestConfig,
            26 => PluginDataTransmission,
            other => return Err(ProtocolError::UnknownMessageType(other)),
        })
    }
}

/// A fully decoded control message.
#[derive(Debug, Clone, PartialEq)]
pub enum ControlMessage {
    Version(proto::Version),
    /// Raw (unencrypted) UDP voice packet tunnelled through TCP.
    UdpTunnel(Bytes),
    Authenticate(proto::Authenticate),
    Ping(proto::Ping),
    Reject(proto::Reject),
    ServerSync(proto::ServerSync),
    ChannelRemove(proto::ChannelRemove),
    ChannelState(proto::ChannelState),
    UserRemove(proto::UserRemove),
    UserState(proto::UserState),
    BanList(proto::BanList),
    TextMessage(proto::TextMessage),
    PermissionDenied(proto::PermissionDenied),
    Acl(proto::Acl),
    QueryUsers(proto::QueryUsers),
    CryptSetup(proto::CryptSetup),
    ContextActionModify(proto::ContextActionModify),
    ContextAction(proto::ContextAction),
    UserList(proto::UserList),
    VoiceTarget(proto::VoiceTarget),
    PermissionQuery(proto::PermissionQuery),
    CodecVersion(proto::CodecVersion),
    UserStats(proto::UserStats),
    RequestBlob(proto::RequestBlob),
    ServerConfig(proto::ServerConfig),
    SuggestConfig(proto::SuggestConfig),
    PluginDataTransmission(proto::PluginDataTransmission),
}

macro_rules! dispatch {
    ($self:expr, $m:ident => $body:expr, tunnel $t:ident => $tbody:expr) => {
        match $self {
            ControlMessage::UdpTunnel($t) => $tbody,
            ControlMessage::Version($m) => $body,
            ControlMessage::Authenticate($m) => $body,
            ControlMessage::Ping($m) => $body,
            ControlMessage::Reject($m) => $body,
            ControlMessage::ServerSync($m) => $body,
            ControlMessage::ChannelRemove($m) => $body,
            ControlMessage::ChannelState($m) => $body,
            ControlMessage::UserRemove($m) => $body,
            ControlMessage::UserState($m) => $body,
            ControlMessage::BanList($m) => $body,
            ControlMessage::TextMessage($m) => $body,
            ControlMessage::PermissionDenied($m) => $body,
            ControlMessage::Acl($m) => $body,
            ControlMessage::QueryUsers($m) => $body,
            ControlMessage::CryptSetup($m) => $body,
            ControlMessage::ContextActionModify($m) => $body,
            ControlMessage::ContextAction($m) => $body,
            ControlMessage::UserList($m) => $body,
            ControlMessage::VoiceTarget($m) => $body,
            ControlMessage::PermissionQuery($m) => $body,
            ControlMessage::CodecVersion($m) => $body,
            ControlMessage::UserStats($m) => $body,
            ControlMessage::RequestBlob($m) => $body,
            ControlMessage::ServerConfig($m) => $body,
            ControlMessage::SuggestConfig($m) => $body,
            ControlMessage::PluginDataTransmission($m) => $body,
        }
    };
}

impl ControlMessage {
    /// The wire message type of this message.
    pub fn message_type(&self) -> MessageType {
        use ControlMessage as C;
        use MessageType as T;
        match self {
            C::Version(_) => T::Version,
            C::UdpTunnel(_) => T::UdpTunnel,
            C::Authenticate(_) => T::Authenticate,
            C::Ping(_) => T::Ping,
            C::Reject(_) => T::Reject,
            C::ServerSync(_) => T::ServerSync,
            C::ChannelRemove(_) => T::ChannelRemove,
            C::ChannelState(_) => T::ChannelState,
            C::UserRemove(_) => T::UserRemove,
            C::UserState(_) => T::UserState,
            C::BanList(_) => T::BanList,
            C::TextMessage(_) => T::TextMessage,
            C::PermissionDenied(_) => T::PermissionDenied,
            C::Acl(_) => T::Acl,
            C::QueryUsers(_) => T::QueryUsers,
            C::CryptSetup(_) => T::CryptSetup,
            C::ContextActionModify(_) => T::ContextActionModify,
            C::ContextAction(_) => T::ContextAction,
            C::UserList(_) => T::UserList,
            C::VoiceTarget(_) => T::VoiceTarget,
            C::PermissionQuery(_) => T::PermissionQuery,
            C::CodecVersion(_) => T::CodecVersion,
            C::UserStats(_) => T::UserStats,
            C::RequestBlob(_) => T::RequestBlob,
            C::ServerConfig(_) => T::ServerConfig,
            C::SuggestConfig(_) => T::SuggestConfig,
            C::PluginDataTransmission(_) => T::PluginDataTransmission,
        }
    }

    /// Length of the encoded payload (without header).
    pub fn encoded_len(&self) -> usize {
        dispatch!(self, m => m.encoded_len(), tunnel t => t.len())
    }

    /// Encodes header + payload into `buf` without intermediate allocations.
    pub fn encode(&self, buf: &mut BytesMut) {
        let len = self.encoded_len();
        buf.reserve(HEADER_LEN + len);
        buf.put_u16(self.message_type() as u16);
        buf.put_u32(len as u32);
        dispatch!(self, m => m.encode_raw(buf), tunnel t => buf.put_slice(t));
    }

    /// Encodes into a freshly allocated, immutable buffer.
    pub fn to_bytes(&self) -> Bytes {
        let mut buf = BytesMut::with_capacity(HEADER_LEN + self.encoded_len());
        self.encode(&mut buf);
        buf.freeze()
    }

    /// Decodes a payload of the given type.
    pub fn decode(kind: MessageType, payload: Bytes) -> Result<Self> {
        use ControlMessage as C;
        use MessageType as T;
        let p = payload;
        Ok(match kind {
            T::Version => C::Version(proto::Version::decode(p)?),
            T::UdpTunnel => C::UdpTunnel(p),
            T::Authenticate => C::Authenticate(proto::Authenticate::decode(p)?),
            T::Ping => C::Ping(proto::Ping::decode(p)?),
            T::Reject => C::Reject(proto::Reject::decode(p)?),
            T::ServerSync => C::ServerSync(proto::ServerSync::decode(p)?),
            T::ChannelRemove => C::ChannelRemove(proto::ChannelRemove::decode(p)?),
            T::ChannelState => C::ChannelState(proto::ChannelState::decode(p)?),
            T::UserRemove => C::UserRemove(proto::UserRemove::decode(p)?),
            T::UserState => C::UserState(proto::UserState::decode(p)?),
            T::BanList => C::BanList(proto::BanList::decode(p)?),
            T::TextMessage => C::TextMessage(proto::TextMessage::decode(p)?),
            T::PermissionDenied => C::PermissionDenied(proto::PermissionDenied::decode(p)?),
            T::Acl => C::Acl(proto::Acl::decode(p)?),
            T::QueryUsers => C::QueryUsers(proto::QueryUsers::decode(p)?),
            T::CryptSetup => C::CryptSetup(proto::CryptSetup::decode(p)?),
            T::ContextActionModify => C::ContextActionModify(proto::ContextActionModify::decode(p)?),
            T::ContextAction => C::ContextAction(proto::ContextAction::decode(p)?),
            T::UserList => C::UserList(proto::UserList::decode(p)?),
            T::VoiceTarget => C::VoiceTarget(proto::VoiceTarget::decode(p)?),
            T::PermissionQuery => C::PermissionQuery(proto::PermissionQuery::decode(p)?),
            T::CodecVersion => C::CodecVersion(proto::CodecVersion::decode(p)?),
            T::UserStats => C::UserStats(proto::UserStats::decode(p)?),
            T::RequestBlob => C::RequestBlob(proto::RequestBlob::decode(p)?),
            T::ServerConfig => C::ServerConfig(proto::ServerConfig::decode(p)?),
            T::SuggestConfig => C::SuggestConfig(proto::SuggestConfig::decode(p)?),
            T::PluginDataTransmission => {
                C::PluginDataTransmission(proto::PluginDataTransmission::decode(p)?)
            }
        })
    }
}

/// Incremental decoder for control frames.
///
/// Feed raw socket bytes with [`FrameDecoder::buffer_mut`] and pull complete messages with
/// [`FrameDecoder::next_message`]. Payloads are split off the internal buffer without copying.
#[derive(Debug, Default)]
pub struct FrameDecoder {
    buf: BytesMut,
}

impl FrameDecoder {
    pub fn new() -> Self {
        Self { buf: BytesMut::with_capacity(16 * 1024) }
    }

    /// Mutable access to the receive buffer (append socket data here).
    pub fn buffer_mut(&mut self) -> &mut BytesMut {
        &mut self.buf
    }

    /// Appends received bytes.
    pub fn extend(&mut self, data: &[u8]) {
        self.buf.extend_from_slice(data);
    }

    /// Returns the next complete raw frame (`type`, `payload`) if available.
    pub fn next_frame(&mut self) -> Result<Option<(u16, Bytes)>> {
        if self.buf.len() < HEADER_LEN {
            return Ok(None);
        }
        let kind = u16::from_be_bytes([self.buf[0], self.buf[1]]);
        let len = u32::from_be_bytes([self.buf[2], self.buf[3], self.buf[4], self.buf[5]]) as usize;
        if len > MAX_PAYLOAD_LEN {
            return Err(ProtocolError::MessageTooLarge(len));
        }
        if self.buf.len() < HEADER_LEN + len {
            self.buf.reserve(HEADER_LEN + len - self.buf.len());
            return Ok(None);
        }
        self.buf.advance(HEADER_LEN);
        Ok(Some((kind, self.buf.split_to(len).freeze())))
    }

    /// Returns the next decoded message. Unknown message types are skipped (forward compatibility).
    pub fn next_message(&mut self) -> Result<Option<ControlMessage>> {
        while let Some((kind, payload)) = self.next_frame()? {
            match MessageType::try_from(kind) {
                Ok(kind) => return ControlMessage::decode(kind, payload).map(Some),
                Err(ProtocolError::UnknownMessageType(_)) => continue,
                Err(e) => return Err(e),
            }
        }
        Ok(None)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn roundtrip_incremental() {
        let msg = ControlMessage::TextMessage(proto::TextMessage {
            actor: Some(3),
            channel_id: vec![0],
            message: "halo dunia".into(),
            ..Default::default()
        });
        let ping = ControlMessage::Ping(proto::Ping { timestamp: Some(42), ..Default::default() });
        let mut wire = BytesMut::new();
        msg.encode(&mut wire);
        ping.encode(&mut wire);

        let mut dec = FrameDecoder::new();
        // Feed byte by byte to exercise partial frames.
        let mut out = Vec::new();
        for b in wire.iter() {
            dec.extend(&[*b]);
            while let Some(m) = dec.next_message().unwrap() {
                out.push(m);
            }
        }
        assert_eq!(out, vec![msg, ping]);
    }

    #[test]
    fn skips_unknown_types() {
        let mut dec = FrameDecoder::new();
        dec.extend(&[0x03, 0xE7, 0, 0, 0, 1, 0xAA]);
        ControlMessage::Ping(proto::Ping::default()).encode(dec.buffer_mut());
        assert!(matches!(dec.next_message().unwrap(), Some(ControlMessage::Ping(_))));
    }

    #[test]
    fn rejects_oversized() {
        let mut dec = FrameDecoder::new();
        dec.extend(&[0, 0, 0xFF, 0xFF, 0xFF, 0xFF]);
        assert!(dec.next_frame().is_err());
    }
}
