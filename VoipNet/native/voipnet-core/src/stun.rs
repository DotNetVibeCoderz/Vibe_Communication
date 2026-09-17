//! STUN (RFC 5389), ICE connectivity-check attributes (RFC 8445) and a TURN client (RFC 5766).

use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, UdpSocket};
use std::time::{Duration, Instant};

use hmac::{Hmac, Mac};
use md5::{Digest, Md5};
use sha1::Sha1;

pub const MAGIC_COOKIE: u32 = 0x2112_A442;

pub const BINDING_REQUEST: u16 = 0x0001;
pub const BINDING_SUCCESS: u16 = 0x0101;
pub const BINDING_ERROR: u16 = 0x0111;
pub const ALLOCATE_REQUEST: u16 = 0x0003;
pub const ALLOCATE_SUCCESS: u16 = 0x0103;
pub const ALLOCATE_ERROR: u16 = 0x0113;
pub const REFRESH_REQUEST: u16 = 0x0004;
pub const CREATE_PERMISSION_REQUEST: u16 = 0x0008;
pub const SEND_INDICATION: u16 = 0x0016;
pub const DATA_INDICATION: u16 = 0x0017;

pub const ATTR_MAPPED_ADDRESS: u16 = 0x0001;
pub const ATTR_USERNAME: u16 = 0x0006;
pub const ATTR_MESSAGE_INTEGRITY: u16 = 0x0008;
pub const ATTR_ERROR_CODE: u16 = 0x0009;
pub const ATTR_LIFETIME: u16 = 0x000D;
pub const ATTR_XOR_PEER_ADDRESS: u16 = 0x0012;
pub const ATTR_DATA: u16 = 0x0013;
pub const ATTR_REALM: u16 = 0x0014;
pub const ATTR_NONCE: u16 = 0x0015;
pub const ATTR_XOR_RELAYED_ADDRESS: u16 = 0x0016;
pub const ATTR_REQUESTED_TRANSPORT: u16 = 0x0019;
pub const ATTR_XOR_MAPPED_ADDRESS: u16 = 0x0020;
pub const ATTR_PRIORITY: u16 = 0x0024;
pub const ATTR_USE_CANDIDATE: u16 = 0x0025;
pub const ATTR_SOFTWARE: u16 = 0x8022;
pub const ATTR_FINGERPRINT: u16 = 0x8028;
pub const ATTR_ICE_CONTROLLED: u16 = 0x8029;
pub const ATTR_ICE_CONTROLLING: u16 = 0x802A;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StunMessage {
    pub msg_type: u16,
    pub transaction_id: [u8; 12],
    pub attributes: Vec<(u16, Vec<u8>)>,
}

impl StunMessage {
    pub fn new(msg_type: u16) -> Self {
        use rand::RngCore;
        let mut tid = [0u8; 12];
        rand::thread_rng().fill_bytes(&mut tid);
        Self { msg_type, transaction_id: tid, attributes: Vec::new() }
    }

    pub fn reply(&self, msg_type: u16) -> Self {
        Self { msg_type, transaction_id: self.transaction_id, attributes: Vec::new() }
    }

    pub fn add(&mut self, attr: u16, value: impl Into<Vec<u8>>) -> &mut Self {
        self.attributes.push((attr, value.into()));
        self
    }

    pub fn get(&self, attr: u16) -> Option<&[u8]> {
        self.attributes.iter().find(|(t, _)| *t == attr).map(|(_, v)| v.as_slice())
    }

    pub fn add_xor_address(&mut self, attr: u16, addr: SocketAddr) -> &mut Self {
        let v = encode_xor_address(addr, &self.transaction_id);
        self.add(attr, v)
    }

    pub fn xor_address(&self, attr: u16) -> Option<SocketAddr> {
        decode_xor_address(self.get(attr)?, &self.transaction_id)
    }

    pub fn mapped_address(&self) -> Option<SocketAddr> {
        self.xor_address(ATTR_XOR_MAPPED_ADDRESS).or_else(|| decode_plain_address(self.get(ATTR_MAPPED_ADDRESS)?))
    }

    pub fn error_code(&self) -> Option<u16> {
        let v = self.get(ATTR_ERROR_CODE)?;
        (v.len() >= 4).then(|| (v[2] as u16 & 0x7) * 100 + v[3] as u16)
    }

