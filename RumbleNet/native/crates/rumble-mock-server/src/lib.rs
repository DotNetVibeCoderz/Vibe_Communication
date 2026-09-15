//! # rumble-mock-server
//!
//! A small but protocol-faithful Mumble server used to test Rumble.Net end to end without a real
//! Murmur installation. It implements:
//!
//! * TLS control channel with the full login sequence (Version, CryptSetup, CodecVersion,
//!   ChannelState, UserState, ServerSync, ServerConfig) and password rejection.
//! * OCB2-AES128 encrypted UDP voice with ping echo, plus TCP tunnelling fallback.
//! * Channel switching, text messages, channel creation/removal, kicks, plugin data, pings.
//! * An **EchoBot** user in the "Lobby" channel that answers text messages with `echo: <text>` and
//!   plays back any voice sent by users in its channel.
//!
//! It is intentionally simple (no ACLs, no persistence) and not meant for production use.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::Arc;
use std::sync::atomic::{AtomicU32, Ordering};

use bytes::{Bytes, BytesMut};
use parking_lot::{Mutex, RwLock};
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream, UdpSocket};
use tokio::sync::{mpsc, watch};
use tokio_rustls::TlsAcceptor;

use rumble_protocol::control::{ControlMessage as M, FrameDecoder};
use rumble_protocol::crypt::CryptState;
use rumble_protocol::proto;
use rumble_protocol::version::Version;
use rumble_protocol::voice::{self, UdpPacket, VoiceFormat, VoicePacket};

/// Session id of the built-in echo bot.
pub const ECHO_BOT_SESSION: u32 = 1;
/// Channel id of the lobby (where the echo bot lives).
pub const LOBBY_CHANNEL: u32 = 1;

/// Server options.
#[derive(Debug, Clone)]
pub struct MockServerConfig {
    /// Address to bind (TCP and UDP use the same port). Port 0 picks a free port.
    pub bind: SocketAddr,
    /// Optional server password.
    pub password: Option<String>,
    pub welcome_text: String,
    /// Include the echo bot.
    pub echo_bot: bool,
}

impl Default for MockServerConfig {
    fn default() -> Self {
        Self {
            bind: "127.0.0.1:0".parse().unwrap(),
            password: None,
            welcome_text: "Welcome to the Rumble.Net mock server".into(),
            echo_bot: true,
        }
    }
}

#[derive(Clone)]
struct ChannelInfo {
    id: u32,
    parent: Option<u32>,
    name: String,
    description: String,
    temporary: bool,
}

struct UserConn {
    name: String,
    channel: u32,
    self_mute: bool,
    self_deaf: bool,
    comment: String,
    tx: mpsc::UnboundedSender<Bytes>,
    crypt: Arc<Mutex<CryptState>>,
    udp_addr: Option<SocketAddr>,
    kill: watch::Sender<bool>,
}

struct State {
    channels: HashMap<u32, ChannelInfo>,
    users: HashMap<u32, UserConn>,
    next_session: u32,
    next_channel: u32,
}

struct Inner {
    config: MockServerConfig,
    state: RwLock<State>,
    udp: UdpSocket,
    voice_packets: AtomicU32,
}

/// A running mock server. Dropping it stops the server.
pub struct MockServer {
    inner: Arc<Inner>,
    addr: SocketAddr,
    shutdown: watch::Sender<bool>,
}

fn encode(msg: &M) -> Bytes {
    msg.to_bytes()
}

impl MockServer {
    /// Starts the server on the current tokio runtime.
    pub async fn start(config: MockServerConfig) -> std::io::Result<Self> {
        let _ = rustls::crypto::ring::default_provider().install_default();

        // TCP and UDP must share a port; retry until a free pair is found.
        let (listener, udp) = {
            let mut attempt = 0;
            loop {
                let listener = TcpListener::bind(config.bind).await?;
                let addr = listener.local_addr()?;
                match UdpSocket::bind(addr).await {
                    Ok(udp) => break (listener, udp),
                    Err(e) if config.bind.port() == 0 && attempt < 20 => {
                        attempt += 1;
                        tracing::debug!("udp bind failed on {addr}: {e}, retrying");
                    }
                    Err(e) => return Err(e),
                }
            }
        };
        let addr = listener.local_addr()?;

        let key = rcgen::KeyPair::generate().map_err(std::io::Error::other)?;
        let params = rcgen::CertificateParams::new(vec!["localhost".to_string()]).map_err(std::io::Error::other)?;
        let cert = params.self_signed(&key).map_err(std::io::Error::other)?;
        let tls = rustls::ServerConfig::builder()
            .with_no_client_auth()
            .with_single_cert(
                vec![CertificateDer::from(cert.der().to_vec())],
                PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(key.serialize_der())),
            )
            .map_err(std::io::Error::other)?;
        let acceptor = TlsAcceptor::from(Arc::new(tls));

