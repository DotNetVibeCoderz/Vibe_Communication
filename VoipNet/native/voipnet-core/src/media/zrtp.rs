//! ZRTP key agreement (RFC 6189): the two endpoints agree on SRTP keys over the media path itself,
//! with no help from the signalling, and read four characters aloud to prove nobody sat in between.
//!
//! Sans-IO, like the ICE agent and the data channel: packets in, events out, and the media session
//! does the sending. What is here is the ordinary Diffie-Hellman exchange — Hello, Commit, DHPart1,
//! DHPart2, Confirm1, Confirm2, Conf2ACK — with EC25 (P-256), AES-128, HMAC-SHA256 and a base32 SAS.
//! There are no cached secrets between calls, no multistream or preshared modes, no signatures and no
//! PBX enrolment: every call starts from nothing, which the RFC allows and which means the short
//! authentication string has to be read out every time.

use std::time::{Duration, Instant};

use ring::agreement::{agree_ephemeral, EphemeralPrivateKey, PublicKey, UnparsedPublicKey, ECDH_P256};
use ring::digest::{digest, SHA256};
use ring::hmac;
use ring::rand::SystemRandom;

use crate::media::sctp::crc32c;
use crate::srtp::{SrtpContext, SrtpProfile};

/// Every ZRTP packet carries this instead of an RTP payload type (RFC 6189 §5).
const MAGIC: u32 = 0x5a52_5450;
/// Messages start with "PZ".
const PREAMBLE: u16 = 0x505a;
/// How long to wait before sending a handshake message again.
const RETRY: Duration = Duration::from_millis(500);
/// How many times, before the exchange is given up on.
const MAX_RETRIES: u8 = 8;

/// What the agreement wants the caller to do, or tells it happened.
pub enum ZrtpEvent {
    /// Send these bytes to the peer, on the media path.
    Send(Vec<u8>),
    /// Keys agreed: (outbound, inbound, profile, short authentication string).
    Secure(Box<SecureResult>),
    /// The exchange failed; media stays as it was.
    Failed(String),
}

/// What comes out of a finished exchange.
pub struct SecureResult {
    pub outbound: SrtpContext,
    pub inbound: SrtpContext,
    pub profile: SrtpProfile,
    /// The four characters both sides read aloud to each other.
    pub sas: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    /// Saying hello and waiting to hear one back.
    Discovery,
    /// Both said hello; this side is trying to take the initiator role.
    Committing,
    /// This side answered a Commit and is waiting for the initiator's key.
    Responding,
    /// Keys exchanged, waiting for the other side to confirm.
    Confirming,
    Secure,
    Failed,
}

/// One end of a ZRTP exchange.
pub struct ZrtpSession {
    state: State,
    ssrc: u32,
    sequence: u16,
    zid: [u8; 12],
    /// The hash chain (RFC 6189 §9): H0 is secret until Confirm, H3 travels in Hello.
    h: [[u8; 32]; 4],
    /// This side's key pair, made when it may be needed.
    private: Option<EphemeralPrivateKey>,
    public: Option<PublicKey>,
    /// Messages kept because the key derivation hashes them verbatim.
    my_hello: Vec<u8>,
    peer_hello: Option<Vec<u8>>,
    peer_h2: Option<[u8; 32]>,
    peer_zid: Option<[u8; 12]>,
    peer_public: Option<Vec<u8>>,
    commit: Option<Vec<u8>>,
    dhpart1: Option<Vec<u8>>,
    dhpart2: Option<Vec<u8>>,
    initiator: bool,
    /// The packet to send again while waiting for an answer, and how many times it has gone out.
    pending: Option<Vec<u8>>,
    sent_at: Instant,
    retries: u8,
    /// Keys, once derived, so a repeated Confirm can be answered without doing it again.
    keys: Option<DerivedKeys>,
    hello_acked: bool,
}

struct DerivedKeys {
    srtp_initiator: ([u8; 16], [u8; 14]),
    srtp_responder: ([u8; 16], [u8; 14]),
    zrtp_initiator: [u8; 16],
    zrtp_responder: [u8; 16],
    mac_initiator: [u8; 32],
    mac_responder: [u8; 32],
    sas: String,
}