    /// Serializes the message, optionally appending MESSAGE-INTEGRITY and FINGERPRINT.
    pub fn encode(&self, integrity_key: Option<&[u8]>, fingerprint: bool) -> Vec<u8> {
        let mut out = Vec::with_capacity(128);
        out.extend_from_slice(&self.msg_type.to_be_bytes());
        out.extend_from_slice(&[0, 0]);
        out.extend_from_slice(&MAGIC_COOKIE.to_be_bytes());
        out.extend_from_slice(&self.transaction_id);
        for (t, v) in &self.attributes {
            push_attr(&mut out, *t, v);
        }
        if let Some(key) = integrity_key {
            set_len(&mut out, 24);
            let mut mac = <Hmac<Sha1> as Mac>::new_from_slice(key).expect("any key length");
            mac.update(&out);
            let digest = mac.finalize().into_bytes();
            push_attr(&mut out, ATTR_MESSAGE_INTEGRITY, &digest);
        }
        if fingerprint {
            set_len(&mut out, 8);
            let crc = crc32fast::hash(&out) ^ 0x5354_554E;
            push_attr(&mut out, ATTR_FINGERPRINT, &crc.to_be_bytes());
        }
        set_len(&mut out, 0);
        out
    }

    pub fn decode(data: &[u8]) -> Option<Self> {
        if data.len() < 20 || data[0] & 0xC0 != 0 {
            return None;
        }
        if u32::from_be_bytes([data[4], data[5], data[6], data[7]]) != MAGIC_COOKIE {
            return None;
        }
        let len = u16::from_be_bytes([data[2], data[3]]) as usize;
        if data.len() < 20 + len {
            return None;
        }
        let mut msg = StunMessage {
            msg_type: u16::from_be_bytes([data[0], data[1]]),
            transaction_id: data[8..20].try_into().ok()?,
            attributes: Vec::new(),
        };
        let mut i = 20;
        while i + 4 <= 20 + len {
            let t = u16::from_be_bytes([data[i], data[i + 1]]);
            let l = u16::from_be_bytes([data[i + 2], data[i + 3]]) as usize;
            let start = i + 4;
            if start + l > data.len() {
                return None;
            }
            msg.attributes.push((t, data[start..start + l].to_vec()));
            i = start + l.div_ceil(4) * 4;
        }
        Some(msg)
    }

    /// Verifies MESSAGE-INTEGRITY of a raw message with the given key.
    pub fn verify_integrity(raw: &[u8], key: &[u8]) -> bool {
        let Some(mi_offset) = find_attr_offset(raw, ATTR_MESSAGE_INTEGRITY) else { return false };
        let mut copy = raw[..mi_offset].to_vec();
        set_len(&mut copy, 24);
        let mut mac = <Hmac<Sha1> as Mac>::new_from_slice(key).expect("any key length");
        mac.update(&copy);
        let expected = mac.finalize().into_bytes();
        raw.get(mi_offset + 4..mi_offset + 24).is_some_and(|got| got == expected.as_slice())
    }
}

pub fn is_stun(data: &[u8]) -> bool {
    data.len() >= 20 && data[0] & 0xC0 == 0 && data[4..8] == MAGIC_COOKIE.to_be_bytes()
}

fn push_attr(out: &mut Vec<u8>, t: u16, v: &[u8]) {
    out.extend_from_slice(&t.to_be_bytes());
    out.extend_from_slice(&(v.len() as u16).to_be_bytes());
    out.extend_from_slice(v);
    out.resize(out.len() + (4 - v.len() % 4) % 4, 0);
}

/// Sets the header length to the current body length plus `extra`.
fn set_len(out: &mut [u8], extra: usize) {
    let len = (out.len() - 20 + extra) as u16;
    out[2..4].copy_from_slice(&len.to_be_bytes());
}

fn find_attr_offset(raw: &[u8], attr: u16) -> Option<usize> {
    let mut i = 20;
    while i + 4 <= raw.len() {
        let t = u16::from_be_bytes([raw[i], raw[i + 1]]);
        let l = u16::from_be_bytes([raw[i + 2], raw[i + 3]]) as usize;
        if t == attr {
            return Some(i);
        }
        i += 4 + l.div_ceil(4) * 4;
    }
    None
}

