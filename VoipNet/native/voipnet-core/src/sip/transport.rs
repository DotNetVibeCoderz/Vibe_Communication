//! SIP transports: UDP, TCP, TLS (RFC 3261 §18, §26) and WebSocket (RFC 7118, `ws`/`wss`).
//!
//! Stream transports share one connection model: a byte stream (plain TCP or rustls over TCP),
//! optionally carrying WebSocket frames, with SIP messages framed by Content-Length.

use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{Shutdown, SocketAddr, TcpListener, TcpStream, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::{Duration, Instant};

use base64::Engine;
use parking_lot::Mutex;
use rustls::pki_types::ServerName;
use serde::Deserialize;
use sha1::{Digest, Sha1};

use super::message::{ParseError, SipMessage};
use super::tls::TlsContext;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TransportKind {
    #[default]
    Udp,
    Tcp,
    Tls,
    Ws,
    Wss,
}

impl TransportKind {
    pub fn via_name(self) -> &'static str {
        match self {
            Self::Udp => "UDP",
            Self::Tcp => "TCP",
            Self::Tls => "TLS",
            Self::Ws => "WS",
            Self::Wss => "WSS",
        }
    }

    pub fn uri_param(self) -> Option<&'static str> {
        match self {
            Self::Udp => None,
            Self::Tcp => Some("tcp"),
            Self::Tls => Some("tls"),
            Self::Ws => Some("ws"),
            Self::Wss => Some("wss"),
        }
    }

    pub fn default_port(self) -> u16 {
        match self {
            Self::Udp | Self::Tcp => 5060,
            Self::Tls => 5061,
            Self::Ws => 80,
            Self::Wss => 443,
        }
    }

    fn secure(self) -> bool {
        matches!(self, Self::Tls | Self::Wss)
    }

    fn websocket(self) -> bool {
        matches!(self, Self::Ws | Self::Wss)
    }
}

pub type MessageHandler = Arc<dyn Fn(SipMessage, SocketAddr) + Send + Sync>;

const WS_GUID: &str = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
const WS_CONNECTING: u8 = 0;
const WS_OPEN: u8 = 1;
/// Largest accepted WebSocket frame or HTTP upgrade request.
const MAX_FRAME: usize = 1 << 20;

enum Wire {
    Tcp,
    /// rustls state, shared by the reader and senders; never locked across a blocking read.
    Tls(Mutex<rustls::Connection>),
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum WsRole {
    Server,
    Client,
}

/// One stream connection.
struct Conn {
    sock: TcpStream,
    wire: Wire,
    ws: Option<WsRole>,
    ws_state: AtomicU8,
    failed: AtomicBool,
    /// Serializes writes so frames and messages from different threads never interleave.
    write_lock: Mutex<()>,
}

impl Conn {
    fn new(sock: TcpStream, wire: Wire, ws: Option<WsRole>) -> Self {
        let _ = sock.set_nodelay(true);
        Self { sock, wire, ws, ws_state: AtomicU8::new(WS_CONNECTING), failed: AtomicBool::new(false), write_lock: Mutex::new(()) }
    }

    fn is_handshaking(&self) -> bool {
        let tls = match &self.wire {
            Wire::Tls(conn) => conn.lock().is_handshaking(),
            Wire::Tcp => false,
        };
        tls || (self.ws.is_some() && self.ws_state.load(Ordering::Relaxed) != WS_OPEN)
    }

    /// Writes bytes to the stream (encrypting them for TLS).
    fn write_raw(&self, data: &[u8]) -> std::io::Result<()> {
        match &self.wire {
            Wire::Tcp => {
                let _g = self.write_lock.lock();
                (&self.sock).write_all(data)
            }
            Wire::Tls(conn) => {
                let mut conn = conn.lock();
                conn.writer().write_all(data)?;
                flush_tls(&self.sock, &mut conn)
            }
        }
    }

    /// Sends one SIP message, framed for WebSocket when needed.
    fn send_message(&self, data: &[u8]) -> std::io::Result<()> {
        match self.ws {
            None => self.write_raw(data),
            Some(role) => {
                let mut frame = Vec::with_capacity(data.len() + 14);
                encode_frame(0x1, data, role == WsRole::Client, &mut frame);
                self.write_raw(&frame)
            }
        }
    }

