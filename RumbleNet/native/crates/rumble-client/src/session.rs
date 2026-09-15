//! Connection supervisor and session loop.

use std::collections::HashMap;
use std::net::{IpAddr, Ipv6Addr, SocketAddr};
use std::sync::Arc;
use std::time::{Duration, Instant};

use bytes::{Bytes, BytesMut};
use rustls_pki_types::ServerName;
use tokio::io::{AsyncReadExt, AsyncWriteExt, WriteHalf};
use tokio::net::{TcpStream, UdpSocket};
use tokio::sync::mpsc;
use tokio_rustls::TlsConnector;
use tokio_rustls::client::TlsStream;

use rumble_audio::capture::EncodedVoice;
use rumble_audio::codec::StreamCodec;
use rumble_audio::mixer::IncomingVoice;
use rumble_protocol::control::{ControlMessage as M, FrameDecoder};
use rumble_protocol::crypt::CryptState;
use rumble_protocol::proto;
use rumble_protocol::version::Version;
use rumble_protocol::voice::{self, Codec, PingPacket, UdpPacket, VoiceFormat, VoicePacket};

use crate::command::Command;
use crate::config::TlsVerification;
use crate::event::{AclEntry, AclGroup, BanEntry, ConnectionState, Event, PacketStats, RegisteredUser};
use crate::{ClientError, Result, Shared, tls};

/// How a session ended.
pub(crate) enum SessionEnd {
    /// The application asked to disconnect.
    UserRequested,
    /// Unrecoverable (rejected, kicked, certificate mismatch): do not reconnect.
    Fatal(String),
    /// Network / protocol failure: reconnect if configured.
    Failed(String),
}

/// Runs connect → session → reconnect until told to stop.
pub(crate) async fn supervise(
    shared: Arc<Shared>,
    mut commands: mpsc::UnboundedReceiver<Command>,
    mut voice_rx: mpsc::Receiver<EncodedVoice>,
) {
    let cfg = shared.config.clone();
    let mut attempt: u32 = 0;
    let mut reconnecting = false;

    loop {
        attempt += 1;
        shared.set_connection_state(if reconnecting { ConnectionState::Reconnecting } else { ConnectionState::Connecting });
        shared.emit(Event::Connecting { host: cfg.host.clone(), port: cfg.port, attempt });

        let mut synced = false;
        let end = run_session(&shared, &mut commands, &mut voice_rx, &mut synced)
            .await
            .unwrap_or_else(|e| SessionEnd::Failed(e.to_string()));

        shared.state.write().reset();
        shared.audio.clear_speakers();
        if synced {
            attempt = 0;
        }

        let (reason, retry) = match end {
            SessionEnd::UserRequested => ("disconnected".to_string(), false),
            SessionEnd::Fatal(r) => (r, false),
            SessionEnd::Failed(r) => {
                let allowed = cfg.max_reconnect_attempts == 0 || attempt < cfg.max_reconnect_attempts;
                (r, cfg.auto_reconnect && allowed)
            }
        };
        tracing::info!(%reason, retry, "session ended");
        shared.set_connection_state(ConnectionState::Disconnected);
        shared.emit(Event::Disconnected { reason, will_reconnect: retry });
        if !retry {
            return;
        }

        reconnecting = true;
        let sleep = tokio::time::sleep(backoff(cfg.reconnect_min_delay_ms, cfg.reconnect_max_delay_ms, attempt));
        tokio::pin!(sleep);
        loop {
            tokio::select! {
                _ = &mut sleep => break,
                cmd = commands.recv() => match cmd {
                    None | Some(Command::Disconnect) => return,
                    Some(_) => {}
                },
                Some(_) = voice_rx.recv() => {}
            }
        }
    }
}

/// Exponential backoff with ±20 % jitter.
fn backoff(min_ms: u64, max_ms: u64, attempt: u32) -> Duration {
    let exp = min_ms.saturating_mul(1u64 << attempt.saturating_sub(1).min(16));
    let base = exp.min(max_ms.max(min_ms)) as f64;
    let jitter = 0.8 + rand::random::<f64>() * 0.4;
    Duration::from_millis((base * jitter) as u64)
}

async fn write_loop(mut writer: WriteHalf<TlsStream<TcpStream>>, mut rx: mpsc::UnboundedReceiver<Bytes>) {
    while let Some(first) = rx.recv().await {
        if writer.write_all(&first).await.is_err() {
            return;
        }
        // Coalesce whatever else is queued into the same TLS flush.
        while let Ok(next) = rx.try_recv() {
            if writer.write_all(&next).await.is_err() {
                return;
            }
        }
        if writer.flush().await.is_err() {
            return;
        }
    }
    let _ = writer.shutdown().await;
}