fn encode_xor_address(addr: SocketAddr, tid: &[u8; 12]) -> Vec<u8> {
    let port = addr.port() ^ (MAGIC_COOKIE >> 16) as u16;
    let mut v = vec![0];
    match addr.ip() {
        IpAddr::V4(ip) => {
            v.push(1);
            v.extend_from_slice(&port.to_be_bytes());
            let x = u32::from(ip) ^ MAGIC_COOKIE;
            v.extend_from_slice(&x.to_be_bytes());
        }
        IpAddr::V6(ip) => {
            v.push(2);
            v.extend_from_slice(&port.to_be_bytes());
            let mut key = [0u8; 16];
            key[..4].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
            key[4..].copy_from_slice(tid);
            v.extend(ip.octets().iter().zip(key).map(|(a, b)| a ^ b));
        }
    }
    v
}

fn decode_xor_address(v: &[u8], tid: &[u8; 12]) -> Option<SocketAddr> {
    if v.len() < 8 {
        return None;
    }
    let port = u16::from_be_bytes([v[2], v[3]]) ^ (MAGIC_COOKIE >> 16) as u16;
    match v[1] {
        1 => {
            let x = u32::from_be_bytes([v[4], v[5], v[6], v[7]]) ^ MAGIC_COOKIE;
            Some(SocketAddr::new(IpAddr::V4(Ipv4Addr::from(x)), port))
        }
        2 if v.len() >= 20 => {
            let mut key = [0u8; 16];
            key[..4].copy_from_slice(&MAGIC_COOKIE.to_be_bytes());
            key[4..].copy_from_slice(tid);
            let mut ip = [0u8; 16];
            for i in 0..16 {
                ip[i] = v[4 + i] ^ key[i];
            }
            Some(SocketAddr::new(IpAddr::V6(Ipv6Addr::from(ip)), port))
        }
        _ => None,
    }
}

fn decode_plain_address(v: &[u8]) -> Option<SocketAddr> {
    if v.len() < 8 {
        return None;
    }
    let port = u16::from_be_bytes([v[2], v[3]]);
    match v[1] {
        1 => Some(SocketAddr::new(IpAddr::V4(Ipv4Addr::new(v[4], v[5], v[6], v[7])), port)),
        2 if v.len() >= 20 => {
            let ip: [u8; 16] = v[4..20].try_into().ok()?;
            Some(SocketAddr::new(IpAddr::V6(Ipv6Addr::from(ip)), port))
        }
        _ => None,
    }
}

/// Blocking request/response with RFC 5389 retransmission (RTO 250 ms doubling).
fn transact(socket: &UdpSocket, server: SocketAddr, raw: &[u8], tid: &[u8; 12], timeout: Duration) -> Option<StunMessage> {
    let deadline = Instant::now() + timeout;
    let mut rto = Duration::from_millis(250);
    let mut buf = [0u8; 1500];
    let prev_timeout = socket.read_timeout().ok().flatten();
    let result = 'outer: loop {
        if Instant::now() >= deadline {
            break None;
        }
        socket.send_to(raw, server).ok()?;
        let wait_until = (Instant::now() + rto).min(deadline);
        while Instant::now() < wait_until {
            let _ = socket.set_read_timeout(Some(wait_until.saturating_duration_since(Instant::now()).max(Duration::from_millis(1))));
            match socket.recv_from(&mut buf) {
                Ok((n, _)) => {
                    if let Some(m) = StunMessage::decode(&buf[..n]) {
                        if &m.transaction_id == tid {
                            break 'outer Some(m);
                        }
                    }
                }
                Err(_) => break,
            }
        }
        rto *= 2;
    };
    let _ = socket.set_read_timeout(prev_timeout);
    result
}

/// Discovers the server-reflexive address of `socket` using a STUN server.
pub fn discover_reflexive(socket: &UdpSocket, server: SocketAddr, timeout: Duration) -> Option<SocketAddr> {
    let mut req = StunMessage::new(BINDING_REQUEST);
    req.add(ATTR_SOFTWARE, b"Voip.NET".to_vec());
    let raw = req.encode(None, true);
    let resp = transact(socket, server, &raw, &req.transaction_id, timeout)?;
    (resp.msg_type == BINDING_SUCCESS).then(|| resp.mapped_address()).flatten()
}

/// Minimal TURN/UDP client allocation.
pub struct TurnAllocation {
    pub server: SocketAddr,
    pub relayed: SocketAddr,
    pub mapped: Option<SocketAddr>,
    pub lifetime: Duration,
    username: String,
    realm: String,
    nonce: String,
    key: [u8; 16],
}

