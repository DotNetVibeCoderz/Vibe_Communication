//! SCTP over DTLS for WebRTC data channels (RFC 4960, RFC 8261, RFC 8831, RFC 8832).
//!
//! Sans-IO, like the ICE agent: packets come in, events come out, and the media session does the
//! sending. Enough of SCTP for data channels beside a call — association setup with the cookie
//! exchange, ordered reliable DATA with SACKs and retransmission, message fragmentation, and the
//! DCEP handshake that names a channel — and no more. There is no multi-homing, no partial
//! reliability and no congestion control beyond a bounded amount of unacknowledged data: chat
//! messages and small files do not need them, and pretending otherwise would be the wrong kind of
//! complexity.

use std::collections::HashMap;
use std::time::{Duration, Instant};

/// WebRTC always uses port 5000 on both ends (RFC 8831 §6.2).
pub const WEBRTC_PORT: u16 = 5000;

const CHUNK_DATA: u8 = 0;
const CHUNK_INIT: u8 = 1;
const CHUNK_INIT_ACK: u8 = 2;
const CHUNK_SACK: u8 = 3;
const CHUNK_HEARTBEAT: u8 = 4;
const CHUNK_HEARTBEAT_ACK: u8 = 5;
const CHUNK_ABORT: u8 = 6;
const CHUNK_SHUTDOWN: u8 = 7;
const CHUNK_SHUTDOWN_ACK: u8 = 8;
const CHUNK_ERROR: u8 = 9;
const CHUNK_COOKIE_ECHO: u8 = 10;
const CHUNK_COOKIE_ACK: u8 = 11;
const CHUNK_SHUTDOWN_COMPLETE: u8 = 14;

const PARAM_STATE_COOKIE: u16 = 7;

/// Payload protocol identifiers a data channel uses (RFC 8831 §8).
const PPID_DCEP: u32 = 50;
const PPID_STRING: u32 = 51;
const PPID_BINARY: u32 = 53;
const PPID_BINARY_EMPTY: u32 = 57;
const PPID_STRING_EMPTY: u32 = 56;

/// DCEP message types (RFC 8832 §5).
const DCEP_ACK: u8 = 0x02;
const DCEP_OPEN: u8 = 0x03;

/// Largest message this side puts back together, advertised as `a=max-message-size` (RFC 8841).
pub const MAX_MESSAGE_SIZE: usize = 262_144;

/// Fits inside the DTLS record this rides in, which the engine builds with a 1200 byte MTU.
const MAX_PAYLOAD: usize = 1000;
/// How long to wait for a SACK before sending a chunk again.
const RTO: Duration = Duration::from_millis(500);
/// Data sent but not acknowledged, above which sending waits. Keeps a slow peer from being flooded.
const MAX_IN_FLIGHT: usize = 64 * 1024;

/// What the association wants the caller to do, or tells it happened.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SctpEvent {
    /// Send these bytes to the peer as DTLS application data.
    Send(Vec<u8>),
    /// A channel is open and can carry messages.
    ChannelOpen { stream: u16, label: String },
    /// A complete message arrived.
    Message { stream: u16, text: bool, data: Vec<u8> },
    /// The association ended, with the reason.
    Closed(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    Closed,
    CookieWait,
    CookieEchoed,
    Established,
}

/// A chunk that has been sent and is waiting for a SACK.
struct Pending {
    tsn: u32,
    packet: Vec<u8>,
    sent: Instant,
    bytes: usize,
}

/// A message being put back together from its fragments.
#[derive(Default)]
struct Reassembly {
    data: Vec<u8>,
    ppid: u32,
}

/// One SCTP association: the endpoint of a data channel connection.
pub struct SctpAssociation {
    client: bool,
    state: State,
    local_port: u16,
    remote_port: u16,
    local_tag: u32,
    peer_tag: u32,
    /// Cookie from the peer's INIT-ACK, echoed back to finish the handshake.
    cookie: Vec<u8>,
    next_tsn: u32,
    /// Highest TSN received in order, which every SACK reports.
    last_received_tsn: u32,
    /// TSNs received out of order, so a SACK can say what is still missing.
    out_of_order: Vec<u32>,
    advertised_window: u32,
    peer_window: u32,
    streams_out: u16,
    streams_in: u16,
    next_ssn: HashMap<u16, u16>,
    reassembly: HashMap<u16, Reassembly>,
    /// Channels this side opened and is waiting to be acknowledged.
    opening: HashMap<u16, String>,
    open_channels: HashMap<u16, String>,
    pending: Vec<Pending>,
    /// Retries left for the handshake before the association is given up on.
    handshake_attempts: u8,
    last_handshake: Option<Instant>,
    handshake_packet: Vec<u8>,
    next_stream: u16,
}