        let mut channels = HashMap::new();
        for (id, parent, name) in [(0, None, "Root"), (LOBBY_CHANNEL, Some(0), "Lobby"), (2, Some(0), "AFK")] {
            channels.insert(
                id,
                ChannelInfo { id, parent, name: name.into(), description: format!("{name} channel"), temporary: false },
            );
        }

        let inner = Arc::new(Inner {
            config,
            state: RwLock::new(State { channels, users: HashMap::new(), next_session: 2, next_channel: 3 }),
            udp,
            voice_packets: AtomicU32::new(0),
        });
        let (shutdown, shutdown_rx) = watch::channel(false);

        tokio::spawn(accept_loop(inner.clone(), listener, acceptor, shutdown_rx.clone()));
        tokio::spawn(udp_loop(inner.clone(), shutdown_rx));

        Ok(Self { inner, addr, shutdown })
    }

    pub fn addr(&self) -> SocketAddr {
        self.addr
    }

    pub fn port(&self) -> u16 {
        self.addr.port()
    }

    /// Number of connected (real) users.
    pub fn user_count(&self) -> usize {
        self.inner.state.read().users.len()
    }

    /// Voice packets relayed so far.
    pub fn voice_packets(&self) -> u32 {
        self.inner.voice_packets.load(Ordering::Relaxed)
    }

    /// Abruptly closes every client connection (to test reconnects).
    pub fn drop_all_connections(&self) {
        let st = self.inner.state.read();
        for u in st.users.values() {
            let _ = u.kill.send(true);
        }
    }
}

impl Drop for MockServer {
    fn drop(&mut self) {
        let _ = self.shutdown.send(true);
        self.drop_all_connections();
    }
}

async fn accept_loop(inner: Arc<Inner>, listener: TcpListener, acceptor: TlsAcceptor, mut shutdown: watch::Receiver<bool>) {
    loop {
        tokio::select! {
            _ = shutdown.changed() => return,
            r = listener.accept() => {
                let Ok((tcp, peer)) = r else { continue };
                let inner = inner.clone();
                let acceptor = acceptor.clone();
                tokio::spawn(async move {
                    if let Err(e) = handle_client(inner, acceptor, tcp, peer).await {
                        tracing::debug!("client {peer} ended: {e}");
                    }
                });
            }
        }
    }
}

fn channel_state(c: &ChannelInfo) -> M {
    M::ChannelState(proto::ChannelState {
        channel_id: Some(c.id),
        parent: c.parent,
        name: Some(c.name.clone()),
        description: Some(c.description.clone()),
        temporary: Some(c.temporary),
        position: Some(0),
        ..Default::default()
    })
}

fn user_state(session: u32, u: &UserConn) -> M {
    M::UserState(proto::UserState {
        session: Some(session),
        name: Some(u.name.clone()),
        channel_id: Some(u.channel),
        self_mute: Some(u.self_mute),
        self_deaf: Some(u.self_deaf),
        comment: Some(u.comment.clone()),
        ..Default::default()
    })
}

fn bot_state() -> M {
    M::UserState(proto::UserState {
        session: Some(ECHO_BOT_SESSION),
        name: Some("EchoBot".into()),
        channel_id: Some(LOBBY_CHANNEL),
        user_id: Some(1),
        comment: Some("I repeat everything you say.".into()),
        ..Default::default()
    })
}

impl Inner {
    fn broadcast(&self, msg: &M, except: Option<u32>) {
        let bytes = encode(msg);
        for (s, u) in self.state.read().users.iter() {
            if Some(*s) != except {
                let _ = u.tx.send(bytes.clone());
            }
        }
    }

    fn send_to(&self, session: u32, msg: &M) {
        if let Some(u) = self.state.read().users.get(&session) {
            let _ = u.tx.send(encode(msg));
        }
    }