async fn run_session(
    shared: &Arc<Shared>,
    commands: &mut mpsc::UnboundedReceiver<Command>,
    voice_rx: &mut mpsc::Receiver<EncodedVoice>,
    synced: &mut bool,
) -> Result<SessionEnd> {
    let cfg = &shared.config;
    let connect_timeout = Duration::from_millis(cfg.connect_timeout_ms);

    // Resolve and connect (try every resolved address).
    let addrs: Vec<SocketAddr> =
        tokio::time::timeout(connect_timeout, tokio::net::lookup_host((cfg.host.as_str(), cfg.port)))
            .await?
            .map_err(|_| ClientError::Resolve(cfg.host.clone()))?
            .collect();
    let mut last_err = ClientError::Resolve(cfg.host.clone());
    let mut connected = None;
    for addr in addrs {
        match tokio::time::timeout(connect_timeout, TcpStream::connect(addr)).await {
            Ok(Ok(s)) => {
                connected = Some((s, addr));
                break;
            }
            Ok(Err(e)) => last_err = e.into(),
            Err(_) => last_err = ClientError::Timeout,
        }
    }
    let Some((tcp, addr)) = connected else { return Err(last_err) };
    tcp.set_nodelay(true)?;

    // TLS handshake.
    let (tls_config, verifier) = tls::client_config(cfg)?;
    let server_name = ServerName::try_from(cfg.host.clone()).map_err(|e| ClientError::Tls(e.to_string()))?;
    let connector = TlsConnector::from(Arc::new(tls_config));
    let stream = match tokio::time::timeout(connect_timeout, connector.connect(server_name, tcp)).await? {
        Ok(s) => s,
        Err(e) if cfg.tls_verification != TlsVerification::AcceptAll && verifier.peer.lock().is_some() => {
            return Ok(SessionEnd::Fatal(format!("server certificate rejected: {e}")));
        }
        Err(e) => return Err(ClientError::Tls(e.to_string())),
    };

    let peer = verifier.peer.lock().clone();
    if let Some(der) = peer {
        let sha1 = tls::sha1_fingerprint(&der);
        shared.state.write().server.certificate_fingerprint = Some(sha1.clone());
        shared.emit(Event::ServerCertificate { sha1_fingerprint: sha1, sha256_fingerprint: tls::sha256_fingerprint(&der) });
    }
    shared.set_connection_state(ConnectionState::Synchronizing);

    let (mut reader, writer) = tokio::io::split(stream);
    let (out_tx, out_rx) = mpsc::unbounded_channel::<Bytes>();
    let writer_task = tokio::spawn(write_loop(writer, out_rx));

    let udp = Arc::new(UdpSocket::bind(if addr.is_ipv4() { "0.0.0.0:0" } else { "[::]:0" }).await?);
    udp.connect(addr).await?;

    let mut session = Session::new(shared.clone(), out_tx, udp.clone());
    session.send_handshake();

    let mut decoder = FrameDecoder::new();
    let mut ping = tokio::time::interval(Duration::from_millis(cfg.ping_interval_ms));
    ping.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut housekeeping = tokio::time::interval(Duration::from_millis(100));
    housekeeping.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut udp_buf = vec![0u8; 4096];

    let result = loop {
        tokio::select! {
            biased;
            cmd = commands.recv() => match cmd {
                None | Some(Command::Disconnect) => break Ok(SessionEnd::UserRequested),
                Some(c) => session.handle_command(c),
            },
            r = reader.read_buf(decoder.buffer_mut()) => {
                match r {
                    Ok(0) => break Ok(SessionEnd::Failed("server closed the connection".into())),
                    Err(e) => break Err(e.into()),
                    Ok(_) => {}
                }
                let mut end = None;
                loop {
                    match decoder.next_message() {
                        Ok(Some(msg)) => {
                            if let Some(e) = session.handle_control(msg, synced) {
                                end = Some(e);
                                break;
                            }
                        }
                        Ok(None) => break,
                        Err(e) => {
                            end = Some(SessionEnd::Failed(format!("protocol error: {e}")));
                            break;
                        }
                    }
                }
                if let Some(e) = end {
                    break Ok(e);
                }
            }
            r = udp.recv(&mut udp_buf) => {
                if let Ok(n) = r {
                    session.handle_udp(&udp_buf[..n]);
                }
            }
            Some(v) = voice_rx.recv() => session.send_voice(v),
            _ = ping.tick() => {
                if let Some(e) = session.on_ping_tick() {
                    break Ok(e);
                }
            }
            _ = housekeeping.tick() => session.housekeeping(),
        }
    };

    session.finish();
    drop(session);
    let _ = tokio::time::timeout(Duration::from_secs(2), writer_task).await;
    result
}

/// Incremental mean / variance (Welford).
#[derive(Default)]
struct RollingStat {
    count: u32,
    mean: f64,
    s: f64,
}

