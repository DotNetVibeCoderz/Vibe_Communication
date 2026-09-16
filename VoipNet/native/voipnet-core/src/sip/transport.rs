//! SIP transports: UDP and TCP (RFC 3261 §18), with stream framing by Content-Length.

use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{Shutdown, SocketAddr, TcpListener, TcpStream, UdpSocket};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::Duration;

use parking_lot::Mutex;
use serde::Deserialize;

use super::message::{ParseError, SipMessage};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TransportKind {
    #[default]
    Udp,
    Tcp,
}

impl TransportKind {
    pub fn via_name(self) -> &'static str {
        match self {
            Self::Udp => "UDP",
            Self::Tcp => "TCP",
        }
    }

    pub fn uri_param(self) -> Option<&'static str> {
        match self {
            Self::Udp => None,
            Self::Tcp => Some("tcp"),
        }
    }
}

pub type MessageHandler = Arc<dyn Fn(SipMessage, SocketAddr) + Send + Sync>;

pub struct Transport {
    kind: TransportKind,
    local: SocketAddr,
    udp: Option<UdpSocket>,
    listener: Option<TcpListener>,
    connections: Mutex<HashMap<SocketAddr, TcpStream>>,
    handler: OnceLock<MessageHandler>,
    running: AtomicBool,
    threads: Mutex<Vec<std::thread::JoinHandle<()>>>,
}

impl Transport {
    pub fn bind(kind: TransportKind, addr: SocketAddr) -> std::io::Result<Arc<Self>> {
        let (udp, listener, local) = match kind {
            TransportKind::Udp => {
                let s = UdpSocket::bind(addr)?;
                s.set_read_timeout(Some(Duration::from_millis(200)))?;
                let local = s.local_addr()?;
                (Some(s), None, local)
            }
            TransportKind::Tcp => {
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

    pub fn start(self: &Arc<Self>, handler: MessageHandler) {
        let _ = self.handler.set(handler);
        let me = self.clone();
        let t = std::thread::Builder::new()
            .name("voipnet-sip-transport".into())
            .spawn(move || match me.kind {
                TransportKind::Udp => me.udp_loop(),
                TransportKind::Tcp => me.accept_loop(),
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
        let listener = self.listener.as_ref().expect("tcp transport");
        while self.running.load(Ordering::Relaxed) {
            match listener.accept() {
                Ok((stream, peer)) => self.adopt(stream, peer),
                Err(ref e) if e.kind() == std::io::ErrorKind::WouldBlock => std::thread::sleep(Duration::from_millis(50)),
                Err(_) => std::thread::sleep(Duration::from_millis(50)),
            }
        }
    }

    fn adopt(self: &Arc<Self>, stream: TcpStream, peer: SocketAddr) {
        let _ = stream.set_nonblocking(false);
        let _ = stream.set_nodelay(true);
        let _ = stream.set_read_timeout(Some(Duration::from_millis(500)));
        if let Ok(clone) = stream.try_clone() {
            self.connections.lock().insert(peer, clone);
        }
        let me = self.clone();
        let _ = std::thread::Builder::new().name("voipnet-sip-tcp".into()).spawn(move || me.stream_loop(stream, peer));
    }

    fn stream_loop(&self, mut stream: TcpStream, peer: SocketAddr) {
        let mut buf: Vec<u8> = Vec::with_capacity(8192);
        let mut chunk = [0u8; 8192];
        while self.running.load(Ordering::Relaxed) {
            match stream.read(&mut chunk) {
                Ok(0) => break,
                Ok(n) => {
                    buf.extend_from_slice(&chunk[..n]);
                    loop {
                        let skip = buf.iter().take_while(|b| **b == b'\r' || **b == b'\n').count();
                        if skip > 0 {
                            buf.drain(..skip);
                        }
                        if buf.is_empty() {
                            break;
                        }
                        match SipMessage::parse(&buf) {
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
                Err(ref e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => {}
                Err(_) => break,
            }
        }
        self.connections.lock().remove(&peer);
    }

    pub fn send(self: &Arc<Self>, dest: SocketAddr, data: &[u8]) -> std::io::Result<()> {
        match self.kind {
            TransportKind::Udp => self.udp.as_ref().expect("udp transport").send_to(data, dest).map(|_| ()),
            TransportKind::Tcp => {
                let existing = self.connections.lock().get(&dest).and_then(|s| s.try_clone().ok());
                let mut stream = match existing {
                    Some(s) => s,
                    None => {
                        let s = TcpStream::connect_timeout(&dest, Duration::from_secs(5))?;
                        let writer = s.try_clone()?;
                        self.adopt(s, dest);
                        writer
                    }
                };
                stream.write_all(data)
            }
        }
    }

    pub fn shutdown(&self) {
        self.running.store(false, Ordering::SeqCst);
        for (_, s) in self.connections.lock().drain() {
            let _ = s.shutdown(Shutdown::Both);
        }
        for t in self.threads.lock().drain(..) {
            let _ = t.join();
        }
    }
}