impl TurnAllocation {
    pub fn allocate(socket: &UdpSocket, server: SocketAddr, username: &str, password: &str, timeout: Duration) -> Option<Self> {
        let mut req = StunMessage::new(ALLOCATE_REQUEST);
        req.add(ATTR_REQUESTED_TRANSPORT, vec![17, 0, 0, 0]);
        let resp = transact(socket, server, &req.encode(None, true), &req.transaction_id, timeout)?;
        if resp.msg_type != ALLOCATE_ERROR || resp.error_code() != Some(401) {
            return None;
        }
        let realm = String::from_utf8(resp.get(ATTR_REALM)?.to_vec()).ok()?;
        let nonce = String::from_utf8(resp.get(ATTR_NONCE)?.to_vec()).ok()?;
        let key: [u8; 16] = Md5::digest(format!("{username}:{realm}:{password}").as_bytes()).into();

        let mut req = StunMessage::new(ALLOCATE_REQUEST);
        req.add(ATTR_REQUESTED_TRANSPORT, vec![17, 0, 0, 0])
            .add(ATTR_USERNAME, username.as_bytes().to_vec())
            .add(ATTR_REALM, realm.as_bytes().to_vec())
            .add(ATTR_NONCE, nonce.as_bytes().to_vec());
        let resp = transact(socket, server, &req.encode(Some(&key), true), &req.transaction_id, timeout)?;
        if resp.msg_type != ALLOCATE_SUCCESS {
            return None;
        }
        let lifetime = resp
            .get(ATTR_LIFETIME)
            .filter(|v| v.len() == 4)
            .map(|v| u32::from_be_bytes([v[0], v[1], v[2], v[3]]))
            .unwrap_or(600);
        Some(Self {
            server,
            relayed: resp.xor_address(ATTR_XOR_RELAYED_ADDRESS)?,
            mapped: resp.xor_address(ATTR_XOR_MAPPED_ADDRESS),
            lifetime: Duration::from_secs(lifetime as u64),
            username: username.to_owned(),
            realm,
            nonce,
            key,
        })
    }

    fn authed(&self, msg_type: u16) -> StunMessage {
        let mut m = StunMessage::new(msg_type);
        m.add(ATTR_USERNAME, self.username.as_bytes().to_vec())
            .add(ATTR_REALM, self.realm.as_bytes().to_vec())
            .add(ATTR_NONCE, self.nonce.as_bytes().to_vec());
        m
    }

    /// Builds a CreatePermission request (send it and let the media loop consume the answer).
    pub fn create_permission(&self, peer: SocketAddr) -> Vec<u8> {
        let mut m = StunMessage::new(CREATE_PERMISSION_REQUEST);
        m.add_xor_address(ATTR_XOR_PEER_ADDRESS, peer)
            .add(ATTR_USERNAME, self.username.as_bytes().to_vec())
            .add(ATTR_REALM, self.realm.as_bytes().to_vec())
            .add(ATTR_NONCE, self.nonce.as_bytes().to_vec());
        m.encode(Some(&self.key), true)
    }

    pub fn refresh(&self, lifetime_secs: u32) -> Vec<u8> {
        let mut m = self.authed(REFRESH_REQUEST);
        m.add(ATTR_LIFETIME, lifetime_secs.to_be_bytes().to_vec());
        m.encode(Some(&self.key), true)
    }

    /// Wraps application data in a Send indication addressed to `peer`.
    pub fn wrap(&self, peer: SocketAddr, data: &[u8]) -> Vec<u8> {
        let mut m = StunMessage::new(SEND_INDICATION);
        m.add_xor_address(ATTR_XOR_PEER_ADDRESS, peer).add(ATTR_DATA, data.to_vec());
        m.encode(None, false)
    }

    /// Extracts (peer, payload) from a Data indication.
    pub fn unwrap(raw: &[u8]) -> Option<(SocketAddr, Vec<u8>)> {
        let m = StunMessage::decode(raw)?;
        (m.msg_type == DATA_INDICATION).then(|| Some((m.xor_address(ATTR_XOR_PEER_ADDRESS)?, m.get(ATTR_DATA)?.to_vec())))?
    }
}

/// An ICE candidate (RFC 8445 §5.1, SDP grammar RFC 8839).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Candidate {
    pub foundation: String,
    pub component: u16,
    pub priority: u32,
    pub address: SocketAddr,
    pub kind: CandidateKind,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CandidateKind {
    Host,
    ServerReflexive,
    Relayed,
    PeerReflexive,
}