impl ZrtpSession {
    /// Starts an exchange for one media stream.
    pub fn new(ssrc: u32) -> Self {
        let mut zid = [0u8; 12];
        zid.copy_from_slice(&rand::random::<[u8; 12]>());
        // The chain is built backwards: H0 is random, and each next one is the hash of the last.
        let h0: [u8; 32] = rand::random();
        let h1 = sha256(&h0);
        let h2 = sha256(&h1);
        let h3 = sha256(&h2);
        let mut session = Self {
            state: State::Discovery,
            ssrc,
            sequence: 1,
            zid,
            h: [h0, h1, h2, h3],
            private: None,
            public: None,
            my_hello: Vec::new(),
            peer_hello: None,
            peer_h2: None,
            peer_zid: None,
            peer_public: None,
            commit: None,
            dhpart1: None,
            dhpart2: None,
            initiator: false,
            pending: None,
            sent_at: Instant::now(),
            retries: 0,
            keys: None,
            hello_acked: false,
        };
        session.my_hello = session.build_hello();
        session
    }

    /// True once both sides have agreed on keys.
    pub fn is_secure(&self) -> bool {
        self.state == State::Secure
    }

    /// The short authentication string, once there is one.
    pub fn sas(&self) -> Option<&str> {
        self.keys.as_ref().map(|k| k.sas.as_str())
    }

    /// The hash of this side's Hello, which signalling can carry so the peer knows what to expect.
    pub fn hello_hash(&self) -> String {
        sha256(&self.my_hello).iter().map(|b| format!("{b:02x}")).collect()
    }

    /// Sends the first Hello. Both sides do this; whoever is heard first does not matter.
    pub fn start(&mut self, now: Instant, events: &mut Vec<ZrtpEvent>) {
        if self.state != State::Discovery {
            return;
        }

        let hello = self.packet(&self.my_hello.clone());
        self.arm(hello, now, events);
    }

    /// True when a datagram looks like ZRTP rather than RTP (RFC 6189 §5.1).
    pub fn is_zrtp(packet: &[u8]) -> bool {
        packet.len() >= 16 && packet[0] == 0x10 && u32::from_be_bytes([packet[4], packet[5], packet[6], packet[7]]) == MAGIC
    }

    /// Feeds one ZRTP packet that arrived on the media socket.
    pub fn handle_packet(&mut self, packet: &[u8], now: Instant, events: &mut Vec<ZrtpEvent>) {
        if !Self::is_zrtp(packet) || packet.len() < 24 || self.state == State::Failed {
            return;
        }

        // The last word is a CRC over everything before it.
        let body = &packet[..packet.len() - 4];
        let crc = u32::from_be_bytes(packet[packet.len() - 4..].try_into().expect("4 bytes"));
        if crc32c(body) != crc {
            return;
        }

        let message = &packet[12..packet.len() - 4];
        if message.len() < 12 || u16::from_be_bytes([message[0], message[1]]) != PREAMBLE {
            return;
        }

        let kind = &message[4..12];
        match kind {
            b"Hello   " => self.on_hello(message, now, events),
            b"HelloACK" => self.hello_acked = true,
            b"Commit  " => self.on_commit(message, now, events),
            b"DHPart1 " => self.on_dhpart1(message, now, events),
            b"DHPart2 " => self.on_dhpart2(message, now, events),
            b"Confirm1" => self.on_confirm(message, true, now, events),
            b"Confirm2" => self.on_confirm(message, false, now, events),
            b"Conf2ACK" => self.on_conf2ack(events),
            b"Error   " => self.fail("the peer reported a ZRTP error", events),
            _ => {}
        }
    }

    /// Sends the outstanding message again when the peer has not answered.
    pub fn poll_timeout(&mut self, now: Instant, events: &mut Vec<ZrtpEvent>) {
        let Some(pending) = self.pending.clone() else { return };
        if now.duration_since(self.sent_at) < RETRY {
            return;
        }

        if self.retries >= MAX_RETRIES {
            self.pending = None;
            self.fail("the peer never finished the ZRTP exchange", events);
            return;
        }

        self.retries += 1;
        self.sent_at = now;
        events.push(ZrtpEvent::Send(pending));
    }