    /// Sends a voice packet to a user over UDP if available, otherwise tunnels it over TCP.
    fn deliver_voice(&self, target: u32, packet: &VoicePacket) {
        let st = self.state.read();
        let Some(u) = st.users.get(&target) else { return };
        let mut plain = Vec::with_capacity(packet.payload.len() + 32);
        voice::encode_voice(packet, VoiceFormat::Protobuf, true, &mut plain);
        match u.udp_addr {
            Some(addr) => {
                let mut out = vec![0u8; plain.len() + rumble_protocol::crypt::OVERHEAD];
                if let Some(n) = u.crypt.lock().encrypt(&plain, &mut out) {
                    let _ = self.udp.try_send_to(&out[..n], addr);
                }
            }
            None => {
                let _ = u.tx.send(encode(&M::UdpTunnel(Bytes::from(plain))));
            }
        }
    }

    fn relay_voice(&self, sender: u32, mut packet: VoicePacket) {
        self.voice_packets.fetch_add(1, Ordering::Relaxed);
        let (sender_channel, recipients): (u32, Vec<u32>) = {
            let st = self.state.read();
            let Some(u) = st.users.get(&sender) else { return };
            let ch = u.channel;
            (ch, st.users.iter().filter(|(s, o)| **s != sender && o.channel == ch && !o.self_deaf).map(|(s, _)| *s).collect())
        };

        if packet.target_or_context == voice::TARGET_SERVER_LOOPBACK {
            packet.session = Some(sender);
            packet.target_or_context = 0;
            self.deliver_voice(sender, &packet);
            return;
        }

        packet.target_or_context = 0;
        packet.session = Some(sender);
        for r in recipients {
            self.deliver_voice(r, &packet);
        }
        if self.config.echo_bot && sender_channel == LOBBY_CHANNEL {
            packet.session = Some(ECHO_BOT_SESSION);
            self.deliver_voice(sender, &packet);
        }
    }
}