impl CandidateKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Host => "host",
            Self::ServerReflexive => "srflx",
            Self::Relayed => "relay",
            Self::PeerReflexive => "prflx",
        }
    }

    fn type_preference(self) -> u32 {
        match self {
            Self::Host => 126,
            Self::PeerReflexive => 110,
            Self::ServerReflexive => 100,
            Self::Relayed => 0,
        }
    }
}

impl Candidate {
    pub fn new(kind: CandidateKind, address: SocketAddr, component: u16) -> Self {
        let priority = (kind.type_preference() << 24) | (65535 << 8) | (256 - component as u32);
        Self { foundation: format!("{}", kind.type_preference()), component, priority, address, kind }
    }

    pub fn to_sdp(&self) -> String {
        format!(
            "{} {} udp {} {} {} typ {}",
            self.foundation,
            self.component,
            self.priority,
            self.address.ip(),
            self.address.port(),
            self.kind.as_str()
        )
    }

    pub fn parse(s: &str) -> Option<Self> {
        let f: Vec<_> = s.trim().trim_start_matches("candidate:").split_whitespace().collect();
        if f.len() < 8 || !f[2].eq_ignore_ascii_case("udp") || f[6] != "typ" {
            return None;
        }
        let kind = match f[7] {
            "host" => CandidateKind::Host,
            "srflx" => CandidateKind::ServerReflexive,
            "relay" => CandidateKind::Relayed,
            "prflx" => CandidateKind::PeerReflexive,
            _ => return None,
        };
        let ip: IpAddr = f[4].parse().ok()?;
        Some(Self {
            foundation: f[0].to_owned(),
            component: f[1].parse().ok()?,
            priority: f[3].parse().ok()?,
            address: SocketAddr::new(ip, f[5].parse().ok()?),
            kind,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn encode_decode_with_integrity_and_fingerprint() {
        let mut m = StunMessage::new(BINDING_REQUEST);
        m.add(ATTR_USERNAME, b"remote:local".to_vec()).add(ATTR_PRIORITY, 12345u32.to_be_bytes().to_vec());
        let raw = m.encode(Some(b"password"), true);
        assert!(is_stun(&raw));
        assert!(StunMessage::verify_integrity(&raw, b"password"));
        assert!(!StunMessage::verify_integrity(&raw, b"wrong"));
        let d = StunMessage::decode(&raw).unwrap();
        assert_eq!(d.get(ATTR_USERNAME), Some(&b"remote:local"[..]));
        assert!(d.get(ATTR_FINGERPRINT).is_some());
    }

    #[test]
    fn xor_address_roundtrip() {
        for addr in ["192.0.2.1:32853", "[2001:db8::1]:5000"] {
            let addr: SocketAddr = addr.parse().unwrap();
            let mut m = StunMessage::new(BINDING_SUCCESS);
            m.add_xor_address(ATTR_XOR_MAPPED_ADDRESS, addr);
            let d = StunMessage::decode(&m.encode(None, false)).unwrap();
            assert_eq!(d.mapped_address(), Some(addr));
        }
    }

    #[test]
    fn candidate_sdp_roundtrip() {
        let c = Candidate::new(CandidateKind::ServerReflexive, "203.0.113.5:40000".parse().unwrap(), 1);
        assert_eq!(Candidate::parse(&c.to_sdp()), Some(c));
    }

    #[test]
    fn binding_against_local_responder() {
        let server = UdpSocket::bind("127.0.0.1:0").unwrap();
        let server_addr = server.local_addr().unwrap();
        std::thread::spawn(move || {
            let mut buf = [0u8; 1500];
            let (n, from) = server.recv_from(&mut buf).unwrap();
            let req = StunMessage::decode(&buf[..n]).unwrap();
            let mut resp = req.reply(BINDING_SUCCESS);
            resp.add_xor_address(ATTR_XOR_MAPPED_ADDRESS, from);
            server.send_to(&resp.encode(None, true), from).unwrap();
        });
        let client = UdpSocket::bind("127.0.0.1:0").unwrap();
        let mapped = discover_reflexive(&client, server_addr, Duration::from_secs(2)).unwrap();
        assert_eq!(mapped, client.local_addr().unwrap());
    }
}