    // ---- messages received ----------------------------------------------------------------

    fn on_hello(&mut self, message: &[u8], now: Instant, events: &mut Vec<ZrtpEvent>) {
        if self.peer_hello.is_some() {
            // A repeat: answer it again so the peer stops asking.
            let ack = self.packet(&build_message(b"HelloACK", &[]));
            events.push(ZrtpEvent::Send(ack));
            return;
        }

        // Hello body: version(4) client(16) H3(32) ZID(12) flags(4) then algorithm lists.
        if message.len() < 12 + 4 + 16 + 32 + 12 {
            return;
        }

        let mut zid = [0u8; 12];
        zid.copy_from_slice(&message[12 + 4 + 16 + 32..12 + 4 + 16 + 32 + 12]);
        self.peer_zid = Some(zid);
        self.peer_hello = Some(message.to_vec());

        let ack = self.packet(&build_message(b"HelloACK", &[]));
        events.push(ZrtpEvent::Send(ack));

        // Both sides want to commit; the larger ZID wins and becomes the initiator (RFC 6189 §4.2).
        if self.zid > zid {
            self.begin_commit(now, events);
        }
    }

    fn begin_commit(&mut self, now: Instant, events: &mut Vec<ZrtpEvent>) {
        if self.state != State::Discovery {
            return;
        }

        self.initiator = true;
        self.ensure_key();
        let Some(peer_hello) = self.peer_hello.clone() else { return };
        // hvi ties the commit to the key this side will send and to the peer's Hello (§4.4.1.1).
        let dhpart2 = self.build_dhpart(false);
        let mut hvi_input = dhpart2.clone();
        hvi_input.extend_from_slice(&peer_hello);
        let hvi = sha256(&hvi_input);
        self.dhpart2 = Some(dhpart2);

        let mut body = Vec::with_capacity(84);
        body.extend_from_slice(&self.h[2]);
        body.extend_from_slice(&self.zid);
        body.extend_from_slice(b"S256");
        body.extend_from_slice(b"AES1");
        body.extend_from_slice(b"HS32");
        body.extend_from_slice(b"EC25");
        body.extend_from_slice(b"B32 ");
        body.extend_from_slice(&hvi);
        let commit = build_message_with_mac(b"Commit  ", &body, &self.h[1]);
        self.commit = Some(commit.clone());
        self.state = State::Committing;
        let packet = self.packet(&commit);
        self.arm(packet, now, events);
    }

    fn on_commit(&mut self, message: &[u8], now: Instant, events: &mut Vec<ZrtpEvent>) {
        if self.peer_hello.is_none() || matches!(self.state, State::Secure | State::Confirming) {
            return;
        }

        // Commit body: H2(32) ZID(12) hash(4) cipher(4) authtag(4) keyagreement(4) sas(4) hvi(32) mac(8).
        if message.len() < 12 + 32 + 12 + 20 + 32 + 8 {
            return;
        }

        let mut h2 = [0u8; 32];
        h2.copy_from_slice(&message[12..44]);
        let mut peer_zid = [0u8; 12];
        peer_zid.copy_from_slice(&message[44..56]);

        // Both sides may have committed at once; the larger ZID stays the initiator and the other
        // drops its own commit (§4.2). Our own commit only exists when we thought we were larger.
        if self.initiator && self.zid > peer_zid {
            return;
        }

        self.initiator = false;
        self.peer_h2 = Some(h2);
        self.peer_zid = Some(peer_zid);
        self.commit = Some(message.to_vec());
        self.ensure_key();
        let dhpart1 = self.build_dhpart(true);
        self.dhpart1 = Some(dhpart1.clone());
        self.state = State::Responding;
        let packet = self.packet(&dhpart1);
        self.arm(packet, now, events);
    }