async fn handle_client(inner: Arc<Inner>, acceptor: TlsAcceptor, tcp: TcpStream, peer: SocketAddr) -> std::io::Result<()> {
    tcp.set_nodelay(true)?;
    let tls = acceptor.accept(tcp).await?;
    let (mut reader, mut writer) = tokio::io::split(tls);
    let mut decoder = FrameDecoder::new();

    // Wait for Version + Authenticate.
    let auth = loop {
        if reader.read_buf(decoder.buffer_mut()).await? == 0 {
            return Ok(());
        }
        let mut found = None;
        while let Some(msg) = decoder.next_message().map_err(std::io::Error::other)? {
            if let M::Authenticate(a) = msg {
                found = Some(a);
                break;
            }
        }
        if let Some(a) = found {
            break a;
        }
    };

    let version = M::Version(proto::Version {
        version_v1: Some(Version::new(1, 5, 0).to_v1()),
        version_v2: Some(Version::new(1, 5, 0).to_v2()),
        release: Some("Rumble mock server".into()),
        os: Some(std::env::consts::OS.into()),
        os_version: Some("mock".into()),
    });
    writer.write_all(&encode(&version)).await?;

    if let Some(expected) = &inner.config.password {
        if auth.password.as_deref() != Some(expected.as_str()) {
            let reject = M::Reject(proto::Reject {
                r#type: Some(proto::reject::RejectType::WrongServerPw as i32),
                reason: Some("Wrong server password".into()),
            });
            writer.write_all(&encode(&reject)).await?;
            writer.shutdown().await?;
            return Ok(());
        }
    }
    let name = auth.username.clone().unwrap_or_else(|| format!("user{}", peer.port()));
    if inner.state.read().users.values().any(|u| u.name.eq_ignore_ascii_case(&name)) || (inner.config.echo_bot && name == "EchoBot") {
        let reject = M::Reject(proto::Reject {
            r#type: Some(proto::reject::RejectType::UsernameInUse as i32),
            reason: Some("Username already in use".into()),
        });
        writer.write_all(&encode(&reject)).await?;
        writer.shutdown().await?;
        return Ok(());
    }

    let (tx, mut rx) = mpsc::unbounded_channel::<Bytes>();
    let (kill_tx, mut kill_rx) = watch::channel(false);
    let mut crypt = CryptState::new();
    crypt.generate_key();
    let crypt_setup = M::CryptSetup(proto::CryptSetup {
        key: Some(Bytes::copy_from_slice(crypt.raw_key())),
        client_nonce: Some(Bytes::copy_from_slice(crypt.decrypt_iv())),
        server_nonce: Some(Bytes::copy_from_slice(crypt.encrypt_iv())),
    });
    let crypt = Arc::new(Mutex::new(crypt));

    // Register the user.
    let session = {
        let mut st = inner.state.write();
        let session = st.next_session;
        st.next_session += 1;
        st.users.insert(
            session,
            UserConn {
                name: name.clone(),
                channel: 0,
                self_mute: false,
                self_deaf: false,
                comment: String::new(),
                tx: tx.clone(),
                crypt: crypt.clone(),
                udp_addr: None,
                kill: kill_tx,
            },
        );
        session
    };

    // Initial synchronization.
    let mut sync = BytesMut::new();
    crypt_setup.encode(&mut sync);
    M::CodecVersion(proto::CodecVersion { alpha: -2147483637, beta: 0, prefer_alpha: true, opus: Some(true) }).encode(&mut sync);
    {
        let st = inner.state.read();
        let mut channels: Vec<&ChannelInfo> = st.channels.values().collect();
        channels.sort_by_key(|c| c.id);
        for c in channels {
            channel_state(c).encode(&mut sync);
        }
        if inner.config.echo_bot {
            bot_state().encode(&mut sync);
        }
        for (s, u) in st.users.iter() {
            user_state(*s, u).encode(&mut sync);
        }
    }
    M::ServerSync(proto::ServerSync {
        session: Some(session),
        max_bandwidth: Some(72_000),
        welcome_text: Some(inner.config.welcome_text.clone()),
        permissions: Some(0xFFFF_FFFF),
    })
    .encode(&mut sync);
    M::ServerConfig(proto::ServerConfig {
        max_bandwidth: Some(72_000),
        allow_html: Some(true),
        message_length: Some(5000),
        image_message_length: Some(131_072),
        max_users: Some(100),
        recording_allowed: Some(true),
        welcome_text: None,
    })
    .encode(&mut sync);
    writer.write_all(&sync).await?;
    {
        let st = inner.state.read();
        if let Some(u) = st.users.get(&session) {
            let msg = user_state(session, u);
            drop(st);
            inner.broadcast(&msg, Some(session));
        }
    }

    let writer_task = tokio::spawn(async move {
        while let Some(b) = rx.recv().await {
            if writer.write_all(&b).await.is_err() {
                break;
            }
            while let Ok(more) = rx.try_recv() {
                if writer.write_all(&more).await.is_err() {
                    return;
                }
            }
            if writer.flush().await.is_err() {
                break;
            }
        }
        let _ = writer.shutdown().await;
    });

    let result: std::io::Result<()> = async {
        loop {
            tokio::select! {
                _ = kill_rx.changed() => return Ok(()),
                r = reader.read_buf(decoder.buffer_mut()) => {
                    if r? == 0 {
                        return Ok(());
                    }
                    while let Some(msg) = decoder.next_message().map_err(std::io::Error::other)? {
                        if !handle_message(&inner, session, msg) {
                            return Ok(());
                        }
                    }
                }
            }
        }
    }
    .await;

    // Unregister.
    let removed = inner.state.write().users.remove(&session);
    if removed.is_some() {
        inner.broadcast(&M::UserRemove(proto::UserRemove { session, actor: None, reason: None, ban: None }), None);
    }
    drop(tx);
    writer_task.abort();
    result
}