impl SctpAssociation {
    /// Creates an association. The DTLS client opens it (RFC 8832 §6 gives it the even streams).
    /// `client` follows the DTLS role, so both sides agree on who starts.
    pub fn new(client: bool) -> Self {
        Self {
            client,
            state: State::Closed,
            local_port: WEBRTC_PORT,
            remote_port: WEBRTC_PORT,
            local_tag: rand::random::<u32>().max(1),
            peer_tag: 0,
            cookie: Vec::new(),
            next_tsn: rand::random::<u32>().max(1),
            last_received_tsn: 0,
            out_of_order: Vec::new(),
            advertised_window: 256 * 1024,
            peer_window: 64 * 1024,
            streams_out: 16,
            streams_in: 16,
            next_ssn: HashMap::new(),
            reassembly: HashMap::new(),
            opening: HashMap::new(),
            open_channels: HashMap::new(),
            pending: Vec::new(),
            handshake_attempts: 0,
            last_handshake: None,
            handshake_packet: Vec::new(),
            // The side that opened DTLS uses even stream numbers, the other odd ones (RFC 8832 §6).
            next_stream: if client { 0 } else { 1 },
        }
    }

    /// True once messages can be sent.
    pub fn is_established(&self) -> bool {
        self.state == State::Established
    }

    /// Channels that are open, by stream number.
    pub fn channels(&self) -> impl Iterator<Item = (&u16, &String)> {
        self.open_channels.iter()
    }

    /// Starts the association. Only the client sends INIT; the server waits for it.
    pub fn connect(&mut self, now: Instant, events: &mut Vec<SctpEvent>) {
        if !self.client || self.state != State::Closed {
            return;
        }

        self.state = State::CookieWait;
        let init = self.build_init(CHUNK_INIT);
        self.handshake_packet = init.clone();
        self.handshake_attempts = 1;
        self.last_handshake = Some(now);
        events.push(SctpEvent::Send(init));
    }