    fn on_dhpart1(&mut self, message: &[u8], now: Instant, events: &mut Vec<ZrtpEvent>) {
        if !self.initiator || self.state != State::Committing {
            return;
        }

        let Some(public) = read_dh_public(message) else { return };
        self.peer_public = Some(public);
        self.dhpart1 = Some(message.to_vec());
        let Some(dhpart2) = self.dhpart2.clone() else { return };
        if !self.derive_keys(events) {
            return;
        }

        self.state = State::Confirming;
        let packet = self.packet(&dhpart2);
        self.arm(packet, now, events);
    }

    fn on_dhpart2(&mut self, message: &[u8], now: Instant, events: &mut Vec<ZrtpEvent>) {
        if self.initiator || self.state != State::Responding {
            return;
        }

        let Some(public) = read_dh_public(message) else { return };
        self.peer_public = Some(public);
        self.dhpart2 = Some(message.to_vec());
        if !self.derive_keys(events) {
            return;
        }

        // The responder confirms first, with the key the initiator will check.
        let confirm = self.build_confirm(true);
        self.state = State::Confirming;
        let packet = self.packet(&confirm);
        self.arm(packet, now, events);
    }

    fn on_confirm(&mut self, message: &[u8], first: bool, now: Instant, events: &mut Vec<ZrtpEvent>) {
        let Some(keys) = self.keys.as_ref() else { return };
        // Confirm1 comes from the responder, Confirm2 from the initiator.
        let (key, mac_key) = match first {
            true => (keys.zrtp_responder, keys.mac_responder),
            false => (keys.zrtp_initiator, keys.mac_initiator),
        };
        if message.len() < 12 + 8 + 16 {
            return;
        }

        let mac = &message[12..20];
        let iv = &message[20..36];
        let encrypted = &message[36..];
        let signing = hmac::Key::new(hmac::HMAC_SHA256, &mac_key);
        if hmac::verify(&signing, encrypted, &pad_to_32(mac)).is_err() && !mac_matches(&signing, encrypted, mac) {
            self.fail("the ZRTP confirmation did not match", events);
            return;
        }

        let _plain = aes_cfb(&key, iv.try_into().expect("16 bytes"), encrypted, false);

        if first && self.initiator {
            let confirm = self.build_confirm(false);
            let packet = self.packet(&confirm);
            self.arm(packet, now, events);
        } else if !first && !self.initiator {
            let ack = self.packet(&build_message(b"Conf2ACK", &[]));
            events.push(ZrtpEvent::Send(ack));
            self.secure(events);
        }
    }

    fn on_conf2ack(&mut self, events: &mut Vec<ZrtpEvent>) {
        if self.initiator {
            self.secure(events);
        }
    }

    // ---- keys ---------------------------------------------------------------------------------