    fn close(&self) {
        if let Wire::Tls(conn) = &self.wire {
            let mut conn = conn.lock();
            conn.send_close_notify();
            let _ = flush_tls(&self.sock, &mut conn);
        }
        let _ = self.sock.shutdown(Shutdown::Both);
    }
}

fn flush_tls(sock: &TcpStream, conn: &mut rustls::Connection) -> std::io::Result<()> {
    let mut writer = sock;
    while conn.wants_write() {
        conn.write_tls(&mut writer)?;
    }
    Ok(())
}

/// Encodes a single final WebSocket frame (RFC 6455 §5.2). Clients must mask.
fn encode_frame(opcode: u8, payload: &[u8], mask: bool, out: &mut Vec<u8>) {
    out.push(0x80 | opcode);
    let mask_bit = if mask { 0x80 } else { 0 };
    match payload.len() {
        n if n < 126 => out.push(mask_bit | n as u8),
        n if n <= 0xFFFF => {
            out.push(mask_bit | 126);
            out.extend_from_slice(&(n as u16).to_be_bytes());
        }
        n => {
            out.push(mask_bit | 127);
            out.extend_from_slice(&(n as u64).to_be_bytes());
        }
    }
    if mask {
        let key: [u8; 4] = rand::random();
        out.extend_from_slice(&key);
        out.extend(payload.iter().enumerate().map(|(i, b)| b ^ key[i % 4]));
    } else {
        out.extend_from_slice(payload);
    }
}

/// A decoded frame: (fin, opcode, payload, bytes consumed).
fn decode_frame(buf: &[u8]) -> Result<Option<(bool, u8, Vec<u8>, usize)>, ()> {
    if buf.len() < 2 {
        return Ok(None);
    }
    let fin = buf[0] & 0x80 != 0;
    let opcode = buf[0] & 0x0F;
    let masked = buf[1] & 0x80 != 0;
    let (len, mut pos) = match buf[1] & 0x7F {
        126 if buf.len() >= 4 => (u16::from_be_bytes([buf[2], buf[3]]) as usize, 4),
        127 if buf.len() >= 10 => (u64::from_be_bytes(buf[2..10].try_into().expect("8 bytes")) as usize, 10),
        126 | 127 => return Ok(None),
        n => (n as usize, 2),
    };
    if len > MAX_FRAME {
        return Err(());
    }
    let key = if masked {
        if buf.len() < pos + 4 {
            return Ok(None);
        }
        let k = [buf[pos], buf[pos + 1], buf[pos + 2], buf[pos + 3]];
        pos += 4;
        Some(k)
    } else {
        None
    };
    if buf.len() < pos + len {
        return Ok(None);
    }
    let mut payload = buf[pos..pos + len].to_vec();
    if let Some(k) = key {
        payload.iter_mut().enumerate().for_each(|(i, b)| *b ^= k[i % 4]);
    }
    Ok(Some((fin, opcode, payload, pos + len)))
}

fn header_value<'a>(head: &'a str, name: &str) -> Option<&'a str> {
    head.lines().skip(1).find_map(|l| {
        let (k, v) = l.split_once(':')?;
        k.trim().eq_ignore_ascii_case(name).then(|| v.trim())
    })
}

fn ws_accept(key: &str) -> String {
    let digest = Sha1::digest(format!("{key}{WS_GUID}").as_bytes());
    base64::engine::general_purpose::STANDARD.encode(digest)
}

pub struct Transport {
    kind: TransportKind,
    local: SocketAddr,
    udp: Option<UdpSocket>,
    listener: Option<TcpListener>,
    connections: Mutex<HashMap<SocketAddr, Arc<Conn>>>,
    tls: Option<Arc<TlsContext>>,
    server_names: Mutex<HashMap<SocketAddr, String>>,
    handler: OnceLock<MessageHandler>,
    running: AtomicBool,
    threads: Mutex<Vec<std::thread::JoinHandle<()>>>,
}