/// Handles one message; returns false to close the connection.
fn handle_message(inner: &Arc<Inner>, session: u32, msg: M) -> bool {
    match msg {
        M::Ping(p) => {
            inner.send_to(session, &M::Ping(proto::Ping { timestamp: p.timestamp, ..Default::default() }));
        }
        M::UserState(us) => {
            let target = us.session.unwrap_or(session);
            let mut changed = proto::UserState { session: Some(target), actor: Some(session), ..Default::default() };
            {
                let mut st = inner.state.write();
                let channel_exists = us.channel_id.is_some_and(|c| st.channels.contains_key(&c));
                let Some(u) = st.users.get_mut(&target) else { return true };
                if let Some(c) = us.channel_id.filter(|_| channel_exists) {
                    u.channel = c;
                    changed.channel_id = Some(c);
                }
                if let Some(m) = us.self_mute {
                    u.self_mute = m;
                    changed.self_mute = Some(m);
                }
                if let Some(d) = us.self_deaf {
                    u.self_deaf = d;
                    changed.self_deaf = Some(d);
                }
                if let Some(c) = &us.comment {
                    u.comment.clone_from(c);
                    changed.comment = Some(c.clone());
                }
                changed.mute = us.mute;
                changed.deaf = us.deaf;
                changed.recording = us.recording;
                changed.listening_channel_add = us.listening_channel_add;
                changed.listening_channel_remove = us.listening_channel_remove;
            }
            inner.broadcast(&M::UserState(changed), None);
        }
        M::TextMessage(tm) => {
            let out = M::TextMessage(proto::TextMessage { actor: Some(session), ..tm.clone() });
            let recipients: Vec<u32> = {
                let st = inner.state.read();
                st.users
                    .iter()
                    .filter(|(s, u)| **s != session && (tm.channel_id.contains(&u.channel) || tm.session.contains(s)))
                    .map(|(s, _)| *s)
                    .collect()
            };
            for r in recipients {
                inner.send_to(r, &out);
            }
            let sender_in_lobby = inner.state.read().users.get(&session).is_some_and(|u| u.channel == LOBBY_CHANNEL);
            let to_bot = tm.session.contains(&ECHO_BOT_SESSION) || (sender_in_lobby && tm.channel_id.contains(&LOBBY_CHANNEL));
            if inner.config.echo_bot && to_bot {
                inner.send_to(
                    session,
                    &M::TextMessage(proto::TextMessage {
                        actor: Some(ECHO_BOT_SESSION),
                        session: vec![session],
                        message: format!("echo: {}", tm.message),
                        ..Default::default()
                    }),
                );
            }
        }
        M::ChannelState(cs) => {
            if cs.channel_id.is_none() {
                let (Some(parent), Some(name)) = (cs.parent, cs.name.clone()) else { return true };
                let info = {
                    let mut st = inner.state.write();
                    if !st.channels.contains_key(&parent) {
                        return true;
                    }
                    let id = st.next_channel;
                    st.next_channel += 1;
                    let info = ChannelInfo {
                        id,
                        parent: Some(parent),
                        name,
                        description: cs.description.clone().unwrap_or_default(),
                        temporary: cs.temporary.unwrap_or(false),
                    };
                    st.channels.insert(id, info.clone());
                    info
                };
                inner.broadcast(&channel_state(&info), None);
            } else if let Some(id) = cs.channel_id {
                let info = {
                    let mut st = inner.state.write();
                    let Some(c) = st.channels.get_mut(&id) else { return true };
                    if let Some(n) = &cs.name {
                        c.name.clone_from(n);
                    }
                    if let Some(d) = &cs.description {
                        c.description.clone_from(d);
                    }
                    c.clone()
                };
                let mut msg = match channel_state(&info) {
                    M::ChannelState(s) => s,
                    _ => unreachable!(),
                };
                msg.links_add = cs.links_add;
                msg.links_remove = cs.links_remove;
                inner.broadcast(&M::ChannelState(msg), None);
            }
        }
        M::ChannelRemove(cr) => {
            if cr.channel_id == 0 {
                return true;
            }
            let removed = {
                let mut st = inner.state.write();
                let removed = st.channels.remove(&cr.channel_id).is_some();
                for u in st.users.values_mut() {
                    if u.channel == cr.channel_id {
                        u.channel = 0;
                    }
                }
                removed
            };
            if removed {
                inner.broadcast(&M::ChannelRemove(cr), None);
            }
        }
        M::UserRemove(ur) => {
            let kill = inner.state.read().users.get(&ur.session).map(|u| u.kill.clone());
            if let Some(kill) = kill {
                inner.broadcast(
                    &M::UserRemove(proto::UserRemove { session: ur.session, actor: Some(session), reason: ur.reason, ban: ur.ban }),
                    None,
                );
                inner.state.write().users.remove(&ur.session);
                let _ = kill.send(true);
            }
        }
        M::CryptSetup(c) => {
            let st = inner.state.read();
            if let Some(u) = st.users.get(&session) {
                match c.client_nonce {
                    Some(nonce) => {
                        u.crypt.lock().set_decrypt_iv(&nonce);
                    }
                    None => {
                        let iv = Bytes::copy_from_slice(u.crypt.lock().encrypt_iv());
                        let _ = u.tx.send(encode(&M::CryptSetup(proto::CryptSetup { server_nonce: Some(iv), ..Default::default() })));
                    }
                }
            }
        }
        M::PermissionQuery(pq) => inner.send_to(
            session,
            &M::PermissionQuery(proto::PermissionQuery { channel_id: pq.channel_id, permissions: Some(0xFFFF_FFFF), flush: None }),
        ),
        M::UserStats(us) => {
            let target = us.session.unwrap_or(session);
            inner.send_to(
                session,
                &M::UserStats(proto::UserStats {
                    session: Some(target),
                    stats_only: Some(false),
                    onlinesecs: Some(42),
                    idlesecs: Some(0),
                    opus: Some(true),
                    strong_certificate: Some(false),
                    ..Default::default()
                }),
            );
        }
        M::UdpTunnel(data) => {
            if let Ok(UdpPacket::Voice(v)) = voice::decode_udp(&data, VoiceFormat::Protobuf, false)
                .or_else(|_| voice::decode_udp(&data, VoiceFormat::Legacy, false))
            {
                inner.relay_voice(session, v);
            }
        }
        M::PluginDataTransmission(p) => {
            let out = M::PluginDataTransmission(proto::PluginDataTransmission {
                sender_session: Some(session),
                receiver_sessions: p.receiver_sessions.clone(),
                data: p.data,
                data_id: p.data_id,
            });
            for r in p.receiver_sessions {
                inner.send_to(r, &out);
            }
        }
        M::UserList(_) => inner.send_to(
            session,
            &M::UserList(proto::UserList {
                users: vec![proto::user_list::User {
                    user_id: 1,
                    name: Some("EchoBot".into()),
                    last_seen: None,
                    last_channel: Some(LOBBY_CHANNEL),
                }],
            }),
        ),
        M::BanList(bl) if bl.query() => inner.send_to(session, &M::BanList(proto::BanList { bans: vec![], query: None })),
        _ => {}
    }
    true
}