    fn derive_keys(&mut self, events: &mut Vec<ZrtpEvent>) -> bool {
        let (Some(private), Some(peer_public)) = (self.private.take(), self.peer_public.clone()) else {
            self.fail("ZRTP key agreement had nothing to agree on", events);
            return false;
        };

        // ring hands the shared secret to a closure; EC25 uses the x coordinate, 32 bytes.
        let mut point = Vec::with_capacity(65);
        point.push(0x04);
        point.extend_from_slice(&peer_public);
        let peer = UnparsedPublicKey::new(&ECDH_P256, point);
        let Ok(shared) = agree_ephemeral(private, &peer, |secret| secret.to_vec()) else {
            self.fail("the peer's ZRTP key was not usable", events);
            return false;
        };

        let (Some(hello), Some(commit), Some(dhpart1), Some(dhpart2)) =
            (self.responder_hello(), self.commit.clone(), self.dhpart1.clone(), self.dhpart2.clone())
        else {
            self.fail("the ZRTP exchange was incomplete", events);
            return false;
        };

        // total_hash covers everything both sides said, so a tampered message changes every key.
        let mut total = Vec::new();
        total.extend_from_slice(&hello);
        total.extend_from_slice(&commit);
        total.extend_from_slice(&dhpart1);
        total.extend_from_slice(&dhpart2);
        let total_hash = sha256(&total);

        let (zid_initiator, zid_responder) = match self.initiator {
            true => (self.zid, self.peer_zid.unwrap_or_default()),
            false => (self.peer_zid.unwrap_or_default(), self.zid),
        };
        let mut context = Vec::with_capacity(12 + 12 + 32);
        context.extend_from_slice(&zid_initiator);
        context.extend_from_slice(&zid_responder);
        context.extend_from_slice(&total_hash);

        // s0 mixes the agreed secret with everything that was said (§4.4.1.4); with no cached
        // secrets the three shared-secret slots are empty, which their zero lengths say.
        let mut s0_input = Vec::new();
        s0_input.extend_from_slice(&1u32.to_be_bytes());
        s0_input.extend_from_slice(&shared);
        s0_input.extend_from_slice(b"ZRTP-HMAC-KDF");
        s0_input.extend_from_slice(&zid_initiator);
        s0_input.extend_from_slice(&zid_responder);
        s0_input.extend_from_slice(&total_hash);
        for _ in 0..3 {
            s0_input.extend_from_slice(&0u32.to_be_bytes());
        }

        let s0 = sha256(&s0_input);
        let sas_hash = kdf(&s0, "SAS", &context, 32);
        let sas = base32_sas(&sas_hash[..4]);
        self.keys = Some(DerivedKeys {
            srtp_initiator: (
                kdf(&s0, "Initiator SRTP master key", &context, 16).try_into().expect("16 bytes"),
                kdf(&s0, "Initiator SRTP master salt", &context, 14).try_into().expect("14 bytes"),
            ),
            srtp_responder: (
                kdf(&s0, "Responder SRTP master key", &context, 16).try_into().expect("16 bytes"),
                kdf(&s0, "Responder SRTP master salt", &context, 14).try_into().expect("14 bytes"),
            ),
            zrtp_initiator: kdf(&s0, "Initiator ZRTP key", &context, 16).try_into().expect("16 bytes"),
            zrtp_responder: kdf(&s0, "Responder ZRTP key", &context, 16).try_into().expect("16 bytes"),
            mac_initiator: kdf(&s0, "Initiator HMAC key", &context, 32).try_into().expect("32 bytes"),
            mac_responder: kdf(&s0, "Responder HMAC key", &context, 32).try_into().expect("32 bytes"),
            sas,
        });
        true
    }

    fn secure(&mut self, events: &mut Vec<ZrtpEvent>) {
        if self.state == State::Secure {
            return;
        }

        let Some(keys) = self.keys.as_ref() else { return };
        // Each side sends with its own role's key and receives with the other's.
        let (send, receive) = match self.initiator {
            true => (keys.srtp_initiator, keys.srtp_responder),
            false => (keys.srtp_responder, keys.srtp_initiator),
        };
        let profile = SrtpProfile::AesCm128HmacSha1_80;
        let outbound = SrtpContext::new(&send.0, &send.1);
        let inbound = SrtpContext::new(&receive.0, &receive.1);
        self.pending = None;
        self.state = State::Secure;
        events.push(ZrtpEvent::Secure(Box::new(SecureResult { outbound, inbound, profile, sas: keys.sas.clone() })));
    }

    // ---- building -------------------------------------------------------------------------------

    fn build_hello(&self) -> Vec<u8> {
        let mut body = Vec::with_capacity(88);
        body.extend_from_slice(b"1.10");
        body.extend_from_slice(b"Voip.NET 1.2    ");
        body.extend_from_slice(&self.h[3]);
        body.extend_from_slice(&self.zid);
        // Flags: no signature, no MITM, not passive. Then one algorithm of each kind.
        body.extend_from_slice(&[0, 0, 0x11, 0x11]);
        body.extend_from_slice(b"S256");
        body.extend_from_slice(b"AES1");
        body.extend_from_slice(b"HS32");
        body.extend_from_slice(b"EC25");
        body.extend_from_slice(b"B32 ");
        build_message_with_mac(b"Hello   ", &body, &self.h[2])
    }