impl RollingStat {
    fn push(&mut self, x: f64) {
        // Exponential window after 16 samples keeps the estimate responsive.
        self.count = (self.count + 1).min(16);
        let n = self.count as f64;
        let old = self.mean;
        self.mean += (x - old) / n;
        self.s += ((x - old) * (x - self.mean) - self.s) / n;
    }
    fn mean(&self) -> f32 {
        self.mean as f32
    }
    fn var(&self) -> f32 {
        self.s.max(0.0) as f32
    }
}

struct Session {
    shared: Arc<Shared>,
    out: mpsc::UnboundedSender<Bytes>,
    udp: Arc<UdpSocket>,
    crypt: CryptState,
    format: VoiceFormat,
    epoch: Instant,
    last_tcp_pong: Instant,
    udp_unanswered: u32,
    udp_active: bool,
    tcp_ping: RollingStat,
    udp_ping: RollingStat,
    tcp_packets: u32,
    udp_packets: u32,
    talking: HashMap<u32, Instant>,
    self_talking: bool,
    decrypt_failures: u32,
    last_resync_request: Option<Instant>,
    voice_buf: Vec<u8>,
    crypt_buf: Vec<u8>,
    plain_buf: Vec<u8>,
    encode_buf: BytesMut,
}

fn packet_stats(s: rumble_protocol::crypt::CryptStats) -> PacketStats {
    PacketStats { good: s.good, late: s.late, lost: s.lost, resync: s.resync }
}

fn proto_stats(s: Option<proto::user_stats::Stats>) -> PacketStats {
    let s = s.unwrap_or_default();
    PacketStats { good: s.good(), late: s.late(), lost: s.lost(), resync: s.resync() }
}

fn format_ip(bytes: &[u8]) -> String {
    match <[u8; 16]>::try_from(bytes) {
        Ok(arr) => {
            let v6 = Ipv6Addr::from(arr);
            v6.to_ipv4_mapped().map_or_else(|| v6.to_string(), |v4| v4.to_string())
        }
        Err(_) => hex::encode(bytes),
    }
}

fn parse_ip(text: &str) -> Bytes {
    match text.parse::<IpAddr>() {
        Ok(IpAddr::V4(v4)) => Bytes::copy_from_slice(&v4.to_ipv6_mapped().octets()),
        Ok(IpAddr::V6(v6)) => Bytes::copy_from_slice(&v6.octets()),
        Err(_) => Bytes::new(),
    }
}

impl Session {
    fn new(shared: Arc<Shared>, out: mpsc::UnboundedSender<Bytes>, udp: Arc<UdpSocket>) -> Self {
        Self {
            shared,
            out,
            udp,
            crypt: CryptState::new(),
            format: VoiceFormat::Legacy,
            epoch: Instant::now(),
            last_tcp_pong: Instant::now(),
            udp_unanswered: 0,
            udp_active: false,
            tcp_ping: RollingStat::default(),
            udp_ping: RollingStat::default(),
            tcp_packets: 0,
            udp_packets: 0,
            talking: HashMap::new(),
            self_talking: false,
            decrypt_failures: 0,
            last_resync_request: None,
            voice_buf: Vec::with_capacity(1500),
            crypt_buf: Vec::with_capacity(1500),
            plain_buf: vec![0; 4096],
            encode_buf: BytesMut::with_capacity(4096),
        }
    }

    fn now_us(&self) -> u64 {
        self.epoch.elapsed().as_micros() as u64
    }

    fn my_session(&self) -> Option<u32> {
        self.shared.state.read().session
    }

    fn send(&mut self, msg: M) {
        msg.encode(&mut self.encode_buf);
        let frame = self.encode_buf.split().freeze();
        let _ = self.out.send(frame);
    }

    fn send_handshake(&mut self) {
        let shared = self.shared.clone();
        let cfg = &shared.config;
        self.send(M::Version(proto::Version {
            version_v1: Some(Version::RUMBLE.to_v1()),
            version_v2: Some(Version::RUMBLE.to_v2()),
            release: Some(cfg.client_release.clone()),
            os: Some(std::env::consts::OS.to_string()),
            os_version: Some(std::env::consts::ARCH.to_string()),
        }));
        self.send(M::Authenticate(proto::Authenticate {
            username: Some(cfg.username.clone()),
            password: cfg.password.clone(),
            tokens: cfg.tokens.clone(),
            celt_versions: Vec::new(),
            opus: Some(true),
            client_type: Some(if cfg.is_bot { 1 } else { 0 }),
        }));
    }

    /// Sends a TLS close as soon as the writer drains.
    fn finish(&mut self) {
        if self.self_talking {
            if let Some(me) = self.my_session() {
                self.shared.emit(Event::UserTalking { session: me, talking: false });
            }
        }
    }

    fn emit(&self, event: Event) {
        self.shared.emit(event);
    }

    // ---------------------------------------------------------------- control channel