async fn udp_loop(inner: Arc<Inner>, mut shutdown: watch::Receiver<bool>) {
    let mut buf = vec![0u8; 4096];
    let mut plain = vec![0u8; 4096];
    loop {
        let (n, from) = tokio::select! {
            _ = shutdown.changed() => return,
            r = inner.udp.recv_from(&mut buf) => match r {
                Ok(v) => v,
                Err(_) => continue,
            },
        };
        let datagram = &buf[..n];

        // Legacy unconnected info ping (12 bytes, first 4 zero).
        if n == 12 && datagram[..4] == [0, 0, 0, 0] {
            let mut resp = [0u8; 24];
            resp[0..4].copy_from_slice(&Version::new(1, 5, 0).to_v1().to_be_bytes());
            resp[4..12].copy_from_slice(&datagram[4..12]);
            resp[12..16].copy_from_slice(&(inner.state.read().users.len() as u32).to_be_bytes());
            resp[16..20].copy_from_slice(&100u32.to_be_bytes());
            resp[20..24].copy_from_slice(&72_000u32.to_be_bytes());
            let _ = inner.udp.send_to(&resp, from).await;
            continue;
        }

        // Find the sender: known address first, then trial decryption.
        let found = {
            let st = inner.state.read();
            let mut result = None;
            let known = st.users.iter().find(|(_, u)| u.udp_addr == Some(from));
            let candidates: Vec<(u32, Arc<Mutex<CryptState>>)> = match known {
                Some((s, u)) => vec![(*s, u.crypt.clone())],
                None => st.users.iter().filter(|(_, u)| u.udp_addr.is_none()).map(|(s, u)| (*s, u.crypt.clone())).collect(),
            };
            drop(st);
            for (s, crypt) in candidates {
                if let Some(len) = crypt.lock().decrypt(datagram, &mut plain) {
                    result = Some((s, len));
                    break;
                }
            }
            result
        };
        let Some((session, len)) = found else { continue };
        if let Some(u) = inner.state.write().users.get_mut(&session) {
            u.udp_addr = Some(from);
        }

        match voice::decode_udp(&plain[..len], VoiceFormat::Protobuf, false)
            .or_else(|_| voice::decode_udp(&plain[..len], VoiceFormat::Legacy, false))
        {
            Ok(UdpPacket::Ping(_)) => {
                // Echo the ping back verbatim (encrypted).
                let crypt = inner.state.read().users.get(&session).map(|u| u.crypt.clone());
                if let Some(crypt) = crypt {
                    let mut out = vec![0u8; len + rumble_protocol::crypt::OVERHEAD];
                    let sent = crypt.lock().encrypt(&plain[..len], &mut out);
                    if let Some(m) = sent {
                        let _ = inner.udp.send_to(&out[..m], from).await;
                    }
                }
            }
            Ok(UdpPacket::Voice(v)) => inner.relay_voice(session, v),
            Err(_) => {}
        }
    }
}