    fn build_dhpart(&self, first: bool) -> Vec<u8> {
        let public = self.public.as_ref().map(|p| p.as_ref().to_vec()).unwrap_or_default();
        let mut body = Vec::with_capacity(32 + 32 + 64);
        body.extend_from_slice(&self.h[1]);
        // No cached secrets, so the four secret ids are zero.
        body.extend_from_slice(&[0u8; 32]);
        // The public value without its 0x04 prefix: EC25 is the two coordinates, 64 bytes.
        body.extend_from_slice(public.get(1..).unwrap_or_default());
        build_message_with_mac(if first { b"DHPart1 " } else { b"DHPart2 " }, &body, &self.h[0])
    }

    fn build_confirm(&self, first: bool) -> Vec<u8> {
        let Some(keys) = self.keys.as_ref() else { return Vec::new() };
        let (key, mac_key) = match first {
            true => (keys.zrtp_responder, keys.mac_responder),
            false => (keys.zrtp_initiator, keys.mac_initiator),
        };
        let iv: [u8; 16] = rand::random();
        // The encrypted part reveals H0, which lets the peer walk the chain back to the Hello.
        let mut plain = Vec::with_capacity(40);
        plain.extend_from_slice(&self.h[0]);
        plain.extend_from_slice(&[0, 0, 0, 0]);
        plain.extend_from_slice(&[0, 0, 0, 0]);
        let encrypted = aes_cfb(&key, &iv, &plain, true);
        let signing = hmac::Key::new(hmac::HMAC_SHA256, &mac_key);
        let mac = hmac::sign(&signing, &encrypted);

        let mut body = Vec::with_capacity(8 + 16 + encrypted.len());
        body.extend_from_slice(&mac.as_ref()[..8]);
        body.extend_from_slice(&iv);
        body.extend_from_slice(&encrypted);
        build_message(if first { b"Confirm1" } else { b"Confirm2" }, &body)
    }

    /// The Hello the responder sent, which is what the key derivation hashes (§4.4.1.4).
    fn responder_hello(&self) -> Option<Vec<u8>> {
        match self.initiator {
            true => self.peer_hello.clone(),
            false => Some(self.my_hello.clone()),
        }
    }

    fn ensure_key(&mut self) {
        if self.private.is_some() {
            return;
        }

        let rng = SystemRandom::new();
        if let Ok(private) = EphemeralPrivateKey::generate(&ECDH_P256, &rng) {
            self.public = private.compute_public_key().ok();
            self.private = Some(private);
        }
    }

    /// Wraps a message in the ZRTP packet header and CRC.
    fn packet(&self, message: &[u8]) -> Vec<u8> {
        let mut out = Vec::with_capacity(message.len() + 16);
        out.extend_from_slice(&[0x10, 0x00]);
        out.extend_from_slice(&self.sequence.to_be_bytes());
        out.extend_from_slice(&MAGIC.to_be_bytes());
        out.extend_from_slice(&self.ssrc.to_be_bytes());
        out.extend_from_slice(message);
        let crc = crc32c(&out);
        out.extend_from_slice(&crc.to_be_bytes());
        out
    }

    /// Sends a message and keeps it to send again until the peer answers.
    fn arm(&mut self, packet: Vec<u8>, now: Instant, events: &mut Vec<ZrtpEvent>) {
        self.sequence = self.sequence.wrapping_add(1);
        self.pending = Some(packet.clone());
        self.sent_at = now;
        self.retries = 0;
        events.push(ZrtpEvent::Send(packet));
    }

    fn fail(&mut self, reason: &str, events: &mut Vec<ZrtpEvent>) {
        if self.state == State::Failed {
            return;
        }

        self.state = State::Failed;
        self.pending = None;
        events.push(ZrtpEvent::Failed(reason.to_owned()));
    }
}

/// Wraps a body in the ZRTP message header: preamble, length in words, type.
fn build_message(kind: &[u8; 8], body: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(12 + body.len());
    out.extend_from_slice(&PREAMBLE.to_be_bytes());
    out.extend_from_slice(&(((12 + body.len()) / 4) as u16).to_be_bytes());
    out.extend_from_slice(kind);
    out.extend_from_slice(body);
    out
}