    fn handle_control(&mut self, msg: M, synced: &mut bool) -> Option<SessionEnd> {
        match msg {
            M::Version(v) => {
                let version = Version::from_message(v.version_v1, v.version_v2);
                self.format =
                    if version.uses_protobuf_udp() && Version::RUMBLE.uses_protobuf_udp() { VoiceFormat::Protobuf } else { VoiceFormat::Legacy };
                let mut st = self.shared.state.write();
                st.server_version = version;
                st.server.version = version.to_string();
                st.server.release = v.release.unwrap_or_default();
                st.server.os = v.os.unwrap_or_default();
                st.server.os_version = v.os_version.unwrap_or_default();
            }
            M::Reject(r) => {
                let reject_type = format!("{:?}", r.r#type());
                let reason = r.reason.clone().unwrap_or_else(|| reject_type.clone());
                self.emit(Event::Rejected { reject_type, reason: reason.clone() });
                return Some(SessionEnd::Fatal(format!("rejected by server: {reason}")));
            }
            M::CryptSetup(c) => match (&c.key, &c.client_nonce, &c.server_nonce) {
                (Some(key), Some(client_nonce), Some(server_nonce)) => {
                    if self.crypt.set_key(key, client_nonce, server_nonce) {
                        self.send_udp_ping();
                    } else {
                        tracing::warn!("invalid CryptSetup key material");
                    }
                }
                (_, _, Some(server_nonce)) => {
                    self.crypt.set_decrypt_iv(server_nonce);
                }
                _ => {
                    let nonce = Bytes::copy_from_slice(self.crypt.encrypt_iv());
                    self.send(M::CryptSetup(proto::CryptSetup { client_nonce: Some(nonce), ..Default::default() }));
                }
            },
            M::CodecVersion(c) => {
                self.shared.state.write().server.opus = c.opus();
            }
            M::ChannelState(cs) => {
                let applied = self.shared.state.write().apply_channel_state(&cs);
                if let Some((channel, is_new)) = applied {
                    self.emit(if is_new { Event::ChannelAdded { channel } } else { Event::ChannelUpdated { channel } });
                }
            }
            M::ChannelRemove(cr) => {
                self.shared.state.write().remove_channel(cr.channel_id);
                self.emit(Event::ChannelRemoved { channel_id: cr.channel_id });
            }
            M::UserState(us) => {
                let actor = us.actor;
                let applied = self.shared.state.write().apply_user_state(&us);
                if let Some((user, is_new, moved)) = applied {
                    let session = user.session;
                    let to = user.channel_id;
                    if is_new {
                        self.emit(Event::UserJoined { user });
                    } else {
                        self.emit(Event::UserUpdated { user, actor });
                    }
                    if let Some(from) = moved {
                        self.emit(Event::UserMoved { session, from_channel_id: from, to_channel_id: to, actor });
                    }
                }
            }
            M::UserRemove(ur) => {
                let me = self.my_session();
                self.shared.state.write().users.remove(&ur.session);
                self.shared.audio.remove_speaker(ur.session);
                if self.talking.remove(&ur.session).is_some() {
                    self.emit(Event::UserTalking { session: ur.session, talking: false });
                }
                let ban = ur.ban();
                if me == Some(ur.session) {
                    let reason = ur.reason.clone().unwrap_or_default();
                    self.emit(Event::Kicked { actor: ur.actor, reason: reason.clone(), ban });
                    return Some(SessionEnd::Fatal(format!(
                        "{} by server: {reason}",
                        if ban { "banned" } else { "kicked" }
                    )));
                }
                self.emit(Event::UserLeft { session: ur.session, actor: ur.actor, reason: ur.reason, ban });
            }
            M::ServerSync(ss) => {
                let server = {
                    let mut st = self.shared.state.write();
                    st.session = ss.session;
                    st.synchronized = true;
                    if let Some(w) = &ss.welcome_text {
                        st.server.welcome_text.clone_from(w);
                    }
                    if let Some(b) = ss.max_bandwidth {
                        st.server.max_bandwidth = b;
                    }
                    st.server.root_permissions = ss.permissions.unwrap_or_default();
                    st.server.clone()
                };
                *synced = true;
                self.shared.set_connection_state(ConnectionState::Connected);
                self.emit(Event::Connected {
                    session: ss.session.unwrap_or_default(),
                    welcome_text: server.welcome_text.clone(),
                    server,
                });
            }
            M::ServerConfig(sc) => {
                let server = {
                    let mut st = self.shared.state.write();
                    let s = &mut st.server;
                    if let Some(v) = sc.max_bandwidth {
                        s.max_bandwidth = v;
                    }
                    if let Some(v) = sc.welcome_text {
                        s.welcome_text = v;
                    }
                    if let Some(v) = sc.allow_html {
                        s.allow_html = v;
                    }
                    if let Some(v) = sc.message_length {
                        s.message_length = v;
                    }
                    if let Some(v) = sc.image_message_length {
                        s.image_message_length = v;
                    }
                    if let Some(v) = sc.max_users {
                        s.max_users = v;
                    }
                    if let Some(v) = sc.recording_allowed {
                        s.recording_allowed = v;
                    }
                    s.clone()
                };
                self.emit(Event::ServerConfigUpdated { server });
            }
            M::SuggestConfig(sc) => {
                let server = {
                    let mut st = self.shared.state.write();
                    st.server.suggested_positional = sc.positional;
                    st.server.suggested_push_to_talk = sc.push_to_talk;
                    st.server.clone()
                };
                self.emit(Event::ServerConfigUpdated { server });
            }
            M::TextMessage(tm) => self.emit(Event::TextMessage {
                actor: tm.actor,
                sessions: tm.session,
                channel_ids: tm.channel_id,
                tree_ids: tm.tree_id,
                message: tm.message,
            }),
            M::PermissionDenied(pd) => self.emit(Event::PermissionDenied {
                deny_type: format!("{:?}", pd.r#type()),
                reason: pd.reason,
                permission: pd.permission,
                channel_id: pd.channel_id,
                session: pd.session,
                name: pd.name,
            }),
            M::PermissionQuery(pq) => {
                {
                    let mut st = self.shared.state.write();
                    if pq.flush() {
                        for ch in st.channels.values_mut() {
                            ch.permissions = None;
                        }
                    }
                    if let (Some(id), Some(p)) = (pq.channel_id, pq.permissions) {
                        if let Some(ch) = st.channels.get_mut(&id) {
                            ch.permissions = Some(p);
                        }
                    }
                }
                self.emit(Event::PermissionsUpdated { channel_id: pq.channel_id, permissions: pq.permissions, flush: pq.flush() });
            }
            M::Ping(p) => {
                self.last_tcp_pong = Instant::now();
                if let Some(ts) = p.timestamp {
                    let rtt_ms = self.now_us().saturating_sub(ts) as f64 / 1000.0;
                    self.tcp_ping.push(rtt_ms);
                    self.shared.state.write().tcp_ping_ms = self.tcp_ping.mean();
                }
                self.crypt.remote.good = p.good();
                self.crypt.remote.late = p.late();
                self.crypt.remote.lost = p.lost();
                self.crypt.remote.resync = p.resync();
            }
            M::UdpTunnel(data) => {
                self.tcp_packets = self.tcp_packets.wrapping_add(1);
                self.handle_plain_udp(&data);
            }
            M::UserStats(us) => {
                let version = us.version.as_ref().map(|v| Version::from_message(v.version_v1, v.version_v2).to_string());
                let os = us.version.as_ref().and_then(|v| v.os.clone());
                self.emit(Event::UserStats {
                    session: us.session.unwrap_or_default(),
                    stats_only: us.stats_only(),
                    tcp_ping_ms: us.tcp_ping_avg(),
                    udp_ping_ms: us.udp_ping_avg(),
                    online_seconds: us.onlinesecs,
                    idle_seconds: us.idlesecs,
                    bandwidth: us.bandwidth,
                    version,
                    os,
                    address: us.address.as_ref().map(|a| format_ip(a)),
                    strong_certificate: us.strong_certificate(),
                    opus: us.opus(),
                    from_client: proto_stats(us.from_client),
                    from_server: proto_stats(us.from_server),
                });
            }
            M::BanList(bl) => self.emit(Event::BanList {
                bans: bl
                    .bans
                    .into_iter()
                    .map(|b| BanEntry {
                        address: format_ip(&b.address),
                        mask: b.mask,
                        name: b.name,
                        certificate_hash: b.hash,
                        reason: b.reason,
                        start: b.start,
                        duration_seconds: b.duration,
                    })
                    .collect(),
            }),
            M::UserList(ul) => self.emit(Event::RegisteredUsers {
                users: ul
                    .users
                    .into_iter()
                    .map(|u| RegisteredUser { user_id: u.user_id, name: u.name, last_seen: u.last_seen, last_channel: u.last_channel })
                    .collect(),
            }),
            M::Acl(acl) => self.emit(Event::Acl {
                channel_id: acl.channel_id,
                inherit_acls: acl.inherit_acls(),
                groups: acl
                    .groups
                    .iter()
                    .map(|g| AclGroup {
                        name: g.name.clone(),
                        inherited: g.inherited(),
                        inherit: g.inherit(),
                        inheritable: g.inheritable(),
                        add: g.add.clone(),
                        remove: g.remove.clone(),
                        inherited_members: g.inherited_members.clone(),
                    })
                    .collect(),
                acls: acl
                    .acls
                    .iter()
                    .map(|a| AclEntry {
                        apply_here: a.apply_here(),
                        apply_subs: a.apply_subs(),
                        inherited: a.inherited(),
                        user_id: a.user_id,
                        group: a.group.clone(),
                        grant: a.grant(),
                        deny: a.deny(),
                    })
                    .collect(),
            }),
            M::QueryUsers(q) => self.emit(Event::UsersQueried { ids: q.ids, names: q.names }),
            M::ContextActionModify(c) => self.emit(Event::ContextActionModified {
                remove: c.operation() == proto::context_action_modify::Operation::Remove,
                context: c.context(),
                text: c.text,
                action: c.action,
            }),
            M::PluginDataTransmission(p) => self.emit(Event::PluginData {
                sender_session: p.sender_session,
                data_id: p.data_id.unwrap_or_default(),
                data: p.data.map(|d| d.to_vec()).unwrap_or_default(),
            }),
            _ => {}
        }
        None
    }

    // ---------------------------------------------------------------- heartbeat

    fn send_udp_ping(&mut self) {
        if !self.crypt.is_valid() || self.shared.config.force_tcp_voice {
            return;
        }
        let ping = PingPacket { timestamp: self.now_us(), ..Default::default() };
        voice::encode_ping(&ping, self.format, &mut self.voice_buf);
        self.send_udp_plain();
    }

    fn send_udp_plain(&mut self) {
        self.crypt_buf.resize(self.voice_buf.len() + rumble_protocol::crypt::OVERHEAD, 0);
        if let Some(n) = self.crypt.encrypt(&self.voice_buf, &mut self.crypt_buf) {
            let _ = self.udp.try_send(&self.crypt_buf[..n]);
        }
    }

    fn on_ping_tick(&mut self) -> Option<SessionEnd> {
        if self.last_tcp_pong.elapsed() > Duration::from_millis(self.shared.config.ping_timeout_ms) {
            return Some(SessionEnd::Failed("ping timeout".into()));
        }
        let local = self.crypt.local;
        let timestamp = self.now_us();
        self.send(M::Ping(proto::Ping {
            timestamp: Some(timestamp),
            good: Some(local.good),
            late: Some(local.late),
            lost: Some(local.lost),
            resync: Some(local.resync),
            udp_packets: Some(self.udp_packets),
            tcp_packets: Some(self.tcp_packets),
            udp_ping_avg: Some(self.udp_ping.mean()),
            udp_ping_var: Some(self.udp_ping.var()),
            tcp_ping_avg: Some(self.tcp_ping.mean()),
            tcp_ping_var: Some(self.tcp_ping.var()),
        }));

        if self.crypt.is_valid() && !self.shared.config.force_tcp_voice {
            self.udp_unanswered += 1;
            if self.udp_unanswered > 3 && self.udp_active {
                tracing::info!("UDP unresponsive, tunnelling voice over TCP");
                self.set_udp_active(false);
            }
            self.send_udp_ping();
        }

        self.emit(Event::PingUpdated {
            tcp_ping_ms: self.tcp_ping.mean(),
            udp_ping_ms: self.udp_ping.mean(),
            udp_active: self.udp_active,
            local: packet_stats(self.crypt.local),
            remote: packet_stats(self.crypt.remote),
        });
        None
    }

    fn set_udp_active(&mut self, active: bool) {
        self.udp_active = active;
        self.shared.state.write().udp_active = active;
    }

    fn housekeeping(&mut self) {
        let now = Instant::now();
        let expired: Vec<u32> = self
            .talking
            .iter()
            .filter(|(_, t)| now.duration_since(**t) > Duration::from_millis(400))
            .map(|(s, _)| *s)
            .collect();
        for session in expired {
            self.talking.remove(&session);
            self.emit(Event::UserTalking { session, talking: false });
        }

        let transmitting = self.shared.audio.capture_control().is_transmitting();
        if transmitting != self.self_talking {
            if let Some(me) = self.my_session() {
                self.self_talking = transmitting;
                self.emit(Event::UserTalking { session: me, talking: transmitting });
            }
        }
    }

    // ---------------------------------------------------------------- voice

    fn handle_udp(&mut self, datagram: &[u8]) {
        if !self.crypt.is_valid() || datagram.len() < rumble_protocol::crypt::OVERHEAD {
            return;
        }
        if self.plain_buf.len() < datagram.len() {
            self.plain_buf.resize(datagram.len(), 0);
        }
        let mut plain = std::mem::take(&mut self.plain_buf);
        match self.crypt.decrypt(datagram, &mut plain) {
            Some(n) => {
                self.decrypt_failures = 0;
                self.udp_packets = self.udp_packets.wrapping_add(1);
                self.handle_plain_udp(&plain[..n]);
            }
            None => {
                self.decrypt_failures += 1;
                let due = self.last_resync_request.is_none_or(|t| t.elapsed() > Duration::from_secs(5));
                if self.decrypt_failures > 8 && due {
                    tracing::debug!("requesting UDP crypt resync");
                    self.last_resync_request = Some(Instant::now());
                    self.send(M::CryptSetup(proto::CryptSetup::default()));
                }
            }
        }
        self.plain_buf = plain;
    }

    fn handle_plain_udp(&mut self, data: &[u8]) {
        let packet = voice::decode_udp(data, self.format, true).or_else(|_| {
            let other = if self.format == VoiceFormat::Legacy { VoiceFormat::Protobuf } else { VoiceFormat::Legacy };
            voice::decode_udp(data, other, true)
        });
        match packet {
            Ok(UdpPacket::Ping(p)) => {
                let rtt_ms = self.now_us().saturating_sub(p.timestamp) as f64 / 1000.0;
                self.udp_ping.push(rtt_ms);
                self.udp_unanswered = 0;
                self.shared.state.write().udp_ping_ms = self.udp_ping.mean();
                if !self.udp_active && !self.shared.config.force_tcp_voice {
                    self.set_udp_active(true);
                }
            }
            Ok(UdpPacket::Voice(v)) => self.handle_voice(v),
            Err(e) => tracing::trace!("dropping malformed voice packet: {e}"),
        }
    }

    fn handle_voice(&mut self, v: VoicePacket) {
        let Some(session) = v.session else { return };
        let now = Instant::now();
        if self.talking.insert(session, now).is_none() && !v.is_terminator {
            self.emit(Event::UserTalking { session, talking: true });
        }
        if v.is_terminator && self.talking.remove(&session).is_some() {
            self.emit(Event::UserTalking { session, talking: false });
        }
        let codec = if v.codec == Codec::Opus { StreamCodec::Opus } else { StreamCodec::Legacy };
        self.shared.audio.route(session, move |arrival_us| IncomingVoice {
            codec,
            sequence: v.sequence,
            payload: v.payload,
            position: v.position,
            is_terminator: v.is_terminator,
            volume_adjustment: v.volume_adjustment,
            arrival_us,
        });
    }

    fn send_voice(&mut self, v: EncodedVoice) {
        // Mumble servers only relay Opus.
        if v.codec != StreamCodec::Opus || self.my_session().is_none() {
            return;
        }
        let mut packet = VoicePacket::opus(v.target, v.sequence, v.payload, v.is_terminator);
        if self.shared.config.positional_transmit {
            packet.position = self.shared.audio.outgoing_position();
        }
        voice::encode_voice(&packet, self.format, false, &mut self.voice_buf);
        if self.udp_active && self.crypt.is_valid() && !self.shared.config.force_tcp_voice {
            self.send_udp_plain();
        } else {
            let data = Bytes::copy_from_slice(&self.voice_buf);
            self.send(M::UdpTunnel(data));
        }
    }

    // ---------------------------------------------------------------- commands

    fn handle_command(&mut self, cmd: Command) {
        let me = self.my_session();
        let user_state = |f: &dyn Fn(&mut proto::UserState)| {
            let mut us = proto::UserState { session: me, ..Default::default() };
            f(&mut us);
            M::UserState(us)
        };
        let other_user = |session: u32, f: &dyn Fn(&mut proto::UserState)| {
            let mut us = proto::UserState { session: Some(session), ..Default::default() };
            f(&mut us);
            M::UserState(us)
        };

        let msg = match cmd {
            Command::Disconnect => return,
            Command::JoinChannel { channel_id } => user_state(&|u| u.channel_id = Some(channel_id)),
            Command::SendTextMessage { channel_ids, sessions, tree_ids, message } => M::TextMessage(proto::TextMessage {
                actor: me,
                session: sessions,
                channel_id: channel_ids,
                tree_id: tree_ids,
                message,
            }),
            Command::SetSelfMute { mute } => {
                self.shared.audio.set_capture_muted(mute);
                if !mute {
                    self.shared.audio.set_deafened(false);
                }
                user_state(&|u| {
                    u.self_mute = Some(mute);
                    if !mute {
                        u.self_deaf = Some(false);
                    }
                })
            }
            Command::SetSelfDeaf { deaf } => {
                self.shared.audio.set_deafened(deaf);
                if deaf {
                    self.shared.audio.set_capture_muted(true);
                }
                user_state(&|u| {
                    u.self_deaf = Some(deaf);
                    if deaf {
                        u.self_mute = Some(true);
                    }
                })
            }
            Command::SetComment { comment } => user_state(&|u| u.comment = Some(comment.clone())),
            Command::SetTexture { texture } => {
                let t = Bytes::from(texture);
                user_state(&|u| u.texture = Some(t.clone()))
            }
            Command::RegisterSelf => user_state(&|u| u.user_id = Some(0)),
            Command::SetRecording { recording } => user_state(&|u| u.recording = Some(recording)),
            Command::MoveUser { session, channel_id } => other_user(session, &|u| u.channel_id = Some(channel_id)),
            Command::SetUserMute { session, mute } => other_user(session, &|u| u.mute = Some(mute)),
            Command::SetUserDeaf { session, deaf } => other_user(session, &|u| u.deaf = Some(deaf)),
            Command::SetPrioritySpeaker { session, priority } => {
                other_user(session, &|u| u.priority_speaker = Some(priority))
            }
            Command::SetUserSuppressed { session, suppress } => other_user(session, &|u| u.suppress = Some(suppress)),
            Command::KickUser { session, reason } => {
                M::UserRemove(proto::UserRemove { session, actor: me, reason, ban: Some(false) })
            }
            Command::BanUser { session, reason } => {
                M::UserRemove(proto::UserRemove { session, actor: me, reason, ban: Some(true) })
            }
            Command::CreateChannel { parent_id, name, description, temporary, position, max_users } => {
                M::ChannelState(proto::ChannelState {
                    parent: Some(parent_id),
                    name: Some(name),
                    description,
                    temporary: Some(temporary),
                    position,
                    max_users,
                    ..Default::default()
                })
            }
            Command::UpdateChannel { channel_id, name, description, parent_id, position, max_users } => {
                M::ChannelState(proto::ChannelState {
                    channel_id: Some(channel_id),
                    parent: parent_id,
                    name,
                    description,
                    position,
                    max_users,
                    ..Default::default()
                })
            }
            Command::RemoveChannel { channel_id } => M::ChannelRemove(proto::ChannelRemove { channel_id }),
            Command::LinkChannels { channel_id, targets } => M::ChannelState(proto::ChannelState {
                channel_id: Some(channel_id),
                links_add: targets,
                ..Default::default()
            }),
            Command::UnlinkChannels { channel_id, targets } => M::ChannelState(proto::ChannelState {
                channel_id: Some(channel_id),
                links_remove: targets,
                ..Default::default()
            }),
            Command::ListenToChannels { add, remove } => user_state(&|u| {
                u.listening_channel_add.clone_from(&add);
                u.listening_channel_remove.clone_from(&remove);
            }),
            Command::RegisterVoiceTarget { id, targets } => M::VoiceTarget(proto::VoiceTarget {
                id: Some(id),
                targets: targets
                    .into_iter()
                    .map(|t| proto::voice_target::Target {
                        session: t.sessions,
                        channel_id: t.channel_id,
                        group: t.group,
                        links: Some(t.links),
                        children: Some(t.children),
                    })
                    .collect(),
            }),
            Command::RequestUserStats { session, stats_only } => M::UserStats(proto::UserStats {
                session: Some(session),
                stats_only: Some(stats_only),
                ..Default::default()
            }),
            Command::RequestBanList => M::BanList(proto::BanList { bans: Vec::new(), query: Some(true) }),
            Command::SetBanList { bans } => M::BanList(proto::BanList {
                query: Some(false),
                bans: bans
                    .into_iter()
                    .map(|b| proto::ban_list::BanEntry {
                        address: parse_ip(&b.address),
                        mask: b.mask,
                        name: b.name,
                        hash: b.certificate_hash,
                        reason: b.reason,
                        start: None,
                        duration: b.duration_seconds,
                    })
                    .collect(),
            }),
            Command::RequestRegisteredUsers => M::UserList(proto::UserList { users: Vec::new() }),
            Command::RequestAcl { channel_id } => {
                M::Acl(proto::Acl { channel_id, query: Some(true), ..Default::default() })
            }
            Command::QueryPermissions { channel_id } => {
                M::PermissionQuery(proto::PermissionQuery { channel_id: Some(channel_id), ..Default::default() })
            }
            Command::QueryUsers { ids, names } => M::QueryUsers(proto::QueryUsers { ids, names }),
            Command::RequestBlob { session_textures, session_comments, channel_descriptions } => {
                M::RequestBlob(proto::RequestBlob {
                    session_texture: session_textures,
                    session_comment: session_comments,
                    channel_description: channel_descriptions,
                })
            }
            Command::SendPluginData { receivers, data_id, data } => {
                M::PluginDataTransmission(proto::PluginDataTransmission {
                    sender_session: me,
                    receiver_sessions: receivers,
                    data: Some(Bytes::from(data)),
                    data_id: Some(data_id),
                })
            }
            Command::ExecuteContextAction { action, session, channel_id } => {
                M::ContextAction(proto::ContextAction { session, channel_id, action })
            }
            Command::Raw(m) => *m,
        };
        self.send(msg);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backoff_grows_and_caps() {
        let a = backoff(1000, 30_000, 1).as_millis();
        let b = backoff(1000, 30_000, 4).as_millis();
        let c = backoff(1000, 30_000, 30).as_millis();
        assert!((800..=1200).contains(&a));
        assert!((6400..=9600).contains(&b));
        assert!(c <= 36_000);
    }

    #[test]
    fn ip_formatting() {
        assert_eq!(format_ip(&parse_ip("192.168.1.10")), "192.168.1.10");
        assert_eq!(format_ip(&parse_ip("2001:db8::1")), "2001:db8::1");
    }
}