    /// Opens a channel and tells the peer what it is for (DCEP OPEN). Returns the stream number.
    pub fn open_channel(&mut self, label: &str, now: Instant, events: &mut Vec<SctpEvent>) -> Option<u16> {
        if self.state != State::Established {
            return None;
        }

        let stream = self.next_stream;
        self.next_stream = self.next_stream.wrapping_add(2);
        // DCEP OPEN: reliable ordered channel, no priority, the label and an empty protocol.
        let mut message = vec![DCEP_OPEN, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        message.extend_from_slice(&(label.len() as u16).to_be_bytes());
        message.extend_from_slice(&0u16.to_be_bytes());
        message.extend_from_slice(label.as_bytes());
        self.opening.insert(stream, label.to_owned());
        self.send_message(stream, PPID_DCEP, &message, now, events);
        Some(stream)
    }

    /// Sends a text message on a channel.
    pub fn send_text(&mut self, stream: u16, text: &str, now: Instant, events: &mut Vec<SctpEvent>) -> bool {
        if self.state != State::Established {
            return false;
        }

        let ppid = if text.is_empty() { PPID_STRING_EMPTY } else { PPID_STRING };
        let payload = if text.is_empty() { vec![0] } else { text.as_bytes().to_vec() };
        self.send_message(stream, ppid, &payload, now, events);
        true
    }

    /// Sends a binary message on a channel.
    pub fn send_binary(&mut self, stream: u16, data: &[u8], now: Instant, events: &mut Vec<SctpEvent>) -> bool {
        if self.state != State::Established {
            return false;
        }

        let ppid = if data.is_empty() { PPID_BINARY_EMPTY } else { PPID_BINARY };
        let payload = if data.is_empty() { vec![0] } else { data.to_vec() };
        self.send_message(stream, ppid, &payload, now, events);
        true
    }

    /// Feeds a packet that arrived inside DTLS.
    pub fn handle_packet(&mut self, packet: &[u8], now: Instant, events: &mut Vec<SctpEvent>) {
        if packet.len() < 12 {
            return;
        }

        // The checksum covers the whole packet with its own field zeroed.
        let mut check = packet.to_vec();
        check[8..12].fill(0);
        let expected = u32::from_le_bytes([packet[8], packet[9], packet[10], packet[11]]);
        if crc32c(&check) != expected {
            return;
        }

        self.remote_port = u16::from_be_bytes([packet[0], packet[1]]);
        let mut at = 12;
        let mut sack_needed = false;
        while at + 4 <= packet.len() {
            let kind = packet[at];
            let length = u16::from_be_bytes([packet[at + 2], packet[at + 3]]) as usize;
            if length < 4 || at + length > packet.len() {
                break;
            }

            let body = &packet[at + 4..at + length];
            match kind {
                CHUNK_INIT => self.on_init(body, events),
                CHUNK_INIT_ACK => self.on_init_ack(body, now, events),
                CHUNK_COOKIE_ECHO => self.on_cookie_echo(events),
                CHUNK_COOKIE_ACK => self.on_cookie_ack(events),
                CHUNK_DATA => {
                    let flags = packet[at + 1];
                    self.on_data(flags, body, now, events);
                    sack_needed = true;
                }
                CHUNK_SACK => self.on_sack(body),
                CHUNK_HEARTBEAT => events.push(SctpEvent::Send(self.packet(&[chunk(CHUNK_HEARTBEAT_ACK, 0, body)]))),
                CHUNK_ABORT => {
                    self.state = State::Closed;
                    events.push(SctpEvent::Closed("the peer aborted the association".into()));
                }
                CHUNK_SHUTDOWN => {
                    events.push(SctpEvent::Send(self.packet(&[chunk(CHUNK_SHUTDOWN_ACK, 0, &[])])));
                    self.state = State::Closed;
                    events.push(SctpEvent::Closed("the peer shut the association down".into()));
                }
                CHUNK_SHUTDOWN_ACK => {
                    events.push(SctpEvent::Send(self.packet(&[chunk(CHUNK_SHUTDOWN_COMPLETE, 0, &[])])));
                    self.state = State::Closed;
                }
                CHUNK_ERROR | CHUNK_HEARTBEAT_ACK | CHUNK_SHUTDOWN_COMPLETE => {}
                _ => {}
            }

            at += length.div_ceil(4) * 4;
        }

        if sack_needed {
            let sack = self.build_sack();
            events.push(SctpEvent::Send(self.packet(&[sack])));
        }
    }

    /// Resends anything that has gone unacknowledged, and retries a handshake that got no answer.
    pub fn poll_timeout(&mut self, now: Instant, events: &mut Vec<SctpEvent>) {
        if matches!(self.state, State::CookieWait | State::CookieEchoed) {
            if self.last_handshake.is_some_and(|at| now.duration_since(at) > RTO * 2) {
                if self.handshake_attempts >= 5 {
                    self.state = State::Closed;
                    events.push(SctpEvent::Closed("the peer never answered the SCTP handshake".into()));
                    return;
                }

                self.handshake_attempts += 1;
                self.last_handshake = Some(now);
                events.push(SctpEvent::Send(self.handshake_packet.clone()));
            }

            return;
        }

        for chunk in &mut self.pending {
            if now.duration_since(chunk.sent) > RTO {
                chunk.sent = now;
                events.push(SctpEvent::Send(chunk.packet.clone()));
            }
        }
    }

    // ---- handshake --------------------------------------------------------------------------

    fn build_init(&self, kind: u8) -> Vec<u8> {
        let mut body = Vec::with_capacity(20);
        body.extend_from_slice(&self.local_tag.to_be_bytes());
        body.extend_from_slice(&self.advertised_window.to_be_bytes());
        body.extend_from_slice(&self.streams_out.to_be_bytes());
        body.extend_from_slice(&self.streams_in.to_be_bytes());
        body.extend_from_slice(&self.next_tsn.to_be_bytes());
        if kind == CHUNK_INIT_ACK {
            // The cookie is ours to choose; it only has to come back unchanged.
            let mut cookie = Vec::with_capacity(12);
            cookie.extend_from_slice(&PARAM_STATE_COOKIE.to_be_bytes());
            cookie.extend_from_slice(&12u16.to_be_bytes());
            cookie.extend_from_slice(&self.local_tag.to_be_bytes());
            cookie.extend_from_slice(&self.peer_tag.to_be_bytes());
            body.extend_from_slice(&cookie);
        }

        // INIT and INIT-ACK carry the peer's tag as the verification tag, which is zero for INIT.
        self.packet_with_tag(if kind == CHUNK_INIT { 0 } else { self.peer_tag }, &[chunk(kind, 0, &body)])
    }

    fn on_init(&mut self, body: &[u8], events: &mut Vec<SctpEvent>) {
        if body.len() < 16 {
            return;
        }

        self.peer_tag = u32::from_be_bytes([body[0], body[1], body[2], body[3]]);
        self.peer_window = u32::from_be_bytes([body[4], body[5], body[6], body[7]]);
        self.streams_in = u16::from_be_bytes([body[8], body[9]]);
        self.last_received_tsn = u32::from_be_bytes([body[12], body[13], body[14], body[15]]).wrapping_sub(1);
        events.push(SctpEvent::Send(self.build_init(CHUNK_INIT_ACK)));
    }

    fn on_init_ack(&mut self, body: &[u8], now: Instant, events: &mut Vec<SctpEvent>) {
        if body.len() < 16 || self.state != State::CookieWait {
            return;
        }

        self.peer_tag = u32::from_be_bytes([body[0], body[1], body[2], body[3]]);
        self.peer_window = u32::from_be_bytes([body[4], body[5], body[6], body[7]]);
        self.streams_in = u16::from_be_bytes([body[8], body[9]]);
        self.last_received_tsn = u32::from_be_bytes([body[12], body[13], body[14], body[15]]).wrapping_sub(1);

        // Find the cookie among the parameters and echo it back.
        let mut at = 16;
        while at + 4 <= body.len() {
            let kind = u16::from_be_bytes([body[at], body[at + 1]]);
            let length = u16::from_be_bytes([body[at + 2], body[at + 3]]) as usize;
            if length < 4 || at + length > body.len() {
                break;
            }

            if kind == PARAM_STATE_COOKIE {
                self.cookie = body[at + 4..at + length].to_vec();
            }

            at += length.div_ceil(4) * 4;
        }

        let echo = self.packet(&[chunk(CHUNK_COOKIE_ECHO, 0, &self.cookie.clone())]);
        self.state = State::CookieEchoed;
        self.handshake_packet = echo.clone();
        self.handshake_attempts = 1;
        self.last_handshake = Some(now);
        events.push(SctpEvent::Send(echo));
    }

    fn on_cookie_echo(&mut self, events: &mut Vec<SctpEvent>) {
        // The cookie is ours, so nothing to check beyond having got one back.
        events.push(SctpEvent::Send(self.packet(&[chunk(CHUNK_COOKIE_ACK, 0, &[])])));
        if self.state != State::Established {
            self.state = State::Established;
        }
    }

    fn on_cookie_ack(&mut self, _events: &mut Vec<SctpEvent>) {
        self.state = State::Established;
        self.handshake_packet.clear();
    }

    // ---- data -------------------------------------------------------------------------------

    fn send_message(&mut self, stream: u16, ppid: u32, payload: &[u8], now: Instant, events: &mut Vec<SctpEvent>) {
        let ssn = {
            let entry = self.next_ssn.entry(stream).or_insert(0);
            let ssn = *entry;
            *entry = entry.wrapping_add(1);
            ssn
        };

        let chunks: Vec<&[u8]> = payload.chunks(MAX_PAYLOAD).collect();
        for (index, part) in chunks.iter().enumerate() {
            let mut body = Vec::with_capacity(12 + part.len());
            body.extend_from_slice(&self.next_tsn.to_be_bytes());
            body.extend_from_slice(&stream.to_be_bytes());
            body.extend_from_slice(&ssn.to_be_bytes());
            body.extend_from_slice(&ppid.to_be_bytes());
            body.extend_from_slice(part);
            // B and E mark the first and last fragment of a message (RFC 4960 §3.3.1).
            let flags = u8::from(index == 0) << 1 | u8::from(index == chunks.len() - 1);
            let packet = self.packet(&[chunk(CHUNK_DATA, flags, &body)]);
            let tsn = self.next_tsn;
            self.next_tsn = self.next_tsn.wrapping_add(1);
            if self.in_flight() < MAX_IN_FLIGHT {
                events.push(SctpEvent::Send(packet.clone()));
            }

            self.pending.push(Pending { tsn, packet, sent: now, bytes: part.len() });
        }
    }

    fn in_flight(&self) -> usize {
        self.pending.iter().map(|p| p.bytes).sum()
    }

    fn on_data(&mut self, flags: u8, body: &[u8], now: Instant, events: &mut Vec<SctpEvent>) {
        if body.len() < 12 {
            return;
        }

        let tsn = u32::from_be_bytes([body[0], body[1], body[2], body[3]]);
        let stream = u16::from_be_bytes([body[4], body[5]]);
        let ppid = u32::from_be_bytes([body[8], body[9], body[10], body[11]]);
        let payload = &body[12..];

        // Track what has been seen so the SACK is honest, and ignore anything already delivered.
        if tsn == self.last_received_tsn.wrapping_add(1) {
            self.last_received_tsn = tsn;
            self.out_of_order.retain(|t| *t != tsn);
            while let Some(position) = self.out_of_order.iter().position(|t| *t == self.last_received_tsn.wrapping_add(1)) {
                self.last_received_tsn = self.out_of_order.remove(position);
            }
        } else if tsn.wrapping_sub(self.last_received_tsn) < u32::MAX / 2 {
            if self.out_of_order.contains(&tsn) {
                return;
            }

            self.out_of_order.push(tsn);
        } else {
            return; // already delivered
        }

        let entry = self.reassembly.entry(stream).or_default();
        if flags & 0x02 != 0 {
            entry.data.clear();
            entry.ppid = ppid;
        }

        if entry.data.len() + payload.len() > MAX_MESSAGE_SIZE {
            // Past what was advertised: drop the message rather than grow without bound.
            entry.data.clear();
            self.reassembly.remove(&stream);
            return;
        }

        entry.data.extend_from_slice(payload);
        if flags & 0x01 == 0 {
            return; // more fragments to come
        }

        let message = std::mem::take(&mut entry.data);
        let ppid = entry.ppid;
        self.reassembly.remove(&stream);
        match ppid {
            PPID_DCEP => self.on_dcep(stream, &message, now, events),
            PPID_STRING | PPID_STRING_EMPTY => events.push(SctpEvent::Message {
                stream,
                text: true,
                data: if ppid == PPID_STRING_EMPTY { Vec::new() } else { message },
            }),
            PPID_BINARY | PPID_BINARY_EMPTY => events.push(SctpEvent::Message {
                stream,
                text: false,
                data: if ppid == PPID_BINARY_EMPTY { Vec::new() } else { message },
            }),
            _ => {}
        }
    }

    fn on_dcep(&mut self, stream: u16, message: &[u8], now: Instant, events: &mut Vec<SctpEvent>) {
        match message.first().copied() {
            Some(DCEP_OPEN) if message.len() >= 12 => {
                let label_length = u16::from_be_bytes([message[8], message[9]]) as usize;
                let label = message
                    .get(12..12 + label_length)
                    .map(|l| String::from_utf8_lossy(l).into_owned())
                    .unwrap_or_default();
                // The peer picked the stream; answering on it is what opens the channel.
                self.send_message(stream, PPID_DCEP, &[DCEP_ACK], now, events);
                self.open_channels.insert(stream, label.clone());
                events.push(SctpEvent::ChannelOpen { stream, label });
            }
            Some(DCEP_ACK) => {
                if let Some(label) = self.opening.remove(&stream) {
                    self.open_channels.insert(stream, label.clone());
                    events.push(SctpEvent::ChannelOpen { stream, label });
                }
            }
            _ => {}
        }
    }

    fn on_sack(&mut self, body: &[u8]) {
        if body.len() < 12 {
            return;
        }

        let cumulative = u32::from_be_bytes([body[0], body[1], body[2], body[3]]);
        self.peer_window = u32::from_be_bytes([body[4], body[5], body[6], body[7]]);
        let gap_blocks = u16::from_be_bytes([body[8], body[9]]) as usize;
        let mut acked: Vec<u32> = Vec::new();
        for i in 0..gap_blocks {
            let at = 12 + i * 4;
            if at + 4 > body.len() {
                break;
            }

            let start = u16::from_be_bytes([body[at], body[at + 1]]) as u32;
            let end = u16::from_be_bytes([body[at + 2], body[at + 3]]) as u32;
            for offset in start..=end.max(start) {
                acked.push(cumulative.wrapping_add(offset));
            }
        }

        self.pending
            .retain(|p| p.tsn.wrapping_sub(cumulative) < u32::MAX / 2 && p.tsn != cumulative && !acked.contains(&p.tsn));
    }

    fn build_sack(&self) -> Vec<u8> {
        let mut body = Vec::with_capacity(16);
        body.extend_from_slice(&self.last_received_tsn.to_be_bytes());
        body.extend_from_slice(&self.advertised_window.to_be_bytes());
        // Gap blocks report what arrived out of order, relative to the cumulative TSN.
        let mut gaps: Vec<u32> = self.out_of_order.clone();
        gaps.sort_unstable();
        body.extend_from_slice(&(gaps.len() as u16).to_be_bytes());
        body.extend_from_slice(&0u16.to_be_bytes()); // no duplicates reported
        for tsn in gaps {
            let offset = tsn.wrapping_sub(self.last_received_tsn) as u16;
            body.extend_from_slice(&offset.to_be_bytes());
            body.extend_from_slice(&offset.to_be_bytes());
        }

        chunk(CHUNK_SACK, 0, &body)
    }

    // ---- packets ----------------------------------------------------------------------------

    fn packet(&self, chunks: &[Vec<u8>]) -> Vec<u8> {
        self.packet_with_tag(self.peer_tag, chunks)
    }

    fn packet_with_tag(&self, tag: u32, chunks: &[Vec<u8>]) -> Vec<u8> {
        let mut packet = Vec::with_capacity(12 + chunks.iter().map(Vec::len).sum::<usize>());
        packet.extend_from_slice(&self.local_port.to_be_bytes());
        packet.extend_from_slice(&self.remote_port.to_be_bytes());
        packet.extend_from_slice(&tag.to_be_bytes());
        packet.extend_from_slice(&[0, 0, 0, 0]); // checksum, filled in below
        for c in chunks {
            packet.extend_from_slice(c);
        }

        let checksum = crc32c(&packet);
        packet[8..12].copy_from_slice(&checksum.to_le_bytes());
        packet
    }
}

/// Builds one chunk: type, flags, length, body, padded to four bytes.
fn chunk(kind: u8, flags: u8, body: &[u8]) -> Vec<u8> {
    let length = 4 + body.len();
    let mut out = Vec::with_capacity(length.div_ceil(4) * 4);
    out.push(kind);
    out.push(flags);
    out.extend_from_slice(&(length as u16).to_be_bytes());
    out.extend_from_slice(body);
    out.resize(length.div_ceil(4) * 4, 0);
    out
}

/// CRC-32c (Castagnoli), which is what SCTP checksums with — not the CRC-32 of zip files.
pub(crate) fn crc32c(data: &[u8]) -> u32 {
    let mut crc = 0xFFFF_FFFFu32;
    for byte in data {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ 0x82F6_3B78 } else { crc >> 1 };
        }
    }

    crc ^ 0xFFFF_FFFF
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Runs two associations against each other until they stop having anything to say.
    fn pump(a: &mut SctpAssociation, b: &mut SctpAssociation, start: Vec<SctpEvent>) -> (Vec<SctpEvent>, Vec<SctpEvent>) {
        let now = Instant::now();
        let (mut for_a, mut for_b) = (Vec::new(), Vec::new());
        let mut pending: Vec<(bool, SctpEvent)> = start.into_iter().map(|e| (true, e)).collect();
        for _ in 0..40 {
            let mut next = Vec::new();
            for (from_a, event) in pending {
                match event {
                    SctpEvent::Send(packet) if from_a => {
                        let mut out = Vec::new();
                        b.handle_packet(&packet, now, &mut out);
                        next.extend(out.into_iter().map(|e| (false, e)));
                    }
                    SctpEvent::Send(packet) => {
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
    fn the_cookie_handshake_establishes_an_association() {
        let (mut client, mut server) = (SctpAssociation::new(true), SctpAssociation::new(false));
        let mut events = Vec::new();
        client.connect(Instant::now(), &mut events);

        pump(&mut client, &mut server, events);

        assert!(client.is_established());
        assert!(server.is_established());
    }

    #[test]
    fn a_channel_opens_on_both_sides_and_carries_a_message() {
        let (mut client, mut server) = (SctpAssociation::new(true), SctpAssociation::new(false));
        let mut events = Vec::new();
        let now = Instant::now();
        client.connect(now, &mut events);
        pump(&mut client, &mut server, events);

        let mut events = Vec::new();
        let stream = client.open_channel("chat", now, &mut events).expect("a stream number");
        assert_eq!(stream % 2, 0, "the DTLS client uses even streams");
        let (for_client, for_server) = pump(&mut client, &mut server, events);

        assert!(matches!(for_server.first(), Some(SctpEvent::ChannelOpen { label, .. }) if label == "chat"));
        assert!(matches!(for_client.first(), Some(SctpEvent::ChannelOpen { label, .. }) if label == "chat"));

        let mut events = Vec::new();
        assert!(client.send_text(stream, "halo dunia", now, &mut events));
        let (_, for_server) = pump(&mut client, &mut server, events);

        let message = for_server
            .iter()
            .find_map(|e| match e {
                SctpEvent::Message { text: true, data, stream } => Some((*stream, String::from_utf8_lossy(data).into_owned())),
                _ => None,
            })
            .expect("the message arrived");
        assert_eq!(message, (stream, "halo dunia".to_owned()));
    }

    #[test]
    fn a_long_message_is_split_and_put_back_together() {
        let (mut client, mut server) = (SctpAssociation::new(true), SctpAssociation::new(false));
        let now = Instant::now();
        let mut events = Vec::new();
        client.connect(now, &mut events);
        pump(&mut client, &mut server, events);
        let mut events = Vec::new();
        let stream = client.open_channel("files", now, &mut events).unwrap();
        pump(&mut client, &mut server, events);

        // Three fragments' worth, so both the B and E bits and the reassembly buffer are exercised.
        let payload: Vec<u8> = (0..2500).map(|i| (i % 251) as u8).collect();
        let mut events = Vec::new();
        assert!(client.send_binary(stream, &payload, now, &mut events));
        let (_, for_server) = pump(&mut client, &mut server, events);

        let received = for_server
            .iter()
            .find_map(|e| match e {
                SctpEvent::Message { text: false, data, .. } => Some(data.clone()),
                _ => None,
            })
            .expect("the binary message arrived");
        assert_eq!(received, payload);
    }

    #[test]
    fn a_packet_with_a_broken_checksum_is_ignored() {
        let (mut client, mut server) = (SctpAssociation::new(true), SctpAssociation::new(false));
        let mut events = Vec::new();
        client.connect(Instant::now(), &mut events);
        let SctpEvent::Send(mut packet) = events.remove(0) else { panic!("expected a packet") };
        packet[12] ^= 0xFF; // flip a bit in the chunk, leaving the checksum stale

        let mut out = Vec::new();
        server.handle_packet(&packet, Instant::now(), &mut out);

        assert!(out.is_empty(), "a corrupt packet must not be answered");
        assert!(!server.is_established());
    }

    #[test]
    fn the_checksum_matches_the_reference_value() {
        // RFC 3720 B.4: CRC-32c of 32 bytes of zeroes.
        assert_eq!(crc32c(&[0u8; 32]), 0x8A91_36AA);
        assert_eq!(crc32c(b"123456789"), 0xE306_9283);
    }
}