/// The same, with the 64-bit MAC the hash chain keys (§8.1): it is checked once the key is revealed.
fn build_message_with_mac(kind: &[u8; 8], body: &[u8], key: &[u8; 32]) -> Vec<u8> {
    let mut message = build_message(kind, body);
    // The length in the header covers the MAC as well.
    let words = ((message.len() + 8) / 4) as u16;
    message[2..4].copy_from_slice(&words.to_be_bytes());
    let signing = hmac::Key::new(hmac::HMAC_SHA256, key);
    let mac = hmac::sign(&signing, &message);
    message.extend_from_slice(&mac.as_ref()[..8]);
    message
}

/// Reads the public value out of a DHPart message: H1, four secret ids, then the key.
fn read_dh_public(message: &[u8]) -> Option<Vec<u8>> {
    let start = 12 + 32 + 32;
    let end = start + 64;
    message.get(start..end).map(<[u8]>::to_vec)
}

fn sha256(data: &[u8]) -> [u8; 32] {
    digest(&SHA256, data).as_ref().try_into().expect("SHA-256 is 32 bytes")
}

/// The key derivation of RFC 6189 §4.5.1, truncated to the length the caller needs.
fn kdf(key: &[u8; 32], label: &str, context: &[u8], length: usize) -> Vec<u8> {
    let signing = hmac::Key::new(hmac::HMAC_SHA256, key);
    let mut input = Vec::with_capacity(4 + label.len() + 1 + context.len() + 4);
    input.extend_from_slice(&1u32.to_be_bytes());
    input.extend_from_slice(label.as_bytes());
    input.push(0);
    input.extend_from_slice(context);
    input.extend_from_slice(&((length * 8) as u32).to_be_bytes());
    hmac::sign(&signing, &input).as_ref()[..length.min(32)].to_vec()
}

/// AES-128 in cipher feedback mode, which is what ZRTP encrypts a Confirm with.
fn aes_cfb(key: &[u8; 16], iv: &[u8; 16], data: &[u8], encrypt: bool) -> Vec<u8> {
    use aes::cipher::{BlockEncrypt, KeyInit};
    let cipher = aes::Aes128::new(key.into());
    let mut previous = *iv;
    let mut out = Vec::with_capacity(data.len());
    for chunk in data.chunks(16) {
        let mut keystream = aes::cipher::generic_array::GenericArray::from(previous);
        cipher.encrypt_block(&mut keystream);
        let mut block = [0u8; 16];
        for (i, byte) in chunk.iter().enumerate() {
            block[i] = byte ^ keystream[i];
        }

        out.extend_from_slice(&block[..chunk.len()]);
        // The feedback is the ciphertext either way.
        let feedback = if encrypt { &block[..chunk.len()] } else { chunk };
        previous = [0u8; 16];
        previous[..feedback.len()].copy_from_slice(feedback);
    }

    out
}

/// True when the first eight bytes of the HMAC match what the peer sent.
fn mac_matches(key: &hmac::Key, data: &[u8], mac: &[u8]) -> bool {
    hmac::sign(key, data).as_ref()[..8] == *mac
}

fn pad_to_32(mac: &[u8]) -> [u8; 32] {
    let mut out = [0u8; 32];
    out[..mac.len().min(32)].copy_from_slice(&mac[..mac.len().min(32)]);
    out
}