impl Transport {
    pub fn bind(kind: TransportKind, addr: SocketAddr, tls: Option<Arc<TlsContext>>) -> std::io::Result<Arc<Self>> {
        if kind.secure() && tls.is_none() {
            return Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, "secure transport requires a TLS context"));
        }
        let (udp, listener, local) = match kind {
            TransportKind::Udp => {
                let s = UdpSocket::bind(addr)?;
                s.set_read_timeout(Some(Duration::from_millis(200)))?;
                let local = s.local_addr()?;
                (Some(s), None, local)
            }
            _ => {
                let l = TcpListener::bind(addr)?;
                l.set_nonblocking(true)?;
                let local = l.local_addr()?;
                (None, Some(l), local)
            }
        };
        Ok(Arc::new(Self {
            kind,
            local,
            udp,
            listener,
            connections: Mutex::new(HashMap::new()),
            tls,
            server_names: Mutex::new(HashMap::new()),
            handler: OnceLock::new(),
            running: AtomicBool::new(true),
            threads: Mutex::new(Vec::new()),
        }))
    }

    pub fn kind(&self) -> TransportKind {
        self.kind
    }

    pub fn local_addr(&self) -> SocketAddr {
        self.local
    }

    pub fn is_reliable(&self) -> bool {
        self.kind != TransportKind::Udp
    }

    /// SHA-256 fingerprint of the certificate this transport presents, for TLS and WSS.
    pub fn tls_fingerprint(&self) -> Option<&str> {
        self.tls.as_ref().filter(|_| self.kind.secure()).map(|t| t.fingerprint.as_str())
    }

    pub fn start(self: &Arc<Self>, handler: MessageHandler) {
        let _ = self.handler.set(handler);
        let me = self.clone();
        let t = std::thread::Builder::new()
            .name("voipnet-sip-transport".into())
            .spawn(move || match me.kind {
                TransportKind::Udp => me.udp_loop(),
                _ => me.accept_loop(),
            })
            .expect("spawn sip transport thread");
        self.threads.lock().push(t);
    }

    fn dispatch(&self, msg: SipMessage, from: SocketAddr) {
        if let Some(h) = self.handler.get() {
            h(msg, from);
        }
    }

    fn udp_loop(&self) {
        let sock = self.udp.as_ref().expect("udp transport");
        let mut buf = vec![0u8; 65535];
        while self.running.load(Ordering::Relaxed) {
            match sock.recv_from(&mut buf) {
                Ok((n, from)) => {
                    let data = &buf[..n];
                    if data.iter().all(|b| b.is_ascii_whitespace()) {
                        continue; // CRLF keep-alive
                    }
                    if let Ok((msg, _)) = SipMessage::parse(data) {
                        self.dispatch(msg, from);
                    }
                }
                Err(ref e)
                    if matches!(
                        e.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut | std::io::ErrorKind::ConnectionReset
                    ) => {}
                Err(_) => std::thread::sleep(Duration::from_millis(10)),
            }
        }
    }

    fn accept_loop(self: &Arc<Self>) {
        let listener = self.listener.as_ref().expect("stream transport");
        while self.running.load(Ordering::Relaxed) {
            match listener.accept() {
                Ok((sock, peer)) => {
                    let _ = sock.set_nonblocking(false);
                    let wire = match (self.kind.secure(), self.tls.as_ref()) {
                        (true, Some(ctx)) => match rustls::ServerConnection::new(ctx.server.clone()) {
                            Ok(c) => Wire::Tls(Mutex::new(c.into())),
                            Err(_) => continue,
                        },
                        _ => Wire::Tcp,
                    };
                    let ws = self.kind.websocket().then_some(WsRole::Server);
                    self.adopt(Arc::new(Conn::new(sock, wire, ws)), peer);
                }
                Err(_) => std::thread::sleep(Duration::from_millis(20)),
            }
        }
    }

    fn adopt(self: &Arc<Self>, conn: Arc<Conn>, peer: SocketAddr) {
        self.connections.lock().insert(peer, conn.clone());
        let me = self.clone();
        let _ = std::thread::Builder::new().name("voipnet-sip-stream".into()).spawn(move || me.stream_loop(conn, peer));
    }

    fn stream_loop(&self, conn: Arc<Conn>, peer: SocketAddr) {
        let _ = conn.sock.set_read_timeout(Some(Duration::from_millis(500)));
        let mut raw = vec![0u8; 16384];
        let mut plain = vec![0u8; 16384];
        let mut inbuf: Vec<u8> = Vec::with_capacity(8192);
        let mut fragments: Vec<u8> = Vec::new();
        'outer: while self.running.load(Ordering::Relaxed) {
            let n = match (&conn.sock).read(&mut raw) {
                Ok(0) => break,
                Ok(n) => n,
                Err(ref e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => continue,
                Err(_) => break,
            };
            match &conn.wire {
                Wire::Tcp => inbuf.extend_from_slice(&raw[..n]),
                Wire::Tls(tls) => {
                    let mut tls = tls.lock();
                    let mut input = &raw[..n];
                    while !input.is_empty() {
                        if tls.read_tls(&mut input).is_err() {
                            break 'outer;
                        }
                        let processed = tls.process_new_packets();
                        // Handshake records and alerts go out before a failure is reported.
                        let _ = flush_tls(&conn.sock, &mut tls);
                        if processed.is_err() {
                            break 'outer;
                        }
                        loop {
                            match tls.reader().read(&mut plain) {
                                Ok(0) => break 'outer, // close_notify
                                Ok(m) => inbuf.extend_from_slice(&plain[..m]),
                                Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => break,
                                Err(_) => break 'outer,
                            }
                        }
                    }
                }
            }
            let ok = match conn.ws {
                None => {
                    self.drain_messages(&mut inbuf, peer);
                    true
                }
                Some(role) => self.process_websocket(&conn, role, &mut inbuf, &mut fragments, peer),
            };
            if !ok || inbuf.len() > MAX_FRAME + 16 {
                break;
            }
        }
        conn.failed.store(true, Ordering::Relaxed);
        conn.close();
        let mut conns = self.connections.lock();
        if conns.get(&peer).is_some_and(|c| Arc::ptr_eq(c, &conn)) {
            conns.remove(&peer);
        }
    }

    /// Handles the upgrade and frames of a WebSocket connection. Returns false to close it.
    fn process_websocket(&self, conn: &Conn, role: WsRole, inbuf: &mut Vec<u8>, fragments: &mut Vec<u8>, peer: SocketAddr) -> bool {
        if conn.ws_state.load(Ordering::Relaxed) == WS_CONNECTING {
            let Some(end) = inbuf.windows(4).position(|w| w == b"\r\n\r\n") else {
                return inbuf.len() < 16384;
            };
            let head = String::from_utf8_lossy(&inbuf[..end]).into_owned();
            inbuf.drain(..end + 4);
            match role {
                WsRole::Server => {
                    let upgrade = header_value(&head, "Upgrade").is_some_and(|v| v.eq_ignore_ascii_case("websocket"));
                    let Some(key) = header_value(&head, "Sec-WebSocket-Key").filter(|_| upgrade && head.starts_with("GET ")) else {
                        let _ = conn.write_raw(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n");
                        return false;
                    };
                    // RFC 7118 §4: the "sip" subprotocol must be negotiated.
                    let sip = header_value(&head, "Sec-WebSocket-Protocol")
                        .is_some_and(|p| p.split(',').any(|s| s.trim().eq_ignore_ascii_case("sip")));
                    if !sip {
                        let _ = conn.write_raw(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n");
                        return false;
                    }
                    let response = format!(
                        "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {}\r\nSec-WebSocket-Protocol: sip\r\n\r\n",
                        ws_accept(key)
                    );
                    if conn.write_raw(response.as_bytes()).is_err() {
                        return false;
                    }
                }
                WsRole::Client => {
                    if !head.starts_with("HTTP/1.1 101") {
                        return false;
                    }
                }
            }
            conn.ws_state.store(WS_OPEN, Ordering::Relaxed);
        }
        loop {
            let (fin, opcode, payload, used) = match decode_frame(inbuf) {
                Ok(Some(f)) => f,
                Ok(None) => return true,
                Err(()) => return false,
            };
            inbuf.drain(..used);
            match opcode {
                0x0 | 0x1 | 0x2 => {
                    fragments.extend_from_slice(&payload);
                    if fragments.len() > MAX_FRAME {
                        return false;
                    }
                    if fin {
                        let mut message = std::mem::take(fragments);
                        self.drain_messages(&mut message, peer);
                    }
                }
                0x8 => {
                    let mut frame = Vec::new();
                    encode_frame(0x8, &payload[..payload.len().min(2)], role == WsRole::Client, &mut frame);
                    let _ = conn.write_raw(&frame);
                    return false;
                }
                0x9 => {
                    let mut frame = Vec::new();
                    encode_frame(0xA, &payload, role == WsRole::Client, &mut frame);
                    let _ = conn.write_raw(&frame);
                }
                _ => {}
            }
        }
    }

    /// Parses complete messages out of a stream buffer, skipping CRLF keep-alives.
    fn drain_messages(&self, buf: &mut Vec<u8>, peer: SocketAddr) {
        loop {
            let skip = buf.iter().take_while(|b| **b == b'\r' || **b == b'\n').count();
            if skip > 0 {
                buf.drain(..skip);
            }
            if buf.is_empty() {
                break;
            }
            match SipMessage::parse(buf) {
                Ok((msg, used)) => {
                    buf.drain(..used);
                    self.dispatch(msg, peer);
                }
                Err(ParseError::Incomplete) => break,
                Err(_) => {
                    buf.clear();
                    break;
                }
            }
        }
    }

    /// Remembers the host name behind an address so TLS can send SNI and verify the certificate name.
    pub fn note_server_name(&self, addr: SocketAddr, host: &str) {
        let host = host.trim_matches(|c| c == '[' || c == ']');
        if self.kind != TransportKind::Udp && !host.is_empty() && host.parse::<std::net::IpAddr>().is_err() {
            self.server_names.lock().insert(addr, host.to_ascii_lowercase());
        }
    }

    fn connect(self: &Arc<Self>, dest: SocketAddr) -> std::io::Result<Arc<Conn>> {
        let host = self.server_names.lock().get(&dest).cloned();
        let wire = match (self.kind.secure(), self.tls.as_ref()) {
            (true, Some(ctx)) => {
                let name = match &host {
                    Some(h) => ServerName::try_from(h.clone()).map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidInput, e))?,
                    None => ServerName::IpAddress(dest.ip().into()),
                };
                let c = rustls::ClientConnection::new(ctx.client.clone(), name).map_err(std::io::Error::other)?;
                Wire::Tls(Mutex::new(c.into()))
            }
            _ => Wire::Tcp,
        };
        let sock = TcpStream::connect_timeout(&dest, Duration::from_secs(5))?;
        let ws = self.kind.websocket().then_some(WsRole::Client);
        let conn = Arc::new(Conn::new(sock, wire, ws));
        if let Wire::Tls(tls) = &conn.wire {
            let mut tls = tls.lock();
            flush_tls(&conn.sock, &mut tls)?; // ClientHello
        }
        self.adopt(conn.clone(), dest);

        let deadline = Instant::now() + Duration::from_secs(5);
        let mut upgrade_sent = false;
        loop {
            if conn.failed.load(Ordering::Relaxed) {
                return Err(std::io::Error::new(std::io::ErrorKind::ConnectionAborted, "connection handshake failed"));
            }
            let tls_ready = match &conn.wire {
                Wire::Tls(tls) => !tls.lock().is_handshaking(),
                Wire::Tcp => true,
            };
            if tls_ready && ws.is_some() && !upgrade_sent {
                upgrade_sent = true;
                let key = base64::engine::general_purpose::STANDARD.encode(rand::random::<[u8; 16]>());
                let authority = host.clone().map_or_else(|| dest.to_string(), |h| format!("{h}:{}", dest.port()));
                let request = format!(
                    "GET / HTTP/1.1\r\nHost: {authority}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: sip\r\n\r\n"
                );
                conn.write_raw(request.as_bytes())?;
            }
            if !conn.is_handshaking() {
                return Ok(conn);
            }
            if Instant::now() >= deadline {
                conn.close();
                return Err(std::io::Error::new(std::io::ErrorKind::TimedOut, "connection handshake timed out"));
            }
            std::thread::sleep(Duration::from_millis(2));
        }
    }

    pub fn send(self: &Arc<Self>, dest: SocketAddr, data: &[u8]) -> std::io::Result<()> {
        if self.kind == TransportKind::Udp {
            return self.udp.as_ref().expect("udp transport").send_to(data, dest).map(|_| ());
        }
        let existing = self.connections.lock().get(&dest).filter(|c| !c.failed.load(Ordering::Relaxed)).cloned();
        let conn = match existing {
            Some(c) => c,
            None => self.connect(dest)?,
        };
        conn.send_message(data)
    }

    pub fn shutdown(&self) {
        self.running.store(false, Ordering::SeqCst);
        for (_, c) in self.connections.lock().drain() {
            c.close();
        }
        for t in self.threads.lock().drain(..) {
            let _ = t.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn websocket_frames_roundtrip() {
        for len in [5usize, 200, 70_000] {
            let payload: Vec<u8> = (0..len).map(|i| i as u8).collect();
            for mask in [false, true] {
                let mut frame = Vec::new();
                encode_frame(0x1, &payload, mask, &mut frame);
                let (fin, op, decoded, used) = decode_frame(&frame).unwrap().unwrap();
                assert!(fin);
                assert_eq!((op, used), (1, frame.len()));
                assert_eq!(decoded, payload);
                assert_eq!(decode_frame(&frame[..frame.len() - 1]), Ok(None));
            }
        }
    }

    #[test]
    fn websocket_accept_matches_rfc6455_example() {
        assert_eq!(ws_accept("dGhlIHNhbXBsZSBub25jZQ=="), "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=");
    }
}