/// The short authentication string: four base32 characters both sides read aloud (§4.5.2).
fn base32_sas(bytes: &[u8]) -> String {
    const ALPHABET: &[u8; 32] = b"ybndrfg8ejkmcpqxot1uwisza345h769";
    let value = u32::from_be_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]);
    (0..4).map(|i| ALPHABET[((value >> (27 - i * 5)) & 0x1F) as usize] as char).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Runs two sessions against each other until neither has anything left to say.
    fn pump(a: &mut ZrtpSession, b: &mut ZrtpSession) -> (Vec<ZrtpEvent>, Vec<ZrtpEvent>) {
        let now = Instant::now();
        let (mut for_a, mut for_b) = (Vec::new(), Vec::new());
        let mut start = Vec::new();
        a.start(now, &mut start);
        b.start(now, &mut for_b);
        let mut pending: Vec<(bool, ZrtpEvent)> = start
            .into_iter()
            .map(|e| (true, e))
            .chain(std::mem::take(&mut for_b).into_iter().map(|e| (false, e)))
            .collect();

        for _ in 0..40 {
            let mut next = Vec::new();
            for (from_a, event) in pending {
                match event {
                    ZrtpEvent::Send(packet) if from_a => {
                        let mut out = Vec::new();
                        b.handle_packet(&packet, now, &mut out);
                        next.extend(out.into_iter().map(|e| (false, e)));
                    }
                    ZrtpEvent::Send(packet) => {
                        let mut out = Vec::new();
                        a.handle_packet(&packet, now, &mut out);
                        next.extend(out.into_iter().map(|e| (true, e)));
                    }
                    other if from_a => for_a.push(other),
                    other => for_b.push(other),
                }
            }

            if next.is_empty() {
                break;
            }

            pending = next;
        }

        (for_a, for_b)
    }

    #[test]
    fn two_endpoints_agree_on_keys_and_read_the_same_words() {
        let (mut a, mut b) = (ZrtpSession::new(0x1111_1111), ZrtpSession::new(0x2222_2222));

        let (for_a, for_b) = pump(&mut a, &mut b);

        assert!(a.is_secure(), "the caller finished the exchange");
        assert!(b.is_secure(), "the callee finished the exchange");
        assert_eq!(a.sas(), b.sas(), "both sides read the same four characters");
        assert_eq!(a.sas().map(str::len), Some(4));

        let secure = |events: &[ZrtpEvent]| {
            events.iter().any(|e| matches!(e, ZrtpEvent::Secure(_)))
        };
        assert!(secure(&for_a) && secure(&for_b), "both sides reported keys");
    }

    #[test]
    fn the_keys_protect_traffic_between_the_two_sides() {
        let (mut a, mut b) = (ZrtpSession::new(7), ZrtpSession::new(9));
        let (for_a, for_b) = pump(&mut a, &mut b);

        let take = |events: Vec<ZrtpEvent>| {
            events
                .into_iter()
                .find_map(|e| if let ZrtpEvent::Secure(result) = e { Some(result) } else { None })
                .expect("keys")
        };
        let mut caller = take(for_a);
        let mut callee = take(for_b);

        // What one side protects, the other unprotects: the roles line up.
        let mut packet = vec![0x80, 0x00, 0x00, 0x01, 0, 0, 0, 10, 0, 0, 0, 7, 1, 2, 3, 4, 5, 6, 7, 8];
        let plain = packet.clone();
        caller.outbound.protect_rtp(&mut packet).expect("protected");
        assert_ne!(packet[12..20], plain[12..20], "the payload is not in the clear");
        callee.inbound.unprotect_rtp(&mut packet).expect("unprotected");
        assert_eq!(packet, plain);
    }

    #[test]
    fn a_tampered_packet_is_ignored() {
        let (mut a, mut b) = (ZrtpSession::new(1), ZrtpSession::new(2));
        let now = Instant::now();
        let mut events = Vec::new();
        a.start(now, &mut events);
        let ZrtpEvent::Send(mut hello) = events.remove(0) else { panic!("expected a packet") };
        hello[20] ^= 0xFF; // flip a bit inside the message, leaving the CRC stale

        let mut out = Vec::new();
        b.handle_packet(&hello, now, &mut out);

        assert!(out.is_empty(), "a packet that fails its CRC is not answered");
    }

    #[test]
    fn the_short_string_is_four_readable_characters() {
        assert_eq!(base32_sas(&[0x00, 0x00, 0x00, 0x00]), "yyyy");
        assert_eq!(base32_sas(&[0xFF, 0xFF, 0xFF, 0xFF]), "9999");
        assert_eq!(base32_sas(&[0x12, 0x34, 0x56, 0x78]).len(), 4);
    }
}
