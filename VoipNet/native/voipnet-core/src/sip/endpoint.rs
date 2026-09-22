//! SIP user agent: registration, calls (UAC/UAS), hold, transfer, DTMF, messaging and conferencing.

use std::collections::HashMap;
use std::net::{IpAddr, SocketAddr};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc;
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use parking_lot::Mutex;
use serde::{Deserialize, Serialize};

use super::auth::DigestChallenge;
use super::message::{new_branch, random_token, split_list, Method, SipMessage};
use super::tls::{TlsContext, TlsSettings};
use super::transport::{Transport, TransportKind};
use super::uri::{NameAddr, SipUri};
use crate::codec::CodecKind;
use crate::media::conference::Conference;
use crate::media::dtls::{DtlsIdentity, DtlsRole};
use crate::media::{AudioDirection, DtmfMode, DtmfSource, MediaConfig, MediaSession, MediaSink, MediaStats, NegotiatedMedia};
use crate::net;
use crate::sdp::{negotiate, CryptoAttr, Direction, MediaDescription, RtpMap, SessionDescription};
use crate::srtp::SUITE_AES_CM_128_HMAC_SHA1_80;
use crate::stun::Candidate;

const T1: Duration = Duration::from_millis(500);
const T2: Duration = Duration::from_secs(4);
const TX_TIMEOUT: Duration = Duration::from_secs(32);

// ---------------------------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SrtpMode {
    #[default]
    Disabled,
    Optional,
    Mandatory,
}

/// How SRTP master keys are exchanged.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SrtpKeying {
    /// Keys in `a=crypto` (RFC 4568); requires a secure signaling path such as TLS.
    #[default]
    Sdes,
    /// DTLS-SRTP handshake on the media path (RFC 5763/5764), as used by WebRTC.
    Dtls,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum DtmfModeConfig {
    #[default]
    Rfc4733,
    InBand,
    SipInfo,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct EndpointConfig {
    pub bind_address: String,
    pub sip_port: u16,
    pub transport: TransportKind,
    pub public_address: Option<String>,
    pub display_name: String,
    pub username: String,
    pub auth_username: Option<String>,
    pub password: String,
    pub domain: String,
    pub registrar: Option<String>,
    pub outbound_proxy: Option<String>,
    pub register_on_start: bool,
    pub register_expires: u32,
    pub user_agent: String,
    pub audio_codecs: Vec<String>,
    /// Offer and accept a video stream alongside the audio one.
    pub video: bool,
    pub video_codecs: Vec<String>,
    pub srtp: SrtpMode,
    pub srtp_keying: SrtpKeying,
    pub dtmf_mode: DtmfModeConfig,
    pub ice: bool,
    pub stun_server: Option<String>,
    pub turn_server: Option<String>,
    pub turn_username: String,
    pub turn_password: String,
    pub rtp_port_min: u16,
    pub rtp_port_max: u16,
    pub ptime_ms: u32,
    pub jitter_min_ms: u32,
    pub jitter_max_ms: u32,
    pub detect_inband_dtmf: bool,
    pub auto_ringing: bool,
    pub accept_transfers: bool,
    pub trace_sip: bool,
    pub keepalive_secs: u32,
    pub rtp_timeout_ms: u32,
    /// Let Opus stop sending during silence (saves bandwidth, some PBXs dislike it).
    pub opus_dtx: bool,
    /// Remove the echo of the played-out audio from what the application sends.
    pub echo_cancellation: bool,
    /// Suppress steady background noise in what the application sends.
    pub noise_suppression: bool,
    /// Even out the level of what the application sends.
    pub auto_gain: bool,
    pub tls_verify_server: bool,
    pub tls_ca_file: Option<String>,
    pub tls_pinned_fingerprints: Vec<String>,
    pub tls_certificate_file: Option<String>,
    pub tls_private_key_file: Option<String>,
    /// Ask TLS and WSS callers for a certificate and refuse untrusted ones (mutual TLS).
    pub tls_require_client_certificate: bool,
}

impl Default for EndpointConfig {
    fn default() -> Self {
        Self {
            bind_address: "0.0.0.0".into(),
            sip_port: 5060,
            transport: TransportKind::Udp,
            public_address: None,
            display_name: String::new(),
            username: "voipnet".into(),
            auth_username: None,
            password: String::new(),
            domain: String::new(),
            registrar: None,
            outbound_proxy: None,
            register_on_start: false,
            register_expires: 600,
            user_agent: format!("Voip.NET/{}", env!("CARGO_PKG_VERSION")),
            audio_codecs: vec!["opus".into(), "G722".into(), "PCMU".into(), "PCMA".into()],
            video: false,
            video_codecs: vec!["H264".into(), "VP8".into()],
            srtp: SrtpMode::Disabled,
            srtp_keying: SrtpKeying::Sdes,
            dtmf_mode: DtmfModeConfig::Rfc4733,
            ice: false,
            stun_server: None,
            turn_server: None,
            turn_username: String::new(),
            turn_password: String::new(),
            rtp_port_min: 10000,
            rtp_port_max: 20000,
            ptime_ms: 20,
            jitter_min_ms: 40,
            jitter_max_ms: 300,
            detect_inband_dtmf: false,
            auto_ringing: true,
            accept_transfers: true,
            trace_sip: false,
            keepalive_secs: 25,
            rtp_timeout_ms: 0,
            opus_dtx: false,
            echo_cancellation: false,
            noise_suppression: false,
            auto_gain: false,
            tls_verify_server: true,
            tls_ca_file: None,
            tls_pinned_fingerprints: Vec::new(),
            tls_certificate_file: None,
            tls_private_key_file: None,
            tls_require_client_certificate: false,
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Events
// ---------------------------------------------------------------------------------------------

#[derive(Debug, Clone, Serialize)]
#[serde(tag = "type", rename_all = "camelCase", rename_all_fields = "camelCase")]
pub enum Event {
    RegistrationChanged { state: &'static str, code: u16, reason: String, expires: u32 },
    IncomingCall { call_id: u64, from: String, from_display: Option<String>, to: String, sip_call_id: String, has_video: bool, replaces_call_id: Option<u64> },
    CallState { call_id: u64, state: &'static str, code: u16, reason: String },
    MediaStarted { call_id: u64, codec: String, sample_rate: u32, remote: String, srtp: bool, direction: &'static str },
    TransferRequested { call_id: u64, target: String, new_call_id: Option<u64> },
    TransferProgress { call_id: u64, code: u16, reason: String },
    MessageReceived { from: String, content_type: String, body: String },
    RequestResult { request_id: u64, method: String, code: u16, reason: String, latency_ms: u64, user_agent: Option<String> },
    MediaEvent { call_id: u64, kind: String, detail: String },
    SipTrace { direction: &'static str, remote: String, message: String },
    Log { level: &'static str, message: String },
}

/// Application callbacks. Events are delivered on a dedicated dispatcher thread (in order);
/// audio/encoded callbacks come from media threads.
pub trait EndpointHandler: Send + Sync {
    fn on_event(&self, event: &Event);
    fn on_audio(&self, call_id: u64, direction: AudioDirection, sample_rate: u32, pcm: &[i16]);
    fn on_dtmf(&self, call_id: u64, digit: char, source: DtmfSource);
    fn on_encoded(&self, call_id: u64, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]);
    /// A complete video frame was received; `frame` is one H.264 access unit (Annex B) or VP8 frame.
    fn on_video_frame(&self, _call_id: u64, _timestamp: u32, _keyframe: bool, _frame: &[u8]) {}
}

enum Dispatch {
    Event(Event),
    Dtmf(u64, char, DtmfSource),
}

struct SinkAdapter {
    handler: Arc<dyn EndpointHandler>,
    tx: Mutex<mpsc::Sender<Dispatch>>,
}

impl MediaSink for SinkAdapter {
    fn on_audio(&self, call_id: u64, direction: AudioDirection, sample_rate: u32, pcm: &[i16]) {
        self.handler.on_audio(call_id, direction, sample_rate, pcm);
    }
    fn on_dtmf(&self, call_id: u64, digit: char, source: DtmfSource) {
        let _ = self.tx.lock().send(Dispatch::Dtmf(call_id, digit, source));
    }
    fn on_encoded(&self, call_id: u64, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]) {
        self.handler.on_encoded(call_id, payload_type, timestamp, marker, payload);
    }
    fn on_media_event(&self, call_id: u64, kind: &str, detail: &str) {
        let _ = self.tx.lock().send(Dispatch::Event(Event::MediaEvent { call_id, kind: kind.into(), detail: detail.into() }));
    }
    fn on_video_frame(&self, call_id: u64, timestamp: u32, keyframe: bool, frame: &[u8]) {
        self.handler.on_video_frame(call_id, timestamp, keyframe, frame);
    }
}

// ---------------------------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum CallState {
    Calling,
    Ringing,
    EarlyMedia,
    Incoming,
    Connected,
    OnHold,
    RemoteHold,
    Terminated,
}

impl CallState {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Calling => "calling",
            Self::Ringing => "ringing",
            Self::EarlyMedia => "early-media",
            Self::Incoming => "incoming",
            Self::Connected => "connected",
            Self::OnHold => "on-hold",
            Self::RemoteHold => "remote-hold",
            Self::Terminated => "terminated",
        }
    }

    fn is_established(self) -> bool {
        matches!(self, Self::Connected | Self::OnHold | Self::RemoteHold)
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CallInfo {
    pub call_id: u64,
    pub sip_call_id: String,
    pub outgoing: bool,
    pub state: CallState,
    pub remote_uri: String,
    pub remote_display: Option<String>,
    pub local_uri: String,
    pub codec: Option<String>,
    pub duration_ms: u64,
    pub local_hold: bool,
    pub remote_hold: bool,
    pub muted: bool,
    pub srtp: bool,
}

#[derive(Clone)]
enum Purpose {
    Register,
    Invite(u64),
    ReInvite(u64),
    Bye(u64),
    Cancel,
    Refer(u64),
    Info(u64),
    Notify(u64),
    OutOfDialog { request_id: u64, started: Instant },
}

struct ClientTx {
    request: SipMessage,
    bytes: Vec<u8>,
    dest: SocketAddr,
    purpose: Purpose,
    started: Instant,
    next_retransmit: Instant,
    interval: Duration,
    provisional: bool,
    completed_at: Option<Instant>,
    auth_attempts: u8,
    /// A reliable transport could not deliver the request (RFC 3261 §8.1.3.1: treat as 503).
    transport_failed: bool,
}

struct ServerTx {
    last_response: Vec<u8>,
    dest: SocketAddr,
    created: Instant,
    invite_final: Option<(Instant, Duration)>,
    acked: bool,
}

struct Call {
    id: u64,
    sip_call_id: String,
    outgoing: bool,
    state: CallState,
    local_tag: String,
    remote_tag: Option<String>,
    local_uri: String,
    remote_uri: String,
    remote_target: String,
    route_set: Vec<String>,
    local_cseq: u32,
    remote_cseq: u32,
    invite: Option<SipMessage>,
    invite_branch: String,
    peer: SocketAddr,
    media: Option<Arc<MediaSession>>,
    /// Second media session carrying video, with its own port; `None` on audio-only calls.
    video: Option<Arc<MediaSession>>,
    /// Formats to put on the `m=video` line: everything we support in an offer, the chosen one in an answer.
    video_formats: Vec<RtpMap>,
    remote_offer: Option<SessionDescription>,
    session_id: u64,
    session_version: u64,
    local_hold: bool,
    remote_hold: bool,
    pending_cancel: bool,
    got_provisional: bool,
    created: Instant,
    connected_at: Option<Instant>,
    pending_2xx: Option<(Vec<u8>, Instant, Duration, Instant)>,
    last_ack: Option<(Vec<u8>, SocketAddr)>,
    refer_origin: Option<u64>,
    replaces: Option<u64>,
    codec_name: Option<String>,
    final_stats: Option<MediaStats>,
    redirects: u8,
}

impl Call {
    /// Detaches the media session, keeping its last statistics so quality can still be reported
    /// after the call has ended.
    fn take_media(&mut self) -> Option<Arc<MediaSession>> {
        if let Some(video) = self.video.take() {
            video.stop();
        }
        let media = self.media.take()?;
        self.final_stats = Some(media.stats());
        Some(media)
    }
}

#[derive(Default)]
struct Registration {
    call_id: String,
    local_tag: String,
    cseq: u32,
    registered: bool,
    expires: u32,
    refresh_at: Option<Instant>,
    unregistering: bool,
    last_keepalive: Option<Instant>,
    learned_public: Option<SocketAddr>,
}

#[derive(Default)]
struct State {
    calls: HashMap<u64, Call>,
    by_sip_id: HashMap<String, u64>,
    client_txs: HashMap<String, ClientTx>,
    server_txs: HashMap<String, ServerTx>,
    registration: Registration,
    nonce_count: u32,
    last_challenge: Option<(bool, DigestChallenge)>,
}

struct Inner {
    cfg: EndpointConfig,
    transport: Arc<Transport>,
    contact: Mutex<SocketAddr>,
    state: Mutex<State>,
    sink: Arc<SinkAdapter>,
    events: Mutex<mpsc::Sender<Dispatch>>,
    running: AtomicBool,
    ids: AtomicU64,
    threads: Mutex<Vec<JoinHandle<()>>>,
    media_cfg: MediaConfig,
    conferences: Mutex<HashMap<u64, Arc<Conference>>>,
    supported_audio: Vec<RtpMap>,
    supported_video: Vec<RtpMap>,
}

pub struct Endpoint {
    inner: Arc<Inner>,
}

#[derive(Debug)]
pub enum EndpointError {
    Io(std::io::Error),
    InvalidArgument(String),
    NotFound,
    InvalidState(&'static str),
}

impl std::fmt::Display for EndpointError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(e) => write!(f, "I/O error: {e}"),
            Self::InvalidArgument(s) => write!(f, "invalid argument: {s}"),
            Self::NotFound => f.write_str("call not found"),
            Self::InvalidState(s) => write!(f, "invalid state: {s}"),
        }
    }
}

impl std::error::Error for EndpointError {}

impl From<std::io::Error> for EndpointError {
    fn from(e: std::io::Error) -> Self {
        Self::Io(e)
    }
}

type Result<T> = std::result::Result<T, EndpointError>;

// ---------------------------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------------------------

impl Endpoint {
    pub fn start(cfg: EndpointConfig, handler: Arc<dyn EndpointHandler>) -> Result<Self> {
        let bind_ip: IpAddr = cfg.bind_address.parse().map_err(|_| EndpointError::InvalidArgument("bindAddress".into()))?;
        let tls = if matches!(cfg.transport, TransportKind::Tls | TransportKind::Wss) {
            Some(Arc::new(build_tls_context(&cfg)?))
        } else {
            None
        };
        let transport = Transport::bind(cfg.transport, SocketAddr::new(bind_ip, cfg.sip_port), tls)?;
        let bound = transport.local_addr();
        let advertised_ip = match &cfg.public_address {
            Some(p) => p.parse().map_err(|_| EndpointError::InvalidArgument("publicAddress".into()))?,
            None if bind_ip.is_unspecified() => net::primary_local_ip(),
            None => bind_ip,
        };
        let contact = SocketAddr::new(advertised_ip, bound.port());

        let media_cfg = MediaConfig {
            local_ip: bind_ip,
            advertised_ip: cfg.public_address.as_ref().and_then(|p| p.parse().ok()).or(Some(advertised_ip)),
            port_min: cfg.rtp_port_min,
            port_max: cfg.rtp_port_max,
            ptime_ms: cfg.ptime_ms,
            jitter_min_ms: cfg.jitter_min_ms,
            jitter_max_ms: cfg.jitter_max_ms,
            stun_server: cfg.stun_server.as_deref().and_then(|s| resolve_hostport(s, 3478)),
            turn_server: cfg.turn_server.as_deref().and_then(|s| resolve_hostport(s, 3478)),
            turn_username: cfg.turn_username.clone(),
            turn_password: cfg.turn_password.clone(),
            detect_inband_dtmf: cfg.detect_inband_dtmf,
            rtp_timeout_ms: cfg.rtp_timeout_ms,
            symmetric_rtp: true,
            opus_dtx: cfg.opus_dtx,
            echo_cancellation: cfg.echo_cancellation,
            noise_suppression: cfg.noise_suppression,
            auto_gain: cfg.auto_gain,
            dtls_identity: Some(DtlsIdentity::generate().map_err(EndpointError::InvalidArgument)?),
        };

        let mut supported_audio: Vec<RtpMap> = cfg
            .audio_codecs
            .iter()
            .filter_map(|n| CodecKind::parse(n))
            .filter(|k| k.is_native() && !k.is_video() && *k != CodecKind::TelephoneEvent)
            .map(CodecKind::rtpmap)
            .collect();
        if supported_audio.is_empty() {
            supported_audio = vec![CodecKind::Pcmu.rtpmap(), CodecKind::Pcma.rtpmap()];
        }
        let supported_video: Vec<RtpMap> = cfg
            .video_codecs
            .iter()
            .filter_map(|n| CodecKind::parse(n))
            .filter(|k| k.is_video())
            .map(CodecKind::rtpmap)
            .collect();
        supported_audio.push(CodecKind::TelephoneEvent.rtpmap());
        // RFC 4733 events use the audio clock, so wideband Opus calls need a 48 kHz telephone-event too.
        if supported_audio.iter().any(|m| m.clock_rate == 48000 && !m.encoding.eq_ignore_ascii_case("telephone-event")) {
            supported_audio.push(RtpMap { payload_type: 110, clock_rate: 48000, ..CodecKind::TelephoneEvent.rtpmap() });
        }

        let (tx, rx) = mpsc::channel::<Dispatch>();
        let sink = Arc::new(SinkAdapter { handler: handler.clone(), tx: Mutex::new(tx.clone()) });
        let inner = Arc::new(Inner {
            cfg,
            transport: transport.clone(),
            contact: Mutex::new(contact),
            state: Mutex::new(State::default()),
            sink,
            events: Mutex::new(tx),
            running: AtomicBool::new(true),
            ids: AtomicU64::new(1),
            threads: Mutex::new(Vec::new()),
            media_cfg,
            conferences: Mutex::new(HashMap::new()),
            supported_audio,
            supported_video,
        });

        let dispatcher_handler = handler;
        let dispatcher = std::thread::Builder::new()
            .name("voipnet-events".into())
            .spawn(move || {
                while let Ok(d) = rx.recv() {
                    match d {
                        Dispatch::Event(e) => dispatcher_handler.on_event(&e),
                        Dispatch::Dtmf(id, digit, src) => dispatcher_handler.on_dtmf(id, digit, src),
                    }
                }
            })
            .map_err(EndpointError::Io)?;

        let weak = Arc::downgrade(&inner);
        transport.start(Arc::new(move |msg, from| {
            if let Some(inner) = weak.upgrade() {
                inner.on_message(msg, from);
            }
        }));
        let disconnect_weak = Arc::downgrade(&inner);
        transport.on_disconnect(Arc::new(move |peer| {
            if let Some(inner) = disconnect_weak.upgrade() {
                inner.connection_lost(peer);
            }
        }));

        let timer_inner = Arc::downgrade(&inner);
        let timer = std::thread::Builder::new()
            .name("voipnet-sip-timers".into())
            .spawn(move || loop {
                std::thread::sleep(Duration::from_millis(50));
                let Some(inner) = timer_inner.upgrade() else { break };
                if !inner.running.load(Ordering::Relaxed) {
                    break;
                }
                inner.on_timer();
            })
            .map_err(EndpointError::Io)?;
        inner.threads.lock().extend([dispatcher, timer]);

        inner.log("info", format!("endpoint listening on {} ({:?}), contact {}", bound, inner.cfg.transport, contact));
        let endpoint = Self { inner };
        if endpoint.inner.cfg.register_on_start && !endpoint.inner.cfg.domain.is_empty() {
            endpoint.register()?;
        }
        Ok(endpoint)
    }

    pub fn local_address(&self) -> SocketAddr {
        self.inner.transport.local_addr()
    }

    /// SHA-256 fingerprint (`AA:BB:…`) of the certificate presented on the TLS transport.
    pub fn tls_fingerprint(&self) -> Option<String> {
        self.inner.transport.tls_fingerprint()
    }

    /// Re-reads the certificate, key and CA files and uses them for new connections. Calls already up
    /// keep the connection they have, so renewing a certificate never drops a call.
    pub fn reload_tls(&self) -> Result<()> {
        if !matches!(self.inner.cfg.transport, TransportKind::Tls | TransportKind::Wss) {
            return Err(EndpointError::InvalidState("this endpoint does not use TLS"));
        }
        let context = build_tls_context(&self.inner.cfg)?;
        let fingerprint = context.fingerprint.clone();
        self.inner.transport.set_tls_context(Arc::new(context));
        self.inner.log("info", format!("TLS certificate reloaded, fingerprint {fingerprint}"));
        Ok(())
    }

    pub fn contact_address(&self) -> SocketAddr {
        *self.inner.contact.lock()
    }

    pub fn register(&self) -> Result<()> {
        self.inner.send_register(false)
    }

    pub fn unregister(&self) -> Result<()> {
        self.inner.send_register(true)
    }

    pub fn make_call(&self, target: &str) -> Result<u64> {
        self.inner.make_call(target, None, None)
    }

    pub fn answer(&self, call_id: u64) -> Result<()> {
        self.inner.answer(call_id)
    }

    pub fn reject(&self, call_id: u64, code: u16) -> Result<()> {
        self.inner.reject(call_id, code)
    }

    pub fn hangup(&self, call_id: u64) -> Result<()> {
        self.inner.hangup(call_id)
    }

    pub fn set_hold(&self, call_id: u64, hold: bool) -> Result<()> {
        self.inner.set_hold(call_id, hold)
    }

    /// Restarts ICE with new credentials (for example after a network change).
    pub fn restart_ice(&self, call_id: u64) -> Result<()> {
        self.inner.restart_ice(call_id)
    }

    pub fn set_mute(&self, call_id: u64, mute: bool) -> Result<()> {
        let st = self.inner.state.lock();
        let media = st.calls.get(&call_id).ok_or(EndpointError::NotFound)?.media.clone();
        drop(st);
        media.ok_or(EndpointError::InvalidState("no media"))?.set_muted(mute);
        Ok(())
    }

    pub fn transfer(&self, call_id: u64, target: &str) -> Result<()> {
        self.inner.transfer(call_id, target, None)
    }

    pub fn transfer_attended(&self, call_id: u64, consult_call_id: u64) -> Result<()> {
        self.inner.transfer(call_id, "", Some(consult_call_id))
    }

    pub fn send_dtmf(&self, call_id: u64, digits: &str, duration_ms: u32) -> Result<()> {
        self.inner.send_dtmf(call_id, digits, duration_ms)
    }

    pub fn send_audio(&self, call_id: u64, pcm: &[i16], sample_rate: u32) -> Result<u32> {
        Ok(self.inner.media_of(call_id)?.send_audio(pcm, sample_rate))
    }

    pub fn clear_audio(&self, call_id: u64) -> Result<()> {
        self.inner.media_of(call_id)?.clear_audio();
        Ok(())
    }

    /// Sends one encoded video frame on the call's video stream. `timestamp` is in the 90 kHz video
    /// clock; the frame is split into as many RTP packets as it needs.
    pub fn send_video_frame(&self, call_id: u64, timestamp: u32, frame: &[u8]) -> Result<()> {
        let st = self.inner.state.lock();
        let video = st.calls.get(&call_id).ok_or(EndpointError::NotFound)?.video.clone();
        drop(st);
        let video = video.ok_or(EndpointError::InvalidState("call has no video stream"))?;
        if !video.send_video_frame(timestamp, frame) {
            return Err(EndpointError::InvalidState("video stream cannot send"));
        }
        Ok(())
    }

    /// Asks the peer for a video keyframe (RFC 4585 PLI, or RFC 5104 FIR when `full`). The engine also
    /// does this on its own when a frame arrives with packets missing.
    pub fn request_keyframe(&self, call_id: u64, full: bool) -> Result<()> {
        let st = self.inner.state.lock();
        let video = st.calls.get(&call_id).ok_or(EndpointError::NotFound)?.video.clone();
        drop(st);
        let video = video.ok_or(EndpointError::InvalidState("call has no video stream"))?;
        if !video.request_keyframe(full) {
            return Err(EndpointError::InvalidState("no video stream from the peer yet"));
        }
        Ok(())
    }

    /// The codec negotiated for the call's video stream, if it has one.
    pub fn video_codec(&self, call_id: u64) -> Result<Option<String>> {
        let st = self.inner.state.lock();
        let call = st.calls.get(&call_id).ok_or(EndpointError::NotFound)?;
        Ok(call.video.as_ref().and(call.video_formats.first()).map(|f| f.encoding.clone()))
    }

    pub fn send_encoded(&self, call_id: u64, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]) -> Result<()> {
        self.inner.media_of(call_id)?.send_encoded(payload_type, timestamp, marker, payload);
        Ok(())
    }

    pub fn call_stats(&self, call_id: u64) -> Result<MediaStats> {
        let st = self.inner.state.lock();
        let call = st.calls.get(&call_id).ok_or(EndpointError::NotFound)?;
        match (&call.media, call.final_stats) {
            (Some(media), _) => Ok(media.stats()),
            (None, Some(stats)) => Ok(stats),
            (None, None) => Err(EndpointError::InvalidState("no media")),
        }
    }

    pub fn call_info(&self, call_id: u64) -> Result<CallInfo> {
        let st = self.inner.state.lock();
        st.calls.get(&call_id).map(call_info).ok_or(EndpointError::NotFound)
    }

    pub fn calls(&self) -> Vec<CallInfo> {
        let st = self.inner.state.lock();
        let mut v: Vec<_> = st.calls.values().map(call_info).collect();
        v.sort_by_key(|c| c.call_id);
        v
    }

    pub fn send_options(&self, target: &str) -> Result<u64> {
        self.inner.out_of_dialog(Method::Options, target, None)
    }

    pub fn send_message(&self, target: &str, content_type: &str, body: &str) -> Result<u64> {
        self.inner.out_of_dialog(Method::Message, target, Some((content_type, body)))
    }

    pub fn conference_create(&self) -> u64 {
        let id = self.inner.ids.fetch_add(1, Ordering::Relaxed);
        self.inner.conferences.lock().insert(id, Arc::new(Conference::new()));
        id
    }

    pub fn conference_add(&self, conference_id: u64, call_id: u64) -> Result<()> {
        let conf = self.inner.conferences.lock().get(&conference_id).cloned().ok_or(EndpointError::NotFound)?;
        self.inner.media_of(call_id)?.join_conference(conf);
        Ok(())
    }

    pub fn conference_remove(&self, call_id: u64) -> Result<()> {
        self.inner.media_of(call_id)?.leave_conference();
        Ok(())
    }

    pub fn conference_destroy(&self, conference_id: u64) {
        self.inner.conferences.lock().remove(&conference_id);
    }

    pub fn shutdown(&self) {
        self.inner.shutdown();
    }
}

impl Drop for Endpoint {
    fn drop(&mut self) {
        self.inner.shutdown();
    }
}

fn call_info(c: &Call) -> CallInfo {
    let remote = NameAddr::parse(&c.remote_uri);
    CallInfo {
        call_id: c.id,
        sip_call_id: c.sip_call_id.clone(),
        outgoing: c.outgoing,
        state: c.state,
        remote_uri: remote.as_ref().map(|n| n.uri.aor()).unwrap_or_else(|| c.remote_uri.clone()),
        remote_display: remote.and_then(|n| n.display),
        local_uri: NameAddr::parse(&c.local_uri).map(|n| n.uri.aor()).unwrap_or_else(|| c.local_uri.clone()),
        codec: c.codec_name.clone(),
        duration_ms: c.connected_at.map_or(0, |t| t.elapsed().as_millis() as u64),
        local_hold: c.local_hold,
        remote_hold: c.remote_hold,
        muted: c.media.as_ref().is_some_and(|m| m.is_muted()),
        srtp: c.media.as_ref().is_some_and(|m| m.srtp_enabled()),
    }
}

/// Builds the TLS context from the configuration: the certificate to present, who to trust, and
/// whether callers must identify themselves.
fn build_tls_context(cfg: &EndpointConfig) -> Result<TlsContext> {
    let settings = TlsSettings {
        verify_server: cfg.tls_verify_server,
        ca_file: cfg.tls_ca_file.clone(),
        pinned_sha256: cfg.tls_pinned_fingerprints.clone(),
        certificate_file: cfg.tls_certificate_file.clone(),
        private_key_file: cfg.tls_private_key_file.clone(),
        require_client_certificate: cfg.tls_require_client_certificate,
    };
    let mut names = vec!["localhost".to_owned()];
    if !cfg.domain.is_empty() {
        names.push(cfg.domain.clone());
    }
    TlsContext::new(&settings, names).map_err(EndpointError::InvalidArgument)
}

fn resolve_hostport(s: &str, default_port: u16) -> Option<SocketAddr> {
    let s = s.trim().trim_start_matches("sip:").trim_start_matches("stun:").trim_start_matches("turn:");
    let (host, port) = super::uri::split_host_port(s)?;
    net::resolve(&host, port.unwrap_or(default_port))
}

// ---------------------------------------------------------------------------------------------
// Internals
// ---------------------------------------------------------------------------------------------

impl Inner {
    fn emit(&self, e: Event) {
        let _ = self.events.lock().send(Dispatch::Event(e));
    }

    fn log(&self, level: &'static str, message: String) {
        self.emit(Event::Log { level, message });
    }

    fn shutdown(&self) {
        if !self.running.swap(false, Ordering::SeqCst) {
            return;
        }
        let (medias, byes) = {
            let mut st = self.state.lock();
            let ids: Vec<u64> = st.calls.keys().copied().collect();
            let mut byes = Vec::new();
            for id in ids {
                if let Some(c) = st.calls.get_mut(&id) {
                    if c.state.is_established() {
                        c.local_cseq += 1;
                        let bye = self.dialog_request(c, Method::Bye);
                        byes.push((bye.to_bytes(), self.dialog_dest(c)));
                    }
                }
            }
            if st.registration.registered {
                // Best effort unregister.
                drop(st);
                let _ = self.send_register(true);
                st = self.state.lock();
            }
            let medias: Vec<_> = st.calls.values_mut().filter_map(|c| c.take_media()).collect();
            (medias, byes)
        };
        for (bytes, dest) in byes {
            let _ = self.transport.send(dest, &bytes);
        }
        for m in medias {
            m.stop();
        }
        self.transport.shutdown();
        for t in self.threads.lock().drain(..) {
            if t.thread().id() != std::thread::current().id() {
                // The timer thread exits on its own once `running` is false; the dispatcher exits when
                // all senders are dropped, so we do not join it here to avoid blocking on handler code.
                let _ = t;
            }
        }
    }

    fn contact_uri(&self) -> String {
        let c = *self.contact.lock();
        let user = &self.cfg.username;
        let host = match c.ip() {
            IpAddr::V6(ip) => format!("[{ip}]"),
            IpAddr::V4(ip) => ip.to_string(),
        };
        match self.cfg.transport.uri_param() {
            Some(t) => format!("<sip:{user}@{host}:{};transport={t}>", c.port()),
            None => format!("<sip:{user}@{host}:{}>", c.port()),
        }
    }

    fn via(&self, branch: &str) -> String {
        let c = *self.contact.lock();
        let host = match c.ip() {
            IpAddr::V6(ip) => format!("[{ip}]"),
            IpAddr::V4(ip) => ip.to_string(),
        };
        format!("SIP/2.0/{} {host}:{};branch={branch};rport", self.cfg.transport.via_name(), c.port())
    }

    fn domain(&self) -> String {
        if self.cfg.domain.is_empty() {
            self.contact.lock().ip().to_string()
        } else {
            self.cfg.domain.clone()
        }
    }

    fn local_aor(&self) -> String {
        format!("sip:{}@{}", self.cfg.username, self.domain())
    }

    fn local_name_addr(&self, tag: &str) -> String {
        let aor = self.local_aor();
        if self.cfg.display_name.is_empty() {
            format!("<{aor}>;tag={tag}")
        } else {
            format!("\"{}\" <{aor}>;tag={tag}", self.cfg.display_name.replace('"', ""))
        }
    }

    fn normalize_target(&self, target: &str) -> Result<SipUri> {
        let t = target.trim();
        let candidate = if t.starts_with("sip:") || t.starts_with("sips:") {
            t.to_owned()
        } else if t.contains('@') {
            format!("sip:{t}")
        } else {
            format!("sip:{t}@{}", self.domain())
        };
        SipUri::parse(&candidate).ok_or_else(|| EndpointError::InvalidArgument(format!("invalid SIP URI: {target}")))
    }

    fn proxy_addr(&self) -> Option<SocketAddr> {
        self.cfg.outbound_proxy.as_deref().and_then(|p| self.resolve_host(p))
    }

    /// Resolves `host[:port]` with the transport's default port, remembering the name for TLS.
    fn resolve_host(&self, s: &str) -> Option<SocketAddr> {
        let s = s.trim().trim_start_matches("sips:").trim_start_matches("sip:");
        let (host, port) = super::uri::split_host_port(s)?;
        let addr = net::resolve(&host, port.unwrap_or(self.cfg.transport.default_port()))?;
        self.transport.note_server_name(addr, &host);
        Some(addr)
    }

    fn resolve_uri(&self, uri: &SipUri) -> Option<SocketAddr> {
        let port = uri.port.unwrap_or(if uri.secure { 5061 } else { self.cfg.transport.default_port() });
        let addr = net::resolve(&uri.host, port)?;
        self.transport.note_server_name(addr, &uri.host);
        Some(addr)
    }

    fn destination_for(&self, uri: &SipUri, routes: &[String]) -> Option<SocketAddr> {
        if let Some(p) = self.proxy_addr() {
            return Some(p);
        }
        if let Some(first) = routes.first().and_then(|r| NameAddr::parse(r)) {
            return self.resolve_uri(&first.uri);
        }
        self.resolve_uri(uri)
    }

    /// A stream connection died (TLS rejection, restart, network loss): requests waiting on it have
    /// nowhere to go, so they fail now instead of after the 32 second transaction timeout.
    fn connection_lost(&self, peer: SocketAddr) {
        let mut st = self.state.lock();
        for tx in st.client_txs.values_mut() {
            if tx.dest == peer && tx.completed_at.is_none() {
                tx.transport_failed = true;
            }
        }
    }

    fn send_bytes(&self, dest: SocketAddr, msg: &SipMessage) -> Vec<u8> {
        self.try_send_bytes(dest, msg).0
    }

    /// Sends a message and reports whether the transport accepted it.
    fn try_send_bytes(&self, dest: SocketAddr, msg: &SipMessage) -> (Vec<u8>, bool) {
        let bytes = msg.to_bytes();
        let sent = match self.transport.send(dest, &bytes) {
            Ok(()) => true,
            Err(e) => {
                self.log("warn", format!("send to {dest} failed: {e}"));
                false
            }
        };
        if self.cfg.trace_sip {
            self.emit(Event::SipTrace { direction: "out", remote: dest.to_string(), message: String::from_utf8_lossy(&bytes).into_owned() });
        }
        (bytes, sent)
    }

    fn base_request(&self, method: Method, uri: &str, from: &str, to: &str, call_id: &str, cseq: u32, routes: &[String]) -> SipMessage {
        let mut m = SipMessage::request(method.clone(), uri);
        m.add_header("Via", self.via(&new_branch()))
            .add_header("Max-Forwards", "70")
            .add_header("From", from)
            .add_header("To", to)
            .add_header("Call-ID", call_id)
            .add_header("CSeq", format!("{cseq} {method}"));
        for r in routes {
            m.add_header("Route", r.clone());
        }
        if matches!(method, Method::Invite | Method::Register | Method::Refer | Method::Subscribe | Method::Update | Method::Notify) {
            m.add_header("Contact", self.contact_uri());
        }
        if matches!(method, Method::Invite | Method::Options) {
            m.add_header("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, REFER, NOTIFY, INFO, MESSAGE, UPDATE");
            m.add_header("Supported", "replaces, trickle-ice");
        }
        m.add_header("User-Agent", self.cfg.user_agent.clone());
        m
    }

    fn send_request(&self, st: &mut State, msg: SipMessage, dest: SocketAddr, purpose: Purpose) {
        let (bytes, sent) = self.try_send_bytes(dest, &msg);
        // UDP send errors are transient (ICMP noise); stream transports fail for good.
        let transport_failed = !sent && self.transport.is_reliable();
        // CANCEL shares the INVITE's branch (RFC 3261 §9.1), so transactions are keyed by branch + method.
        let Some(key) = msg.transaction_key() else { return };
        let now = Instant::now();
        st.client_txs.insert(
            key,
            ClientTx {
                request: msg,
                bytes,
                dest,
                purpose,
                started: now,
                next_retransmit: now + T1,
                interval: T1,
                provisional: false,
                completed_at: None,
                auth_attempts: 0,
                transport_failed,
            },
        );
    }

    fn response_for(&self, req: &SipMessage, code: u16, to_tag: Option<&str>) -> SipMessage {
        let mut r = SipMessage::response(code, None);
        for via in req.headers_named("Via") {
            r.add_header("Via", via);
        }
        if let Some(f) = req.header("From") {
            r.add_header("From", f);
        }
        if let Some(t) = req.header("To") {
            let has_tag = NameAddr::parse(t).is_some_and(|n| n.tag().is_some());
            match (has_tag, to_tag) {
                (false, Some(tag)) if code != 100 => r.add_header("To", format!("{t};tag={tag}")),
                _ => r.add_header("To", t),
            };
        }
        if let Some(c) = req.call_id() {
            r.add_header("Call-ID", c);
        }
        if let Some(c) = req.header("CSeq") {
            r.add_header("CSeq", c);
        }
        for rr in req.headers_named("Record-Route") {
            if (180..300).contains(&code) {
                r.add_header("Record-Route", rr);
            }
        }
        r.add_header("Server", self.cfg.user_agent.clone());
        r
    }

    fn respond(&self, st: &mut State, req: &SipMessage, from: SocketAddr, resp: SipMessage) {
        let bytes = self.send_bytes(from, &resp);
        if let Some(branch) = req.via_branch() {
            let key = format!("{branch}|{}", req.method().map(Method::as_str).unwrap_or(""));
            let invite_final = (req.method() == Some(&Method::Invite) && resp.status().is_some_and(|c| c >= 300))
                .then(|| (Instant::now() + T1, T1));
            st.server_txs.insert(key, ServerTx { last_response: bytes, dest: from, created: Instant::now(), invite_final, acked: false });
        }
    }

    // ---- Registration -----------------------------------------------------------------------

    fn send_register(&self, unregister: bool) -> Result<()> {
        if self.cfg.domain.is_empty() {
            return Err(EndpointError::InvalidArgument("domain is required to register".into()));
        }
        let registrar = self.cfg.registrar.clone().unwrap_or_else(|| self.cfg.domain.clone());
        let dest = self.proxy_addr().or_else(|| self.resolve_host(&registrar)).ok_or_else(|| {
            EndpointError::InvalidArgument(format!("cannot resolve registrar {registrar}"))
        })?;
        let mut st = self.state.lock();
        if st.registration.call_id.is_empty() {
            st.registration.call_id = format!("{}@voipnet", random_token(20));
            st.registration.local_tag = random_token(10);
        }
        st.registration.cseq += 1;
        st.registration.unregistering = unregister;
        let aor = self.local_aor();
        let from = self.local_name_addr(&st.registration.local_tag.clone());
        let mut req = self.base_request(
            Method::Register,
            &format!("sip:{}", self.cfg.domain),
            &from,
            &format!("<{aor}>"),
            &st.registration.call_id.clone(),
            st.registration.cseq,
            &[],
        );
        let expires = if unregister { 0 } else { self.cfg.register_expires.max(60) };
        req.set_header("Expires", expires.to_string());
        self.maybe_preauthorize(&mut st, &mut req);
        self.send_request(&mut st, req, dest, Purpose::Register);
        Ok(())
    }

    /// Reuses the last challenge to avoid a 401 round trip on refreshes.
    fn maybe_preauthorize(&self, st: &mut State, req: &mut SipMessage) {
        if let Some((proxy, challenge)) = st.last_challenge.clone() {
            st.nonce_count += 1;
            let uri = req.request_uri().unwrap_or_default().to_owned();
            let method = req.method().map(|m| m.as_str().to_owned()).unwrap_or_default();
            let user = self.cfg.auth_username.clone().unwrap_or_else(|| self.cfg.username.clone());
            let header = challenge.authorize(&method, &uri, &user, &self.cfg.password, st.nonce_count);
            req.set_header(if proxy { "Proxy-Authorization" } else { "Authorization" }, header);
        }
    }

    // ---- Calls ------------------------------------------------------------------------------

    fn new_media(&self, call_id: u64) -> Result<Arc<MediaSession>> {
        let media = MediaSession::new(call_id, self.media_cfg.clone(), self.sink.clone())?;
        media.set_dtmf_mode(match self.cfg.dtmf_mode {
            DtmfModeConfig::Rfc4733 | DtmfModeConfig::SipInfo => DtmfMode::Rfc4733,
            DtmfModeConfig::InBand => DtmfMode::InBand,
        });
        Ok(media)
    }

    fn make_call(&self, target: &str, refer_origin: Option<u64>, replaces: Option<String>) -> Result<u64> {
        let uri = self.normalize_target(target)?;
        let dest = self
            .destination_for(&uri, &[])
            .ok_or_else(|| EndpointError::InvalidArgument(format!("cannot resolve {}", uri.host)))?;
        let id = self.ids.fetch_add(1, Ordering::Relaxed);
        let media = self.new_media(id)?;
        match (self.cfg.srtp, self.cfg.srtp_keying) {
            (SrtpMode::Disabled, _) => {}
            (_, SrtpKeying::Dtls) => {
                media.enable_dtls();
            }
            (_, SrtpKeying::Sdes) => {
                media.enable_srtp();
            }
        }

        let mut st = self.state.lock();
        let local_tag = random_token(10);
        let sip_call_id = format!("{}@{}", random_token(24), self.contact.lock().ip());
        let mut call = Call {
            id,
            sip_call_id: sip_call_id.clone(),
            outgoing: true,
            state: CallState::Calling,
            local_tag: local_tag.clone(),
            remote_tag: None,
            local_uri: self.local_name_addr(&local_tag),
            remote_uri: format!("<{uri}>"),
            remote_target: uri.to_string(),
            route_set: Vec::new(),
            local_cseq: 1,
            remote_cseq: 0,
            invite: None,
            invite_branch: String::new(),
            peer: dest,
            media: Some(media),
            video: None,
            video_formats: Vec::new(),
            remote_offer: None,
            session_id: rand::random::<u32>() as u64,
            session_version: 1,
            local_hold: false,
            remote_hold: false,
            pending_cancel: false,
            got_provisional: false,
            created: Instant::now(),
            connected_at: None,
            pending_2xx: None,
            last_ack: None,
            refer_origin,
            replaces: None,
            codec_name: None,
            final_stats: None,
            redirects: 0,
        };
        self.offer_video(&mut call);
        let sdp = self.local_offer(&call, Direction::SendRecv);
        let mut req = self.base_request(Method::Invite, &uri.to_string(), &call.local_uri, &call.remote_uri, &sip_call_id, 1, &[]);
        if let Some(r) = replaces {
            req.add_header("Replaces", r);
        }
        if let Some(origin) = refer_origin.and_then(|o| st.calls.get(&o)) {
            req.add_header("Referred-By", format!("<{}>", NameAddr::parse(&origin.remote_uri).map(|n| n.uri.aor()).unwrap_or_default()));
        }
        req.set_body("application/sdp", sdp.into_bytes());
        call.invite_branch = req.via_branch().unwrap_or_default();
        call.invite = Some(req.clone());
        st.by_sip_id.insert(sip_call_id, id);
        st.calls.insert(id, call);
        self.maybe_preauthorize_proxy_only(&mut st, &mut req);
        self.send_request(&mut st, req, dest, Purpose::Invite(id));
        drop(st);
        self.emit(Event::CallState { call_id: id, state: "calling", code: 0, reason: String::new() });
        Ok(id)
    }

    fn maybe_preauthorize_proxy_only(&self, _st: &mut State, _req: &mut SipMessage) {
        // INVITE challenges are answered reactively; nonce reuse across dialogs is frequently rejected.
    }

    fn local_offer(&self, call: &Call, direction: Direction) -> String {
        let media = call.media.as_ref().expect("offer requires media");
        let mut formats: Vec<RtpMap> = self.supported_audio.clone();
        // Make sure static payload types never collide with the dynamic telephone-event PT.
        formats.dedup_by_key(|f| f.payload_type);
        self.build_sdp(call, media, formats, direction, None)
    }

    fn build_sdp(&self, call: &Call, media: &Arc<MediaSession>, formats: Vec<RtpMap>, direction: Direction, answer_to: Option<&SessionDescription>) -> String {
        let addr = media.advertised_address();
        let mut lines = vec![self.media_line("audio", media, formats, direction, answer_to, answer_to.and_then(|o| o.audio()), "0")];
        // The video stream has its own port, so it gets its own m-line rather than a BUNDLE group.
        if let Some(video) = call.video.as_ref().filter(|_| !call.video_formats.is_empty()) {
            lines.push(self.media_line(
                "video",
                video,
                call.video_formats.clone(),
                direction,
                answer_to,
                answer_to.and_then(|o| o.video()),
                "1",
            ));
        }
        let mut sdp = SessionDescription {
            origin_user: "voipnet".into(),
            session_id: call.session_id,
            session_version: call.session_version,
            origin_address: addr.ip().to_string(),
            session_name: "Voip.NET".into(),
            connection: Some(addr.ip().to_string()),
            media: lines,
            ..Default::default()
        };
        // RFC 3264 6: an answer keeps the offer's m-lines in order; streams we did not accept get port 0.
        if let Some(offer) = answer_to {
            let mut ours: Vec<Option<MediaDescription>> = sdp.media.drain(..).map(Some).collect();
            for om in &offer.media {
                let mine = ours.iter_mut().find(|l| l.as_ref().is_some_and(|l| l.media == om.media)).and_then(Option::take);
                sdp.media.push(mine.unwrap_or_else(|| MediaDescription {
                    media: om.media.clone(),
                    port: 0,
                    protocol: om.protocol.clone(),
                    formats: om.formats.iter().take(1).cloned().collect(),
                    direction: Direction::Inactive,
                    mid: om.mid.clone(),
                    ..Default::default()
                }));
            }
            // Keep BUNDLE (RFC 8843) for the accepted streams so WebRTC peers accept the answer.
            if offer.groups.iter().any(|g| g.starts_with("BUNDLE")) {
                let mids: Vec<String> = sdp.media.iter().filter(|m| m.port != 0).filter_map(|m| m.mid.clone()).collect();
                if !mids.is_empty() {
                    sdp.groups.push(format!("BUNDLE {}", mids.join(" ")));
                }
            }
        }
        sdp.to_string_sdp()
    }

    /// Builds one m-line: the stream's formats, keying material and ICE candidates. `answer_to_line` is
    /// the offered line being answered, if any.
    fn media_line(
        &self,
        kind: &str,
        media: &Arc<MediaSession>,
        formats: Vec<RtpMap>,
        direction: Direction,
        answer_to: Option<&SessionDescription>,
        answer_to_line: Option<&MediaDescription>,
        default_mid: &str,
    ) -> MediaDescription {
        let addr = media.advertised_address();
        let mut m = MediaDescription {
            media: kind.into(),
            port: addr.port(),
            protocol: if self.cfg.srtp == SrtpMode::Mandatory { "RTP/SAVP".into() } else { "RTP/AVP".into() },
            formats,
            direction,
            rtcp_mux: true,
            ptime: (kind == "audio").then_some(self.cfg.ptime_ms),
            ..Default::default()
        };
        if let Some(offered) = answer_to_line {
            m.protocol = offered.protocol.clone();
            m.rtcp_mux = offered.rtcp_mux;
            m.mid = offered.mid.clone();
        }
        if media.srtp_enabled() {
            let key = media.enable_srtp();
            let tag = answer_to_line.and_then(|a| a.crypto.first()).map_or(1, |c| c.tag);
            m.crypto.push(CryptoAttr { tag, suite: SUITE_AES_CM_128_HMAC_SHA1_80.into(), key_params: key });
        }
        // DTLS-SRTP (RFC 5763): certificate fingerprint plus the connection role.
        let dtls = media.dtls_enabled();
        if let Some(fingerprint) = media.dtls_fingerprint() {
            m.fingerprint = Some(format!("sha-256 {fingerprint}"));
            m.setup = Some(
                match media.dtls_role() {
                    Some(DtlsRole::Client) => "active",
                    Some(DtlsRole::Server) => "passive",
                    None => "actpass",
                }
                .into(),
            );
            if answer_to.is_none() {
                m.protocol = "UDP/TLS/RTP/SAVP".into();
                m.mid = Some(default_mid.into());
            }
        }
        // WebRTC peers require ICE, so DTLS offers and ICE offers are answered with candidates.
        let remote_ice = answer_to_line.is_some_and(|a| a.ice_ufrag.is_some()) || answer_to.is_some_and(|o| o.ice_ufrag.is_some());
        if self.cfg.ice || dtls || remote_ice {
            let (ufrag, pwd) = media.ice_credentials();
            m.ice_ufrag = Some(ufrag);
            m.ice_pwd = Some(pwd);
            m.other_attributes.push("ice-options:trickle".into());
            m.candidates = media.local_candidates().iter().map(Candidate::to_sdp).collect();
        }
        m
    }

    /// Negotiates an incoming offer. Returns (formats for answer, negotiated media) or a SIP error code.
    fn negotiate_offer(&self, offer: &SessionDescription, media: &Arc<MediaSession>) -> std::result::Result<(Vec<RtpMap>, NegotiatedMedia), u16> {
        let audio = offer.audio().filter(|a| a.port != 0).ok_or(488u16)?;
        let secure_profile = audio.protocol.contains("SAVP");
        let crypto = audio.crypto.iter().find(|c| c.suite == SUITE_AES_CM_128_HMAC_SHA1_80);
        let fingerprint = audio.fingerprint.clone().or_else(|| offer.fingerprint.clone()).filter(|_| audio.protocol.contains("TLS"));
        match (self.cfg.srtp, secure_profile, crypto.is_some() || fingerprint.is_some()) {
            (SrtpMode::Disabled, true, _) => return Err(488),
            (SrtpMode::Mandatory, _, false) => return Err(488),
            _ => {}
        }
        let (codec, dtmf) = negotiate(&audio.formats, &self.supported_audio);
        let codec = codec.ok_or(488u16)?;
        // New ICE credentials in an offer mean the peer restarted ICE; answer with new ones too.
        let offered_ufrag = audio.ice_ufrag.clone().or_else(|| offer.ice_ufrag.clone());
        if let (Some(current), Some(offered)) = (media.remote_ice_ufrag(), offered_ufrag.as_ref()) {
            if &current != offered {
                media.restart_ice();
            }
        }
        // Prefer the configured keying when the offer carries both.
        let use_dtls = self.cfg.srtp != SrtpMode::Disabled
            && fingerprint.is_some()
            && (crypto.is_none() || self.cfg.srtp_keying == SrtpKeying::Dtls);
        let use_srtp = !use_dtls && crypto.is_some() && self.cfg.srtp != SrtpMode::Disabled;
        if use_srtp {
            media.enable_srtp();
        }
        if use_dtls {
            media.enable_dtls().ok_or(488u16)?;
            // RFC 5763 §5: the answerer takes the role the offerer left open, preferring active.
            media.set_dtls_role(match audio.setup.as_deref() {
                Some("active") => DtlsRole::Server,
                _ => DtlsRole::Client,
            });
        }
        let mut answer_formats = vec![codec.clone()];
        if let Some(d) = &dtmf {
            answer_formats.push(d.clone());
        }
        let remote_ip = offer.rtp_address(audio).and_then(|a| a.parse::<IpAddr>().ok());
        let negotiated = NegotiatedMedia {
            remote: remote_ip.filter(|ip| !ip.is_unspecified()).map(|ip| SocketAddr::new(ip, audio.port)),
            codec,
            dtmf,
            direction: audio.direction.reversed(),
            remote_srtp_key: if use_srtp { crypto.map(|c| c.key_params.clone()) } else { None },
            remote_ice_ufrag: audio.ice_ufrag.clone().or_else(|| offer.ice_ufrag.clone()),
            remote_ice_pwd: audio.ice_pwd.clone().or_else(|| offer.ice_pwd.clone()),
            ice_controlling: offer.ice_lite,
            remote_candidates: audio.candidates.iter().filter_map(|c| Candidate::parse(c)).collect(),
            ptime_ms: audio.ptime,
            remote_fingerprint: fingerprint.filter(|_| use_dtls),
            dtls_role: media.dtls_role(),
        };
        Ok((answer_formats, negotiated))
    }

    /// Processes an SDP answer to our offer.
    fn negotiate_answer(&self, answer: &SessionDescription) -> Option<NegotiatedMedia> {
        let audio = answer.audio().filter(|a| a.port != 0)?;
        let (codec, dtmf) = negotiate(&audio.formats, &self.supported_audio);
        let remote_ip: IpAddr = answer.rtp_address(audio)?.parse().ok()?;
        Some(NegotiatedMedia {
            remote: Some(SocketAddr::new(remote_ip, audio.port)).filter(|a| !a.ip().is_unspecified()),
            codec: codec?,
            dtmf,
            direction: audio.direction.reversed(),
            remote_srtp_key: audio.crypto.iter().find(|c| c.suite == SUITE_AES_CM_128_HMAC_SHA1_80).map(|c| c.key_params.clone()),
            remote_ice_ufrag: audio.ice_ufrag.clone().or_else(|| answer.ice_ufrag.clone()),
            remote_ice_pwd: audio.ice_pwd.clone().or_else(|| answer.ice_pwd.clone()),
            ice_controlling: true,
            remote_candidates: audio.candidates.iter().filter_map(|c| Candidate::parse(c)).collect(),
            ptime_ms: audio.ptime,
            remote_fingerprint: audio.fingerprint.clone().or_else(|| answer.fingerprint.clone()),
            // The answerer picked a role; we take the other one.
            dtls_role: Some(if audio.setup.as_deref() == Some("active") { DtlsRole::Server } else { DtlsRole::Client }),
        })
    }

    /// Creates the call's video session: a second RTP stream that packetizes whole frames.
    fn new_video_media(&self, call_id: u64, format: &RtpMap) -> Result<Arc<MediaSession>> {
        let media = MediaSession::new(call_id, self.media_cfg.clone(), self.sink.clone())?;
        if !media.enable_video(&format.encoding, format.payload_type) {
            media.stop();
            return Err(EndpointError::InvalidArgument(format!("unsupported video codec {}", format.encoding)));
        }
        match (self.cfg.srtp, self.cfg.srtp_keying) {
            (SrtpMode::Disabled, _) => {}
            (_, SrtpKeying::Dtls) => {
                media.enable_dtls();
            }
            (_, SrtpKeying::Sdes) => {
                media.enable_srtp();
            }
        }
        Ok(media)
    }

    /// Adds a video stream to a call we are about to offer, when video is enabled and a codec is configured.
    fn offer_video(&self, call: &mut Call) {
        if !self.cfg.video || call.video.is_some() {
            return;
        }
        let Some(first) = self.supported_video.first() else { return };
        match self.new_video_media(call.id, first) {
            Ok(session) => {
                call.video = Some(session);
                call.video_formats = self.supported_video.clone();
            }
            Err(e) => self.log("warn", format!("call {}: no video stream ({e})", call.id)),
        }
    }

    /// Negotiates the video stream of an offer we are answering. Clears `call.video_formats` when the
    /// offer has no video we can use, so the answer rejects that m-line with port 0.
    fn negotiate_video_offer(&self, offer: &SessionDescription, call: &mut Call) -> Option<NegotiatedMedia> {
        let video = offer.video().filter(|v| v.port != 0);
        let codec = video.and_then(|v| negotiate(&v.formats, &self.supported_video).0).filter(|_| self.cfg.video);
        let (Some(video), Some(codec)) = (video, codec) else {
            call.video_formats.clear();
            if let Some(session) = call.video.take() {
                session.stop();
            }
            return None;
        };
        let session = match call.video.clone() {
            Some(session) => {
                session.enable_video(&codec.encoding, codec.payload_type);
                session
            }
            None => match self.new_video_media(call.id, &codec) {
                Ok(session) => {
                    call.video = Some(session.clone());
                    session
                }
                Err(e) => {
                    self.log("warn", format!("call {}: no video stream ({e})", call.id));
                    call.video_formats.clear();
                    return None;
                }
            },
        };
        let crypto = video.crypto.iter().find(|c| c.suite == SUITE_AES_CM_128_HMAC_SHA1_80);
        let fingerprint = video.fingerprint.clone().or_else(|| offer.fingerprint.clone()).filter(|_| video.protocol.contains("TLS"));
        let use_dtls = self.cfg.srtp != SrtpMode::Disabled && fingerprint.is_some() && (crypto.is_none() || self.cfg.srtp_keying == SrtpKeying::Dtls);
        if use_dtls {
            session.enable_dtls();
            session.set_dtls_role(match video.setup.as_deref() {
                Some("active") => DtlsRole::Server,
                _ => DtlsRole::Client,
            });
        } else if crypto.is_some() && self.cfg.srtp != SrtpMode::Disabled {
            session.enable_srtp();
        }
        call.video_formats = vec![codec.clone()];
        let remote_ip = offer.rtp_address(video).and_then(|a| a.parse::<IpAddr>().ok());
        Some(NegotiatedMedia {
            remote: remote_ip.filter(|ip| !ip.is_unspecified()).map(|ip| SocketAddr::new(ip, video.port)),
            codec,
            dtmf: None,
            direction: video.direction.reversed(),
            remote_srtp_key: crypto.map(|c| c.key_params.clone()).filter(|_| !use_dtls && self.cfg.srtp != SrtpMode::Disabled),
            remote_ice_ufrag: video.ice_ufrag.clone().or_else(|| offer.ice_ufrag.clone()),
            remote_ice_pwd: video.ice_pwd.clone().or_else(|| offer.ice_pwd.clone()),
            ice_controlling: offer.ice_lite,
            remote_candidates: video.candidates.iter().filter_map(|c| Candidate::parse(c)).collect(),
            ptime_ms: None,
            remote_fingerprint: fingerprint.filter(|_| use_dtls),
            dtls_role: session.dtls_role(),
        })
    }

    /// Applies the answer to a video stream we offered, dropping the stream if the peer refused it.
    fn apply_video_answer(&self, call: &mut Call, answer: &SessionDescription) -> Vec<Event> {
        if call.video.is_none() {
            return Vec::new();
        }
        let video = answer.video().filter(|v| v.port != 0);
        let codec = video.and_then(|v| negotiate(&v.formats, &self.supported_video).0);
        let remote_ip = video.and_then(|v| answer.rtp_address(v)).and_then(|a| a.parse::<IpAddr>().ok());
        let (Some(video), Some(codec), Some(remote_ip)) = (video, codec, remote_ip) else {
            if let Some(session) = call.video.take() {
                session.stop();
            }
            call.video_formats.clear();
            return vec![Event::MediaEvent { call_id: call.id, kind: "video".into(), detail: "declined".into() }];
        };
        let session = call.video.clone().expect("checked above");
        session.enable_video(&codec.encoding, codec.payload_type);
        call.video_formats = vec![codec.clone()];
        let n = NegotiatedMedia {
            remote: Some(SocketAddr::new(remote_ip, video.port)).filter(|a| !a.ip().is_unspecified()),
            codec,
            dtmf: None,
            direction: video.direction.reversed(),
            remote_srtp_key: video.crypto.iter().find(|c| c.suite == SUITE_AES_CM_128_HMAC_SHA1_80).map(|c| c.key_params.clone()),
            remote_ice_ufrag: video.ice_ufrag.clone().or_else(|| answer.ice_ufrag.clone()),
            remote_ice_pwd: video.ice_pwd.clone().or_else(|| answer.ice_pwd.clone()),
            ice_controlling: true,
            remote_candidates: video.candidates.iter().filter_map(|c| Candidate::parse(c)).collect(),
            ptime_ms: None,
            remote_fingerprint: video.fingerprint.clone().or_else(|| answer.fingerprint.clone()),
            dtls_role: Some(if video.setup.as_deref() == Some("active") { DtlsRole::Server } else { DtlsRole::Client }),
        };
        self.apply_video(call, &n)
    }

    /// Starts a negotiated video stream and reports it.
    fn apply_video(&self, call: &mut Call, n: &NegotiatedMedia) -> Vec<Event> {
        let Some(session) = call.video.clone() else { return Vec::new() };
        if let Err(e) = session.apply(n) {
            call.video = None;
            call.video_formats.clear();
            session.stop();
            return vec![Event::Log { level: "error", message: format!("call {}: video {e}", call.id) }];
        }
        vec![Event::MediaEvent {
            call_id: call.id,
            kind: "video".into(),
            detail: format!("{} {}", n.codec.encoding, n.direction.as_str()),
        }]
    }

    fn apply_media(&self, call: &mut Call, n: &NegotiatedMedia) -> Vec<Event> {
        let mut events = Vec::new();
        let Some(media) = call.media.clone() else { return events };
        if let Err(e) = media.apply(n) {
            events.push(Event::Log { level: "error", message: format!("call {}: {e}", call.id) });
            return events;
        }
        let first = call.codec_name.is_none();
        call.codec_name = Some(n.codec.encoding.clone());
        if first {
            let stats = media.stats();
            events.push(Event::MediaStarted {
                call_id: call.id,
                codec: n.codec.encoding.clone(),
                sample_rate: stats.sample_rate.max(8000),
                remote: n.remote.map(|r| r.to_string()).unwrap_or_default(),
                srtp: n.remote_srtp_key.is_some() || n.remote_fingerprint.is_some(),
                direction: n.direction.as_str(),
            });
        }
        events
    }

    fn answer(&self, call_id: u64) -> Result<()> {
        let mut events = Vec::new();
        let replaced;
        {
            let mut st = self.state.lock();
            let st = &mut *st;
            let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
            if call.state != CallState::Incoming {
                return Err(EndpointError::InvalidState("call is not ringing"));
            }
            let invite = call.invite.clone().ok_or(EndpointError::InvalidState("missing INVITE"))?;
            let media = call.media.clone().ok_or(EndpointError::InvalidState("no media"))?;

            let body = match call.remote_offer.clone() {
                Some(offer) => match self.negotiate_offer(&offer, &media) {
                    Ok((formats, negotiated)) => {
                        let video = self.negotiate_video_offer(&offer, call);
                        let sdp = self.build_sdp(call, &media, formats, negotiated.direction, Some(&offer));
                        events.extend(self.apply_media(call, &negotiated));
                        if let Some(video) = video {
                            events.extend(self.apply_video(call, &video));
                        }
                        sdp
                    }
                    Err(code) => {
                        drop(events);
                        let resp = self.response_for(&invite, code, Some(&call.local_tag.clone()));
                        let peer = call.peer;
                        call.state = CallState::Terminated;
                        self.respond(st, &invite, peer, resp);
                        return Err(EndpointError::InvalidState("offer not acceptable"));
                    }
                },
                None => self.local_offer(call, Direction::SendRecv),
            };
            let mut resp = self.response_for(&invite, 200, Some(&call.local_tag));
            resp.add_header("Contact", self.contact_uri());
            resp.add_header("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, REFER, NOTIFY, INFO, MESSAGE, UPDATE");
            resp.add_header("Supported", "replaces, trickle-ice");
            resp.set_body("application/sdp", body.into_bytes());
            let bytes = self.send_bytes(call.peer, &resp);
            let now = Instant::now();
            call.pending_2xx = Some((bytes, now + T1, T1, now));
            call.state = CallState::Connected;
            call.connected_at = Some(now);
            replaced = call.replaces;
            events.push(Event::CallState { call_id, state: "connected", code: 200, reason: "OK".into() });
        }
        for e in events {
            self.emit(e);
        }
        if let Some(old) = replaced {
            let _ = self.hangup(old);
        }
        Ok(())
    }

    fn reject(&self, call_id: u64, code: u16) -> Result<()> {
        let media = {
            let mut st = self.state.lock();
            let st = &mut *st;
            let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
            if call.state != CallState::Incoming {
                return Err(EndpointError::InvalidState("call is not ringing"));
            }
            let invite = call.invite.clone().ok_or(EndpointError::InvalidState("missing INVITE"))?;
            let code = if (300..700).contains(&code) { code } else { 486 };
            let resp = self.response_for(&invite, code, Some(&call.local_tag));
            call.state = CallState::Terminated;
            let peer = call.peer;
            let media = call.take_media();
            self.respond(st, &invite, peer, resp);
            media
        };
        if let Some(m) = media {
            m.stop();
        }
        self.emit(Event::CallState { call_id, state: "terminated", code, reason: "rejected".into() });
        Ok(())
    }

    fn hangup(&self, call_id: u64) -> Result<()> {
        let state = {
            let st = self.state.lock();
            st.calls.get(&call_id).ok_or(EndpointError::NotFound)?.state
        };
        match state {
            CallState::Incoming => self.reject(call_id, 603),
            CallState::Calling | CallState::Ringing | CallState::EarlyMedia => {
                let mut st = self.state.lock();
                let st = &mut *st;
                let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
                call.pending_cancel = true;
                if call.got_provisional {
                    let cancel = self.build_cancel(call);
                    let dest = call.peer;
                    self.send_request(st, cancel, dest, Purpose::Cancel);
                }
                Ok(())
            }
            CallState::Connected | CallState::OnHold | CallState::RemoteHold => {
                let media = {
                    let mut st = self.state.lock();
                    let st = &mut *st;
                    let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
                    call.local_cseq += 1;
                    let bye = self.dialog_request(call, Method::Bye);
                    let dest = self.dialog_dest(call);
                    call.state = CallState::Terminated;
                    call.pending_2xx = None;
                    let media = call.take_media();
                    self.send_request(st, bye, dest, Purpose::Bye(call_id));
                    media
                };
                if let Some(m) = media {
                    m.stop();
                }
                self.emit(Event::CallState { call_id, state: "terminated", code: 0, reason: "local hangup".into() });
                Ok(())
            }
            CallState::Terminated => Ok(()),
        }
    }

    fn build_cancel(&self, call: &Call) -> SipMessage {
        let invite = call.invite.as_ref().expect("outgoing call keeps its INVITE");
        let mut c = SipMessage::request(Method::Cancel, invite.request_uri().unwrap_or_default());
        if let Some(v) = invite.header("Via") {
            c.add_header("Via", v);
        }
        c.add_header("Max-Forwards", "70");
        for h in ["From", "To", "Call-ID"] {
            if let Some(v) = invite.header(h) {
                c.add_header(h, v);
            }
        }
        let cseq = invite.cseq().map_or(1, |(n, _)| n);
        c.add_header("CSeq", format!("{cseq} CANCEL"));
        for r in invite.headers_named("Route") {
            c.add_header("Route", r);
        }
        c.add_header("User-Agent", self.cfg.user_agent.clone());
        c
    }

    fn dialog_request(&self, call: &Call, method: Method) -> SipMessage {
        let to = match &call.remote_tag {
            Some(tag) if NameAddr::parse(&call.remote_uri).is_some_and(|n| n.tag().is_none()) => format!("{};tag={tag}", call.remote_uri),
            _ => call.remote_uri.clone(),
        };
        self.base_request(method, &call.remote_target, &call.local_uri, &to, &call.sip_call_id, call.local_cseq, &call.route_set)
    }

    fn dialog_dest(&self, call: &Call) -> SocketAddr {
        if self.proxy_addr().is_some() || !call.route_set.is_empty() {
            if let Some(d) = SipUri::parse(&call.remote_target).and_then(|u| self.destination_for(&u, &call.route_set)) {
                return d;
            }
        }
        if call.outgoing {
            SipUri::parse(&call.remote_target).and_then(|u| self.destination_for(&u, &[])).unwrap_or(call.peer)
        } else {
            // Incoming calls without proxies: answer to where the INVITE came from (NAT friendly).
            call.peer
        }
    }

    fn set_hold(&self, call_id: u64, hold: bool) -> Result<()> {
        let mut st = self.state.lock();
        let st = &mut *st;
        let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
        if !call.state.is_established() {
            return Err(EndpointError::InvalidState("call is not established"));
        }
        if call.local_hold == hold {
            return Ok(());
        }
        call.local_hold = hold;
        call.session_version += 1;
        call.local_cseq += 1;
        let direction = match (hold, call.remote_hold) {
            (true, true) => Direction::Inactive,
            (true, false) => Direction::SendOnly,
            (false, true) => Direction::RecvOnly,
            (false, false) => Direction::SendRecv,
        };
        let sdp = self.local_offer(call, direction);
        let mut req = self.dialog_request(call, Method::Invite);
        req.set_body("application/sdp", sdp.into_bytes());
        let dest = self.dialog_dest(call);
        self.send_request(st, req, dest, Purpose::ReInvite(call_id));
        Ok(())
    }

    /// Restarts ICE on an established call: fresh credentials in a re-INVITE (RFC 8445 §9).
    fn restart_ice(&self, call_id: u64) -> Result<()> {
        let mut st = self.state.lock();
        let st = &mut *st;
        let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
        if !call.state.is_established() {
            return Err(EndpointError::InvalidState("call is not established"));
        }
        let media = call.media.clone().ok_or(EndpointError::InvalidState("no media"))?;
        if media.remote_ice_ufrag().is_none() {
            return Err(EndpointError::InvalidState("ICE is not in use on this call"));
        }
        media.restart_ice();
        call.session_version += 1;
        call.local_cseq += 1;
        let direction = match (call.local_hold, call.remote_hold) {
            (true, true) => Direction::Inactive,
            (true, false) => Direction::SendOnly,
            (false, true) => Direction::RecvOnly,
            (false, false) => Direction::SendRecv,
        };
        let sdp = self.local_offer(call, direction);
        let mut req = self.dialog_request(call, Method::Invite);
        req.set_body("application/sdp", sdp.into_bytes());
        let dest = self.dialog_dest(call);
        self.send_request(st, req, dest, Purpose::ReInvite(call_id));
        Ok(())
    }

    fn transfer(&self, call_id: u64, target: &str, consult: Option<u64>) -> Result<()> {
        let mut st = self.state.lock();
        let st = &mut *st;
        let refer_to = match consult {
            Some(cid) => {
                let c = st.calls.get(&cid).ok_or(EndpointError::NotFound)?;
                let remote_tag = c.remote_tag.clone().unwrap_or_default();
                let target = c.remote_target.clone();
                let replaces = format!("{};to-tag={};from-tag={}", c.sip_call_id, remote_tag, c.local_tag);
                format!("<{target}?Replaces={}>", uri_escape(&replaces))
            }
            None => format!("<{}>", self.normalize_target(target)?),
        };
        let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
        if !call.state.is_established() {
            return Err(EndpointError::InvalidState("call is not established"));
        }
        call.local_cseq += 1;
        let mut req = self.dialog_request(call, Method::Refer);
        req.add_header("Refer-To", refer_to);
        req.add_header("Referred-By", format!("<{}>", self.local_aor()));
        let dest = self.dialog_dest(call);
        self.send_request(st, req, dest, Purpose::Refer(call_id));
        Ok(())
    }

    fn send_dtmf(&self, call_id: u64, digits: &str, duration_ms: u32) -> Result<()> {
        if self.cfg.dtmf_mode != DtmfModeConfig::SipInfo {
            self.media_of(call_id)?.send_dtmf(digits, duration_ms);
            return Ok(());
        }
        let mut st = self.state.lock();
        let st = &mut *st;
        for digit in digits.chars().filter(|d| crate::codec::dtmf::digit_to_event(*d).is_some()) {
            let call = st.calls.get_mut(&call_id).ok_or(EndpointError::NotFound)?;
            if !call.state.is_established() {
                return Err(EndpointError::InvalidState("call is not established"));
            }
            call.local_cseq += 1;
            let mut req = self.dialog_request(call, Method::Info);
            req.set_body("application/dtmf-relay", format!("Signal={digit}\r\nDuration={duration_ms}\r\n").into_bytes());
            let dest = self.dialog_dest(call);
            self.send_request(st, req, dest, Purpose::Info(call_id));
        }
        Ok(())
    }

    fn media_of(&self, call_id: u64) -> Result<Arc<MediaSession>> {
        let st = self.state.lock();
        st.calls.get(&call_id).ok_or(EndpointError::NotFound)?.media.clone().ok_or(EndpointError::InvalidState("no media"))
    }

    fn out_of_dialog(&self, method: Method, target: &str, body: Option<(&str, &str)>) -> Result<u64> {
        let uri = self.normalize_target(target)?;
        let dest = self.destination_for(&uri, &[]).ok_or_else(|| EndpointError::InvalidArgument(format!("cannot resolve {}", uri.host)))?;
        let request_id = self.ids.fetch_add(1, Ordering::Relaxed);
        let mut req = self.base_request(
            method,
            &uri.to_string(),
            &self.local_name_addr(&random_token(10)),
            &format!("<{uri}>"),
            &format!("{}@voipnet", random_token(20)),
            1,
            &[],
        );
        if let Some((ct, b)) = body {
            req.set_body(ct, b.as_bytes().to_vec());
        }
        let mut st = self.state.lock();
        self.send_request(&mut st, req, dest, Purpose::OutOfDialog { request_id, started: Instant::now() });
        Ok(request_id)
    }

    fn terminate(&self, st: &mut State, call_id: u64, code: u16, reason: &str, events: &mut Vec<Event>, stop: &mut Vec<Arc<MediaSession>>) {
        if let Some(call) = st.calls.get_mut(&call_id) {
            if call.state == CallState::Terminated {
                return;
            }
            call.state = CallState::Terminated;
            call.pending_2xx = None;
            if let Some(m) = call.take_media() {
                stop.push(m);
            }
            events.push(Event::CallState { call_id, state: "terminated", code, reason: reason.into() });
            if let Some(origin) = call.refer_origin {
                let (c, r) = if (200..300).contains(&code) { (200, "OK") } else { (code.max(400), reason) };
                self.notify_refer(st, origin, c, r, true);
            }
        }
    }

    fn notify_refer(&self, st: &mut State, origin: u64, code: u16, reason: &str, terminated: bool) {
        let Some(call) = st.calls.get_mut(&origin) else { return };
        if !call.state.is_established() {
            return;
        }
        call.local_cseq += 1;
        let mut req = self.dialog_request(call, Method::Notify);
        req.add_header("Event", "refer");
        req.add_header("Subscription-State", if terminated { "terminated;reason=noresource" } else { "active;expires=60" });
        req.set_body("message/sipfrag;version=2.0", format!("SIP/2.0 {code} {reason}\r\n").into_bytes());
        let dest = self.dialog_dest(call);
        self.send_request(st, req, dest, Purpose::Notify(origin));
    }

    // ---- Inbound ----------------------------------------------------------------------------

    fn on_message(&self, msg: SipMessage, from: SocketAddr) {
        if self.cfg.trace_sip {
            self.emit(Event::SipTrace { direction: "in", remote: from.to_string(), message: msg.to_string() });
        }
        if msg.is_request() {
            self.on_request(msg, from);
        } else {
            self.on_response(msg, from);
        }
    }

    fn on_request(&self, req: SipMessage, from: SocketAddr) {
        let Some(method) = req.method().cloned() else { return };
        let mut events = Vec::new();
        let mut stop = Vec::new();
        let mut followups: Vec<Box<dyn FnOnce(&Inner)>> = Vec::new();
        {
            let mut guard = self.state.lock();
            let st = &mut *guard;

            // Retransmitted request: replay the last response.
            if method != Method::Ack {
                if let Some(branch) = req.via_branch() {
                    if let Some(tx) = st.server_txs.get(&format!("{branch}|{}", method.as_str())) {
                        let _ = self.transport.send(tx.dest, &tx.last_response.clone());
                        return;
                    }
                }
            }

            let sip_id = req.call_id().unwrap_or_default().to_owned();
            let call_id = st.by_sip_id.get(&sip_id).copied();
            let to_tag = req.to().and_then(|t| t.tag().map(str::to_owned));

            match method {
                Method::Invite if to_tag.is_none() && call_id.is_none() => {
                    drop(guard);
                    self.on_new_invite(req, from);
                    return;
                }
                Method::Invite => self.on_reinvite(st, &req, from, call_id, &mut events),
                Method::Ack => {
                    if let Some(branch) = req.via_branch() {
                        if let Some(tx) = st.server_txs.get_mut(&format!("{branch}|INVITE")) {
                            tx.acked = true;
                        }
                    }
                    if let Some(call) = call_id.and_then(|id| st.calls.get_mut(&id)) {
                        call.pending_2xx = None;
                        // Late offer: SDP answer arrives in the ACK.
                        if !req.body.is_empty() && call.codec_name.is_none() {
                            if let Some(sdp) = SessionDescription::parse(req.body_str()) {
                                if let Some(n) = self.negotiate_answer(&sdp) {
                                    events.extend(self.apply_media(call, &n));
                                }
                                let video = self.apply_video_answer(call, &sdp);
                                events.extend(video);
                            }
                        }
                    }
                }
                Method::Bye => match call_id {
                    Some(id) => {
                        let resp = self.response_for(&req, 200, None);
                        self.respond(st, &req, from, resp);
                        self.terminate(st, id, 200, "remote hangup", &mut events, &mut stop);
                    }
                    None => {
                        let resp = self.response_for(&req, 481, None);
                        self.respond(st, &req, from, resp);
                    }
                },
                Method::Cancel => {
                    let target = call_id.and_then(|id| st.calls.get(&id)).filter(|c| c.state == CallState::Incoming).map(|c| c.id);
                    let resp = self.response_for(&req, if target.is_some() { 200 } else { 481 }, None);
                    self.respond(st, &req, from, resp);
                    if let Some(id) = target {
                        if let Some(invite) = st.calls.get(&id).and_then(|c| c.invite.clone()) {
                            let tag = st.calls[&id].local_tag.clone();
                            let r = self.response_for(&invite, 487, Some(&tag));
                            self.respond(st, &invite, from, r);
                        }
                        self.terminate(st, id, 487, "cancelled", &mut events, &mut stop);
                    }
                }
                Method::Options => {
                    let mut resp = self.response_for(&req, 200, Some(&random_token(8)));
                    resp.add_header("Allow", "INVITE, ACK, CANCEL, BYE, OPTIONS, REFER, NOTIFY, INFO, MESSAGE, UPDATE");
                    resp.add_header("Accept", "application/sdp, application/dtmf-relay, application/trickle-ice-sdpfrag, message/sipfrag, text/plain");
                    resp.add_header("Supported", "replaces");
                    self.respond(st, &req, from, resp);
                }
                Method::Info => {
                    let code = if call_id.is_some() { 200 } else { 481 };
                    let resp = self.response_for(&req, code, None);
                    self.respond(st, &req, from, resp);
                    if let (Some(id), Some(ct)) = (call_id, req.content_type()) {
                        if ct.starts_with("application/dtmf-relay") || ct.starts_with("application/dtmf") {
                            if let Some(d) = parse_dtmf_info(req.body_str()) {
                                let _ = self.events.lock().send(Dispatch::Dtmf(id, d, DtmfSource::SipInfo));
                            }
                        } else if ct.starts_with("application/trickle-ice-sdpfrag") {
                            // Trickled candidates (RFC 8840).
                            let candidates: Vec<Candidate> = req
                                .body_str()
                                .lines()
                                .filter_map(|l| l.trim().strip_prefix("a=candidate:"))
                                .filter_map(Candidate::parse)
                                .collect();
                            if let Some(media) = st.calls.get(&id).and_then(|c| c.media.clone()) {
                                media.add_remote_candidates(&candidates);
                            }
                        }
                    }
                }
                Method::Refer => {
                    let Some(id) = call_id else {
                        let resp = self.response_for(&req, 481, None);
                        self.respond(st, &req, from, resp);
                        return;
                    };
                    let Some(refer_to) = req.header("Refer-To").and_then(NameAddr::parse) else {
                        let resp = self.response_for(&req, 400, None);
                        self.respond(st, &req, from, resp);
                        return;
                    };
                    let code = if self.cfg.accept_transfers { 202 } else { 603 };
                    let resp = self.response_for(&req, code, None);
                    self.respond(st, &req, from, resp);
                    let raw_target = req.header("Refer-To").unwrap_or_default().to_owned();
                    let replaces = extract_replaces(&raw_target);
                    // Keep host:port and transport parameters — `aor()` would drop them.
                    let target = refer_to.uri.to_string();
                    if self.cfg.accept_transfers {
                        self.notify_refer(st, id, 100, "Trying", false);
                        followups.push(Box::new(move |inner: &Inner| {
                            let new_call = inner.make_call(&target, Some(id), replaces).ok();
                            if new_call.is_none() {
                                let mut st = inner.state.lock();
                                inner.notify_refer(&mut st, id, 503, "Service Unavailable", true);
                            }
                            inner.emit(Event::TransferRequested { call_id: id, target, new_call_id: new_call });
                        }));
                    } else {
                        events.push(Event::TransferRequested { call_id: id, target, new_call_id: None });
                    }
                }
                Method::Notify => {
                    let resp = self.response_for(&req, if call_id.is_some() { 200 } else { 481 }, None);
                    self.respond(st, &req, from, resp);
                    if let (Some(id), true) = (call_id, req.header("Event").is_some_and(|e| e.starts_with("refer"))) {
                        let frag = req.body_str();
                        let code = frag.split_whitespace().nth(1).and_then(|c| c.parse::<u16>().ok()).unwrap_or(0);
                        let reason = frag.lines().next().unwrap_or("").splitn(3, ' ').nth(2).unwrap_or("").to_owned();
                        events.push(Event::TransferProgress { call_id: id, code, reason });
                        if (200..300).contains(&code) {
                            followups.push(Box::new(move |inner: &Inner| {
                                let _ = inner.hangup(id);
                            }));
                        }
                    }
                }
                Method::Message => {
                    let resp = self.response_for(&req, 200, Some(&random_token(8)));
                    self.respond(st, &req, from, resp);
                    events.push(Event::MessageReceived {
                        from: req.from().map(|f| f.uri.aor()).unwrap_or_default(),
                        content_type: req.content_type().unwrap_or("text/plain").to_owned(),
                        body: req.body_str().to_owned(),
                    });
                }
                Method::Update => self.on_reinvite(st, &req, from, call_id, &mut events),
                Method::Subscribe => {
                    let resp = self.response_for(&req, 489, None);
                    self.respond(st, &req, from, resp);
                }
                _ => {
                    let resp = self.response_for(&req, 501, None);
                    self.respond(st, &req, from, resp);
                }
            }
        }
        for m in stop {
            m.stop();
        }
        for e in events {
            self.emit(e);
        }
        for f in followups {
            f(self);
        }
    }

    fn on_new_invite(&self, req: SipMessage, from: SocketAddr) {
        let trying = self.response_for(&req, 100, None);
        self.send_bytes(from, &trying);

        let offer = if req.body.is_empty() { None } else { SessionDescription::parse(req.body_str()) };
        let id = self.ids.fetch_add(1, Ordering::Relaxed);
        let media = match self.new_media(id) {
            Ok(m) => m,
            Err(e) => {
                self.log("error", format!("cannot allocate media: {e}"));
                let resp = self.response_for(&req, 503, None);
                let mut st = self.state.lock();
                self.respond(&mut st, &req, from, resp);
                return;
            }
        };
        if let Some(o) = &offer {
            if let Err(code) = self.negotiate_offer(o, &media) {
                let resp = self.response_for(&req, code, Some(&random_token(8)));
                let mut st = self.state.lock();
                self.respond(&mut st, &req, from, resp);
                return;
            }
        }

        let from_na = req.from();
        let local_tag = random_token(10);
        let sip_call_id = req.call_id().unwrap_or_default().to_owned();
        let replaces = req.header("Replaces").and_then(|r| {
            let id = r.split(';').next()?.trim().to_owned();
            Some(id)
        });

        let mut st = self.state.lock();
        let replaces_call = replaces.and_then(|sid| st.by_sip_id.get(&sid).copied());
        let call = Call {
            id,
            sip_call_id: sip_call_id.clone(),
            outgoing: false,
            state: CallState::Incoming,
            local_tag: local_tag.clone(),
            remote_tag: from_na.as_ref().and_then(|f| f.tag().map(str::to_owned)),
            local_uri: format!("{};tag={local_tag}", req.header("To").unwrap_or_default()),
            remote_uri: req.header("From").unwrap_or_default().to_owned(),
            remote_target: req.contact().map(|c| c.uri.to_string()).unwrap_or_else(|| from_na.as_ref().map(|f| f.uri.to_string()).unwrap_or_default()),
            route_set: req.record_routes(),
            local_cseq: rand::random::<u16>() as u32,
            remote_cseq: req.cseq().map_or(0, |(n, _)| n),
            invite: Some(req.clone()),
            invite_branch: req.via_branch().unwrap_or_default(),
            peer: from,
            media: Some(media),
            video: None,
            video_formats: Vec::new(),
            remote_offer: offer.clone(),
            session_id: rand::random::<u32>() as u64,
            session_version: 1,
            local_hold: false,
            remote_hold: false,
            pending_cancel: false,
            got_provisional: false,
            created: Instant::now(),
            connected_at: None,
            pending_2xx: None,
            last_ack: None,
            refer_origin: None,
            replaces: replaces_call,
            codec_name: None,
            final_stats: None,
            redirects: 0,
        };
        st.by_sip_id.insert(sip_call_id.clone(), id);
        st.calls.insert(id, call);
        if self.cfg.auto_ringing && replaces_call.is_none() {
            let mut ringing = self.response_for(&req, 180, Some(&local_tag));
            ringing.add_header("Contact", self.contact_uri());
            self.respond(&mut st, &req, from, ringing);
        }
        drop(st);

        self.emit(Event::IncomingCall {
            call_id: id,
            from: from_na.as_ref().map(|f| f.uri.aor()).unwrap_or_default(),
            from_display: from_na.and_then(|f| f.display),
            to: req.to().map(|t| t.uri.aor()).unwrap_or_default(),
            sip_call_id,
            has_video: offer.as_ref().is_some_and(|o| o.video().is_some_and(|v| v.port != 0)),
            replaces_call_id: replaces_call,
        });
        self.emit(Event::CallState { call_id: id, state: "incoming", code: 0, reason: String::new() });
        if replaces_call.is_some() {
            let _ = self.answer(id);
        }
    }

    fn on_reinvite(&self, st: &mut State, req: &SipMessage, from: SocketAddr, call_id: Option<u64>, events: &mut Vec<Event>) {
        let Some(id) = call_id else {
            let resp = self.response_for(req, 481, None);
            self.respond(st, req, from, resp);
            return;
        };
        let Some(call) = st.calls.get_mut(&id) else { return };
        if let Some(contact) = req.contact() {
            call.remote_target = contact.uri.to_string();
        }
        call.remote_cseq = req.cseq().map_or(call.remote_cseq, |(n, _)| n);
        let media = call.media.clone();
        let mut resp = self.response_for(req, 200, Some(&call.local_tag.clone()));
        resp.add_header("Contact", self.contact_uri());

        if let (Some(media), Some(offer)) = (media, SessionDescription::parse(req.body_str())) {
            match self.negotiate_offer(&offer, &media) {
                Ok((formats, mut negotiated)) => {
                    let offered_dir = offer.audio().map(|a| a.direction).unwrap_or_default();
                    let remote_hold = matches!(offered_dir, Direction::SendOnly | Direction::Inactive)
                        || offer.rtp_address(offer.audio().expect("negotiated")).is_some_and(|a| a == "0.0.0.0");
                    // Our direction combines remote hold with any local hold.
                    negotiated.direction = match (call.local_hold, remote_hold) {
                        (true, true) => Direction::Inactive,
                        (true, false) => Direction::SendOnly,
                        (false, true) => Direction::RecvOnly,
                        (false, false) => Direction::SendRecv,
                    };
                    call.session_version += 1;
                    let video = self.negotiate_video_offer(&offer, call);
                    let sdp = self.build_sdp(call, &media, formats, negotiated.direction, Some(&offer));
                    events.extend(self.apply_media(call, &negotiated));
                    if let Some(video) = video {
                        events.extend(self.apply_video(call, &video));
                    }
                    resp.set_body("application/sdp", sdp.into_bytes());
                    if remote_hold != call.remote_hold {
                        call.remote_hold = remote_hold;
                        call.state = if remote_hold { CallState::RemoteHold } else if call.local_hold { CallState::OnHold } else { CallState::Connected };
                        events.push(Event::CallState { call_id: id, state: call.state.as_str(), code: 0, reason: if remote_hold { "held by remote".into() } else { "resumed by remote".into() } });
                    }
                }
                Err(code) => {
                    let r = self.response_for(req, code, Some(&call.local_tag.clone()));
                    self.respond(st, req, from, r);
                    return;
                }
            }
        } else if req.method() == Some(&Method::Invite) {
            // Offer-less re-INVITE: send our current offer; answer arrives in ACK.
            if let Some(media) = call.media.clone() {
                let dir = media.direction();
                let sdp = self.local_offer(call, dir);
                resp.set_body("application/sdp", sdp.into_bytes());
            }
        }
        if req.method() == Some(&Method::Invite) {
            let bytes = self.send_bytes(from, &resp);
            let now = Instant::now();
            call.pending_2xx = Some((bytes, now + T1, T1, now));
        } else {
            self.respond(st, req, from, resp);
        }
    }

    fn on_response(&self, resp: SipMessage, from: SocketAddr) {
        let Some(code) = resp.status() else { return };
        let Some(branch) = resp.transaction_key() else { return };
        let mut events = Vec::new();
        let mut stop = Vec::new();
        let mut followups: Vec<Box<dyn FnOnce(&Inner)>> = Vec::new();
        {
            let mut guard = self.state.lock();
            let st = &mut *guard;

            // 2xx retransmission for an INVITE whose transaction already completed: re-ACK.
            let Some(tx) = st.client_txs.get_mut(&branch) else {
                if (200..300).contains(&code) && resp.cseq().is_some_and(|(_, m)| m == Method::Invite) {
                    if let Some(call) = resp.call_id().and_then(|c| st.by_sip_id.get(c)).and_then(|id| st.calls.get(id)) {
                        if let Some((ack, dest)) = &call.last_ack {
                            let _ = self.transport.send(*dest, ack);
                        }
                    }
                }
                return;
            };
            if tx.completed_at.is_some() && code >= 200 {
                if let (Purpose::Invite(id) | Purpose::ReInvite(id), true) = (tx.purpose.clone(), (200..300).contains(&code)) {
                    if let Some((ack, dest)) = st.calls.get(&id).and_then(|c| c.last_ack.clone()) {
                        let _ = self.transport.send(dest, &ack);
                    }
                }
                return;
            }
            if code < 200 {
                tx.provisional = true;
            } else {
                tx.completed_at = Some(Instant::now());
            }
            let purpose = tx.purpose.clone();
            let request = tx.request.clone();
            let dest = tx.dest;
            let auth_attempts = tx.auth_attempts;

            // Authentication challenges.
            if code == 401 || code == 407 {
                let header = if code == 401 { "WWW-Authenticate" } else { "Proxy-Authenticate" };
                let challenge = resp.header(header).and_then(DigestChallenge::parse);
                let retry_allowed = auth_attempts < 2 && challenge.as_ref().is_some_and(|c| auth_attempts == 0 || c.stale);
                if let (Some(ch), true, false) = (challenge, retry_allowed, self.cfg.password.is_empty()) {
                    if matches!(purpose, Purpose::Invite(_) | Purpose::ReInvite(_)) {
                        let ack = self.non2xx_ack(&request, &resp);
                        self.send_bytes(dest, &ack);
                    }
                    st.last_challenge = Some((code == 407, ch.clone()));
                    st.nonce_count += 1;
                    let mut retry = request.clone();
                    let new_cseq = retry.cseq().map_or(1, |(n, _)| n + 1);
                    let method = retry.method().cloned().unwrap_or(Method::Options);
                    retry.set_header("CSeq", format!("{new_cseq} {method}"));
                    retry.set_header("Via", self.via(&new_branch()));
                    let user = self.cfg.auth_username.clone().unwrap_or_else(|| self.cfg.username.clone());
                    let auth = ch.authorize(method.as_str(), retry.request_uri().unwrap_or_default(), &user, &self.cfg.password, st.nonce_count);
                    retry.remove_header("Authorization");
                    retry.remove_header("Proxy-Authorization");
                    retry.add_header(if code == 401 { "Authorization" } else { "Proxy-Authorization" }, auth);
                    match &purpose {
                        Purpose::Register => st.registration.cseq = new_cseq,
                        Purpose::Invite(id) | Purpose::ReInvite(id) | Purpose::Bye(id) | Purpose::Refer(id) | Purpose::Info(id) | Purpose::Notify(id) => {
                            if let Some(c) = st.calls.get_mut(id) {
                                c.local_cseq = new_cseq;
                                if matches!(purpose, Purpose::Invite(_)) {
                                    c.invite_branch = retry.via_branch().unwrap_or_default();
                                    c.invite = Some(retry.clone());
                                }
                            }
                        }
                        _ => {}
                    }
                    let new_branch_key = retry.transaction_key().unwrap_or_default();
                    self.send_request(st, retry, dest, purpose.clone());
                    if let Some(t) = st.client_txs.get_mut(&new_branch_key) {
                        t.auth_attempts = auth_attempts + 1;
                    }
                    return;
                }
            }

            match purpose {
                Purpose::Register => self.on_register_response(st, &resp, code, from, &mut events),
                Purpose::Invite(id) => self.on_invite_response(st, id, &request, &resp, code, dest, &mut events, &mut stop, &mut followups),
                Purpose::ReInvite(id) => {
                    if code >= 200 {
                        if let Some(call) = st.calls.get_mut(&id) {
                            if (200..300).contains(&code) {
                                let ack = self.ack_for_2xx(call, &resp);
                                let d = self.dialog_dest(call);
                                let bytes = self.send_bytes(d, &ack);
                                call.last_ack = Some((bytes, d));
                                if let Some(sdp) = SessionDescription::parse(resp.body_str()) {
                                    if let Some(n) = self.negotiate_answer(&sdp) {
                                        events.extend(self.apply_media(call, &n));
                                    }
                                    let video = self.apply_video_answer(call, &sdp);
                                    events.extend(video);
                                }
                                let new_state = if call.local_hold { CallState::OnHold } else if call.remote_hold { CallState::RemoteHold } else { CallState::Connected };
                                if new_state != call.state {
                                    call.state = new_state;
                                    events.push(Event::CallState { call_id: id, state: new_state.as_str(), code, reason: resp.reason().unwrap_or("").into() });
                                }
                            } else {
                                let ack = self.non2xx_ack(&request, &resp);
                                self.send_bytes(dest, &ack);
                                call.local_hold = !call.local_hold; // revert
                                events.push(Event::Log { level: "warn", message: format!("re-INVITE for call {id} failed with {code}") });
                                if code == 481 || code == 408 {
                                    self.terminate(st, id, code, "dialog lost", &mut events, &mut stop);
                                }
                            }
                        }
                    }
                }
                Purpose::Bye(_) | Purpose::Cancel | Purpose::Info(_) | Purpose::Notify(_) => {}
                Purpose::Refer(id) => {
                    if code >= 200 {
                        let reason = resp.reason().unwrap_or("").to_owned();
                        events.push(Event::TransferProgress { call_id: id, code, reason });
                    }
                }
                Purpose::OutOfDialog { request_id, started } => {
                    if code >= 200 {
                        events.push(Event::RequestResult {
                            request_id,
                            method: request.method().map(|m| m.as_str().to_owned()).unwrap_or_default(),
                            code,
                            reason: resp.reason().unwrap_or("").into(),
                            latency_ms: started.elapsed().as_millis() as u64,
                            user_agent: resp.header("User-Agent").or_else(|| resp.header("Server")).map(str::to_owned),
                        });
                    }
                }
            }
        }
        for m in stop {
            m.stop();
        }
        for e in events {
            self.emit(e);
        }
        for f in followups {
            f(self);
        }
    }

    fn on_register_response(&self, st: &mut State, resp: &SipMessage, code: u16, _from: SocketAddr, events: &mut Vec<Event>) {
        if code < 200 {
            return;
        }
        let reg = &mut st.registration;
        if (200..300).contains(&code) {
            if reg.unregistering {
                reg.registered = false;
                reg.refresh_at = None;
                events.push(Event::RegistrationChanged { state: "unregistered", code, reason: resp.reason().unwrap_or("").into(), expires: 0 });
                return;
            }
            let my_contact = self.contact_uri();
            let expires = resp
                .headers_named("Contact")
                .flat_map(split_list)
                .filter_map(NameAddr::parse)
                .find(|c| my_contact.contains(&c.uri.host) && c.uri.port == Some(self.contact.lock().port()))
                .and_then(|c| c.param("expires").and_then(|e| e.parse().ok()))
                .or_else(|| resp.expires())
                .unwrap_or(self.cfg.register_expires);
            reg.registered = true;
            reg.expires = expires;
            reg.refresh_at = Some(Instant::now() + Duration::from_secs((expires as u64 * 85 / 100).max(10)));

            // NAT discovery via rport/received: re-register with the public contact once.
            if let Some(via) = resp.header("Via").and_then(|v| split_list(v).next().map(str::to_owned)) {
                let params = super::uri::parse_params(via.split_once(';').map_or("", |(_, p)| p));
                let received = params.iter().find(|(k, _)| k == "received").and_then(|(_, v)| v.as_deref()?.parse::<IpAddr>().ok());
                let rport = params.iter().find(|(k, _)| k == "rport").and_then(|(_, v)| v.as_deref()?.parse::<u16>().ok());
                if self.cfg.public_address.is_none() && self.cfg.transport == TransportKind::Udp {
                    if let (Some(ip), Some(port)) = (received, rport) {
                        let public = SocketAddr::new(ip, port);
                        let mut contact = self.contact.lock();
                        if *contact != public && reg.learned_public.is_none() {
                            reg.learned_public = Some(public);
                            *contact = public;
                            reg.refresh_at = Some(Instant::now());
                            events.push(Event::Log { level: "info", message: format!("NAT detected, public contact {public}") });
                        }
                    }
                }
            }
            events.push(Event::RegistrationChanged { state: "registered", code, reason: resp.reason().unwrap_or("").into(), expires });
        } else {
            reg.registered = false;
            reg.refresh_at = Some(Instant::now() + Duration::from_secs(60));
            events.push(Event::RegistrationChanged { state: "failed", code, reason: resp.reason().unwrap_or("").into(), expires: 0 });
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn on_invite_response(
        &self,
        st: &mut State,
        id: u64,
        request: &SipMessage,
        resp: &SipMessage,
        code: u16,
        dest: SocketAddr,
        events: &mut Vec<Event>,
        stop: &mut Vec<Arc<MediaSession>>,
        followups: &mut Vec<Box<dyn FnOnce(&Inner)>>,
    ) {
        let Some(call) = st.calls.get_mut(&id) else { return };
        if let Some(tag) = resp.to().and_then(|t| t.tag().map(str::to_owned)) {
            if code > 100 {
                call.remote_tag = Some(tag);
            }
        }
        match code {
            100 => call.got_provisional = true,
            101..=199 => {
                call.got_provisional = true;
                if call.pending_cancel {
                    let cancel = self.build_cancel(call);
                    let d = call.peer;
                    self.send_request(st, cancel, d, Purpose::Cancel);
                    return;
                }
                let early_sdp = (!resp.body.is_empty()).then(|| SessionDescription::parse(resp.body_str())).flatten();
                let new_state = if early_sdp.is_some() { CallState::EarlyMedia } else { CallState::Ringing };
                if let Some(n) = early_sdp.and_then(|s| self.negotiate_answer(&s)) {
                    events.extend(self.apply_media(call, &n));
                }
                if call.state != new_state {
                    call.state = new_state;
                    events.push(Event::CallState { call_id: id, state: new_state.as_str(), code, reason: resp.reason().unwrap_or("").into() });
                }
            }
            200..=299 => {
                call.route_set = resp.record_routes().into_iter().rev().collect();
                if let Some(c) = resp.contact() {
                    call.remote_target = c.uri.to_string();
                }
                if let Some(to) = resp.header("To") {
                    call.remote_uri = to.to_owned();
                }
                let ack = self.ack_for_2xx(call, resp);
                let ack_dest = self.dialog_dest(call);
                let bytes = self.send_bytes(ack_dest, &ack);
                call.last_ack = Some((bytes, ack_dest));

                if call.pending_cancel {
                    call.local_cseq += 1;
                    let bye = self.dialog_request(call, Method::Bye);
                    let d = self.dialog_dest(call);
                    self.send_request(st, bye, d, Purpose::Bye(id));
                    self.terminate(st, id, 487, "cancelled", events, stop);
                    return;
                }
                let answer = SessionDescription::parse(resp.body_str());
                match answer.as_ref().and_then(|s| self.negotiate_answer(s)) {
                    Some(n) => events.extend(self.apply_media(call, &n)),
                    None if call.codec_name.is_none() => {
                        events.push(Event::Log { level: "warn", message: format!("call {id}: 2xx without usable SDP answer") });
                    }
                    None => {}
                }
                if let Some(answer) = answer.as_ref() {
                    let video = self.apply_video_answer(call, answer);
                    events.extend(video);
                }
                call.state = CallState::Connected;
                call.connected_at = Some(Instant::now());
                events.push(Event::CallState { call_id: id, state: "connected", code, reason: resp.reason().unwrap_or("OK").into() });
                if let Some(origin) = call.refer_origin {
                    self.notify_refer(st, origin, 200, "OK", true);
                    followups.push(Box::new(move |inner: &Inner| {
                        let _ = inner.hangup(origin);
                    }));
                }
            }
            300..=399 if call.redirects < 3 => {
                let ack = self.non2xx_ack(request, resp);
                self.send_bytes(dest, &ack);
                let target = resp.contact().map(|c| c.uri.to_string());
                call.redirects += 1;
                self.terminate(st, id, code, "redirected", events, stop);
                if let Some(t) = target {
                    followups.push(Box::new(move |inner: &Inner| {
                        let _ = inner.make_call(&t, None, None);
                    }));
                }
            }
            _ => {
                let ack = self.non2xx_ack(request, resp);
                self.send_bytes(dest, &ack);
                let reason = resp.reason().unwrap_or("").to_owned();
                self.terminate(st, id, code, &reason, events, stop);
            }
        }
    }

    fn ack_for_2xx(&self, call: &Call, resp: &SipMessage) -> SipMessage {
        let cseq = resp.cseq().map_or(call.local_cseq, |(n, _)| n);
        let to = resp.header("To").unwrap_or(&call.remote_uri).to_owned();
        let mut ack = self.base_request(Method::Ack, &call.remote_target, &call.local_uri, &to, &call.sip_call_id, cseq, &call.route_set);
        if let Some(inv) = &call.invite {
            for h in ["Authorization", "Proxy-Authorization"] {
                if let Some(v) = inv.header(h) {
                    ack.add_header(h, v);
                }
            }
        }
        ack
    }

    fn non2xx_ack(&self, request: &SipMessage, resp: &SipMessage) -> SipMessage {
        let mut ack = SipMessage::request(Method::Ack, request.request_uri().unwrap_or_default());
        if let Some(v) = request.header("Via") {
            ack.add_header("Via", v);
        }
        ack.add_header("Max-Forwards", "70");
        if let Some(v) = request.header("From") {
            ack.add_header("From", v);
        }
        if let Some(v) = resp.header("To") {
            ack.add_header("To", v);
        }
        if let Some(v) = request.call_id() {
            ack.add_header("Call-ID", v);
        }
        let n = request.cseq().map_or(1, |(n, _)| n);
        ack.add_header("CSeq", format!("{n} ACK"));
        for r in request.headers_named("Route") {
            ack.add_header("Route", r);
        }
        ack
    }

    // ---- Timers -----------------------------------------------------------------------------

    fn on_timer(&self) {
        let now = Instant::now();
        let mut events = Vec::new();
        let mut stop = Vec::new();
        let mut refresh_register = false;
        {
            let mut guard = self.state.lock();
            let st = &mut *guard;
            let reliable = self.transport.is_reliable();

            let mut timed_out = Vec::new();
            st.client_txs.retain(|_, tx| {
                if let Some(done) = tx.completed_at {
                    return now.duration_since(done) < TX_TIMEOUT;
                }
                if tx.transport_failed {
                    timed_out.push((503, "transport error", tx.purpose.clone()));
                    return false;
                }
                if now.duration_since(tx.started) >= TX_TIMEOUT {
                    timed_out.push((408, "request timeout", tx.purpose.clone()));
                    return false;
                }
                let is_invite = tx.request.method() == Some(&Method::Invite);
                if !reliable && now >= tx.next_retransmit && !(is_invite && tx.provisional) {
                    let _ = self.transport.send(tx.dest, &tx.bytes);
                    tx.interval = if is_invite { tx.interval * 2 } else { (tx.interval * 2).min(T2) };
                    tx.next_retransmit = now + tx.interval;
                }
                true
            });
            for (code, reason, purpose) in timed_out {
                let title = if code == 408 { "Request Timeout" } else { "Service Unavailable" };
                match purpose {
                    Purpose::Invite(id) => self.terminate(st, id, code, reason, &mut events, &mut stop),
                    Purpose::Register => {
                        st.registration.registered = false;
                        st.registration.refresh_at = Some(now + Duration::from_secs(30));
                        events.push(Event::RegistrationChanged { state: "failed", code, reason: title.into(), expires: 0 });
                    }
                    Purpose::ReInvite(id) | Purpose::Bye(id) => {
                        if matches!(purpose, Purpose::ReInvite(_)) {
                            self.terminate(st, id, code, reason, &mut events, &mut stop);
                        }
                    }
                    Purpose::OutOfDialog { request_id, started } => events.push(Event::RequestResult {
                        request_id,
                        method: "?".into(),
                        code,
                        reason: title.into(),
                        latency_ms: started.elapsed().as_millis() as u64,
                        user_agent: None,
                    }),
                    Purpose::Refer(id) => events.push(Event::TransferProgress { call_id: id, code, reason: title.into() }),
                    _ => {}
                }
            }

            st.server_txs.retain(|_, tx| {
                if let Some((next, interval)) = tx.invite_final.as_mut() {
                    if !tx.acked && !reliable && now >= *next {
                        let _ = self.transport.send(tx.dest, &tx.last_response);
                        *interval = (*interval * 2).min(T2);
                        *next = now + *interval;
                    }
                }
                now.duration_since(tx.created) < TX_TIMEOUT
            });

            let mut ack_timeouts = Vec::new();
            for call in st.calls.values_mut() {
                if let Some((bytes, next, interval, started)) = call.pending_2xx.as_mut() {
                    if now.duration_since(*started) >= TX_TIMEOUT {
                        ack_timeouts.push(call.id);
                    } else if !reliable && now >= *next {
                        let _ = self.transport.send(call.peer, bytes);
                        *interval = (*interval * 2).min(T2);
                        *next = now + *interval;
                    }
                }
            }
            for id in ack_timeouts {
                if let Some(c) = st.calls.get_mut(&id) {
                    c.pending_2xx = None;
                    c.local_cseq += 1;
                    let bye = self.dialog_request(c, Method::Bye);
                    let d = self.dialog_dest(c);
                    self.send_request(st, bye, d, Purpose::Bye(id));
                }
                self.terminate(st, id, 408, "ACK timeout", &mut events, &mut stop);
            }

            // Purge terminated calls after a grace period.
            let expired: Vec<(u64, String)> = st
                .calls
                .values()
                .filter(|c| c.state == CallState::Terminated && now.duration_since(c.created) > Duration::from_secs(5) && c.pending_2xx.is_none())
                .map(|c| (c.id, c.sip_call_id.clone()))
                .collect();
            for (id, sip) in expired {
                if st.client_txs.values().any(|t| matches!(t.purpose, Purpose::Bye(x) if x == id) && t.completed_at.is_none()) {
                    continue;
                }
                st.calls.remove(&id);
                if st.by_sip_id.get(&sip) == Some(&id) {
                    st.by_sip_id.remove(&sip);
                }
            }

            let reg = &mut st.registration;
            if reg.refresh_at.is_some_and(|t| now >= t) && !reg.unregistering {
                reg.refresh_at = None;
                refresh_register = true;
            }
            if reg.registered && self.cfg.transport == TransportKind::Udp && self.cfg.keepalive_secs > 0 {
                let due = reg.last_keepalive.is_none_or(|t| now.duration_since(t) >= Duration::from_secs(self.cfg.keepalive_secs as u64));
                if due {
                    reg.last_keepalive = Some(now);
                    let registrar = self.cfg.registrar.clone().unwrap_or_else(|| self.cfg.domain.clone());
                    if let Some(d) = self.proxy_addr().or_else(|| self.resolve_host(&registrar)) {
                        let _ = self.transport.send(d, b"\r\n\r\n");
                    }
                }
            }
        }
        for m in stop {
            m.stop();
        }
        for e in events {
            self.emit(e);
        }
        if refresh_register {
            if let Err(e) = self.send_register(false) {
                self.log("warn", format!("registration refresh failed: {e}"));
            }
        }
    }
}

fn parse_dtmf_info(body: &str) -> Option<char> {
    for line in body.lines() {
        if let Some((k, v)) = line.split_once('=') {
            if k.trim().eq_ignore_ascii_case("Signal") {
                return v.trim().chars().next();
            }
        }
    }
    body.trim().chars().next().filter(|c| crate::codec::dtmf::digit_to_event(*c).is_some())
}

fn uri_escape(s: &str) -> String {
    let mut out = String::with_capacity(s.len() * 3);
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'!' | b'~' | b'*' | b'\'' | b'(' | b')' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

fn uri_unescape(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'%' && i + 2 < bytes.len() + 0 && i + 2 <= bytes.len() - 1 {
            if let Ok(v) = u8::from_str_radix(&s[i + 1..i + 3], 16) {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// Extracts an unescaped `Replaces` value from a Refer-To header (`<sip:x@y?Replaces=...>`).
fn extract_replaces(refer_to: &str) -> Option<String> {
    let q = refer_to.find("?")?;
    let end = refer_to[q..].find('>').map_or(refer_to.len(), |e| q + e);
    refer_to[q + 1..end]
        .split('&')
        .find_map(|p| p.strip_prefix("Replaces=").map(uri_unescape))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicU32;

    #[derive(Default)]
    struct Recorder {
        events: Mutex<Vec<Event>>,
        audio_frames: AtomicU32,
        dtmf: Mutex<String>,
        video: Mutex<Vec<(u32, bool, Vec<u8>)>>,
    }

    impl EndpointHandler for Recorder {
        fn on_event(&self, event: &Event) {
            self.events.lock().push(event.clone());
        }
        fn on_audio(&self, _: u64, direction: AudioDirection, _: u32, pcm: &[i16]) {
            if direction == AudioDirection::Inbound && pcm.iter().any(|s| s.unsigned_abs() > 500) {
                self.audio_frames.fetch_add(1, Ordering::Relaxed);
            }
        }
        fn on_dtmf(&self, _: u64, digit: char, _: DtmfSource) {
            self.dtmf.lock().push(digit);
        }
        fn on_encoded(&self, _: u64, _: u8, _: u32, _: bool, _: &[u8]) {}
        fn on_video_frame(&self, _: u64, timestamp: u32, keyframe: bool, frame: &[u8]) {
            self.video.lock().push((timestamp, keyframe, frame.to_vec()));
        }
    }

    impl Recorder {
        /// `pred` must not touch the recorder: it runs while the event log is locked.
        fn wait_for<F: Fn(&Event) -> bool>(&self, timeout_ms: u64, pred: F) -> Option<Event> {
            let deadline = Instant::now() + Duration::from_millis(timeout_ms);
            while Instant::now() < deadline {
                if let Some(e) = self.events.lock().iter().find(|e| pred(e)) {
                    return Some(e.clone());
                }
                std::thread::sleep(Duration::from_millis(10));
            }
            None
        }

        fn incoming_calls(&self) -> Vec<u64> {
            self.events
                .lock()
                .iter()
                .filter_map(|e| if let Event::IncomingCall { call_id, .. } = e { Some(*call_id) } else { None })
                .collect()
        }

        /// Waits until at least `min` loud inbound frames arrived; returns the count. Paced media takes
        /// longer on busy CI machines, so tests wait for it instead of sleeping a fixed time.
        fn wait_frames(&self, min: u32, timeout_ms: u64) -> u32 {
            let deadline = Instant::now() + Duration::from_millis(timeout_ms);
            while self.audio_frames.load(Ordering::Relaxed) < min && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(20));
            }
            self.audio_frames.load(Ordering::Relaxed)
        }

        /// Waits for at least `min` video frames and returns the ones received.
        fn wait_video(&self, min: usize, timeout_ms: u64) -> Vec<(u32, bool, Vec<u8>)> {
            let deadline = Instant::now() + Duration::from_millis(timeout_ms);
            while self.video.lock().len() < min && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(20));
            }
            self.video.lock().clone()
        }

        /// Waits until the received DTMF digits equal `expected`; returns what arrived.
        fn wait_dtmf(&self, expected: &str, timeout_ms: u64) -> String {
            let deadline = Instant::now() + Duration::from_millis(timeout_ms);
            while self.dtmf.lock().as_str() != expected && Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(20));
            }
            self.dtmf.lock().clone()
        }

        /// Waits until more than `already` incoming calls were seen and returns the latest id.
        fn wait_for_incoming(&self, timeout_ms: u64, already: usize) -> Option<u64> {
            let deadline = Instant::now() + Duration::from_millis(timeout_ms);
            while Instant::now() < deadline {
                let calls = self.incoming_calls();
                if calls.len() > already {
                    return calls.last().copied();
                }
                std::thread::sleep(Duration::from_millis(10));
            }
            None
        }
    }

    fn cfg(user: &str) -> EndpointConfig {
        EndpointConfig {
            bind_address: "127.0.0.1".into(),
            sip_port: 0,
            username: user.into(),
            display_name: user.to_uppercase(),
            rtp_port_min: 30000,
            rtp_port_max: 40000,
            jitter_min_ms: 20,
            ..Default::default()
        }
    }

    fn pair(a_cfg: EndpointConfig, b_cfg: EndpointConfig) -> (Endpoint, Arc<Recorder>, Endpoint, Arc<Recorder>) {
        let (ra, rb) = (Arc::new(Recorder::default()), Arc::new(Recorder::default()));
        let a = Endpoint::start(a_cfg, ra.clone()).unwrap();
        let b = Endpoint::start(b_cfg, rb.clone()).unwrap();
        (a, ra, b, rb)
    }

    fn tone() -> Vec<i16> {
        (0..16000).map(|i| (9000.0 * (i as f64 * 2.0 * std::f64::consts::PI * 500.0 / 16000.0).sin()) as i16).collect()
    }

    fn establish(a: &Endpoint, b: &Endpoint, rb: &Recorder, ra: &Recorder) -> (u64, u64) {
        let target = format!("sip:bob@{}", b.local_address());
        let a_call = a.make_call(&target).unwrap();
        let Some(Event::IncomingCall { call_id: b_call, .. }) = rb.wait_for(3000, |e| matches!(e, Event::IncomingCall { .. })) else {
            panic!("no incoming call");
        };
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "ringing", .. })).expect("ringing");
        b.answer(b_call).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "connected", .. })).expect("caller connected");
        (a_call, b_call)
    }

    #[test]
    fn video_calls_negotiate_a_second_stream_and_carry_frames() {
        let (mut ca, mut cb) = (cfg("alice"), cfg("bob"));
        ca.video = true;
        cb.video = true;
        let (a, ra, b, rb) = pair(ca, cb);
        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        let is_video = |e: &Event| matches!(e, Event::MediaEvent { kind, detail, .. } if kind == "video" && detail.starts_with("H264"));
        ra.wait_for(3000, is_video).expect("caller video stream");
        rb.wait_for(3000, is_video).expect("callee video stream");
        assert_eq!(a.video_codec(a_call).unwrap().as_deref(), Some("H264"));
        assert_eq!(b.video_codec(b_call).unwrap().as_deref(), Some("H264"));
        // An access unit large enough to be split across several RTP packets.
        let mut frame = vec![0, 0, 0, 1, 0x67, 0x42, 0xe0, 0x1f];
        frame.extend_from_slice(&[0, 0, 0, 1, 0x65]);
        frame.extend((0..3000).map(|i| (i % 251) as u8 | 1));
        // ICE and DTLS may still be settling, so send a few frames until one arrives.
        let mut received = Vec::new();
        for i in 0..10u32 {
            a.send_video_frame(a_call, 90_000 + i * 3000, &frame).unwrap();
            received = rb.wait_video(1, 300);
            if !received.is_empty() {
                break;
            }
        }
        let (_, keyframe, data) = received.first().expect("callee received a video frame").clone();
        assert!(keyframe, "the frame carries an IDR slice");
        assert_eq!(data, frame);
        a.hangup(a_call).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "terminated", .. })).expect("terminated");
    }

    #[test]
    fn call_answer_audio_dtmf_and_hangup() {
        let (a, ra, b, rb) = pair(cfg("alice"), cfg("bob"));
        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        assert_eq!(a.call_info(a_call).unwrap().codec.as_deref(), Some("opus"));

        a.send_audio(a_call, &tone(), 16000).unwrap();
        b.send_dtmf(b_call, "42", 80).unwrap();
        assert!(rb.wait_frames(20, 5000) >= 20, "bob heard {} frames", rb.audio_frames.load(Ordering::Relaxed));
        assert_eq!(ra.wait_dtmf("42", 5000), "42");

        a.hangup(a_call).unwrap();
        rb.wait_for(3000, |e| matches!(e, Event::CallState { state: "terminated", .. })).expect("bob terminated");
    }

    #[test]
    fn hold_and_resume_via_reinvite() {
        let (a, ra, b, rb) = pair(cfg("alice"), cfg("bob"));
        let (a_call, _b_call) = establish(&a, &b, &rb, &ra);
        a.set_hold(a_call, true).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "on-hold", .. })).expect("local hold");
        rb.wait_for(3000, |e| matches!(e, Event::CallState { state: "remote-hold", .. })).expect("remote hold");
        a.set_hold(a_call, false).unwrap();
        rb.wait_for(3000, |e| matches!(e, Event::CallState { reason, .. } if reason == "resumed by remote")).expect("resume");
        a.hangup(a_call).unwrap();
    }

    #[test]
    fn caller_cancel_and_callee_reject() {
        let (a, ra, b, rb) = pair(cfg("alice"), cfg("bob"));
        let target = format!("sip:bob@{}", b.local_address());
        let call = a.make_call(&target).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "ringing", .. })).expect("ringing");
        a.hangup(call).unwrap();
        rb.wait_for(3000, |e| matches!(e, Event::CallState { code: 487, .. })).expect("callee sees cancel");
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "terminated", .. })).expect("caller terminated");

        let first_incoming = rb.incoming_calls();
        let call2 = a.make_call(&target).unwrap();
        let second = rb.wait_for_incoming(3000, first_incoming.len()).expect("second call arrives");
        b.reject(second, 486).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { call_id, code: 486, .. } if *call_id == call2)).expect("busy");
    }

    #[test]
    fn digest_auth_against_challenging_peer() {
        // A tiny registrar that challenges then accepts.
        let server = std::net::UdpSocket::bind("127.0.0.1:0").unwrap();
        let addr = server.local_addr().unwrap();
        std::thread::spawn(move || {
            let mut buf = [0u8; 4096];
            for _ in 0..2 {
                let (n, from) = server.recv_from(&mut buf).unwrap();
                let (req, _) = SipMessage::parse(&buf[..n]).unwrap();
                let mut resp;
                if let Some(auth) = req.header("Authorization") {
                    let ok = crate::sip::auth::verify_authorization(auth, "REGISTER", "secret");
                    resp = SipMessage::response(if ok { 200 } else { 403 }, None);
                } else {
                    resp = SipMessage::response(401, None);
                    resp.add_header("WWW-Authenticate", "Digest realm=\"test\", nonce=\"n1\", qop=\"auth\"");
                }
                for h in ["Via", "From", "To", "Call-ID", "CSeq", "Contact"] {
                    if let Some(v) = req.header(h) {
                        resp.add_header(h, v);
                    }
                }
                server.send_to(&resp.to_bytes(), from).unwrap();
            }
        });
        let rec = Arc::new(Recorder::default());
        let ep = Endpoint::start(
            EndpointConfig { domain: addr.to_string(), password: "secret".into(), ..cfg("carol") },
            rec.clone(),
        )
        .unwrap();
        ep.register().unwrap();
        let ev = rec.wait_for(3000, |e| matches!(e, Event::RegistrationChanged { .. })).expect("registration result");
        assert!(matches!(ev, Event::RegistrationChanged { state: "registered", code: 200, .. }), "{ev:?}");
    }

    #[test]
    fn options_ping_and_message() {
        let (a, ra, b, rb) = pair(cfg("alice"), cfg("bob"));
        let target = format!("sip:bob@{}", b.local_address());
        let rid = a.send_options(&target).unwrap();
        let ev = ra.wait_for(3000, |e| matches!(e, Event::RequestResult { request_id, .. } if *request_id == rid)).expect("options");
        assert!(matches!(ev, Event::RequestResult { code: 200, .. }));
        a.send_message(&target, "text/plain", "halo dari Voip.NET").unwrap();
        let ev = rb.wait_for(3000, |e| matches!(e, Event::MessageReceived { .. })).expect("message");
        assert!(matches!(ev, Event::MessageReceived { body, .. } if body == "halo dari Voip.NET"));
    }

    #[test]
    fn blind_transfer_moves_call_to_third_party() {
        let (a, ra, b, rb) = pair(cfg("alice"), cfg("bob"));
        let rc = Arc::new(Recorder::default());
        let c = Endpoint::start(cfg("carol"), rc.clone()).unwrap();
        let (a_call, _b_call) = establish(&a, &b, &rb, &ra);

        // Alice transfers Bob to Carol.
        a.transfer(a_call, &format!("sip:carol@{}", c.local_address())).unwrap();
        let Some(Event::IncomingCall { call_id: c_call, .. }) = rc.wait_for(4000, |e| matches!(e, Event::IncomingCall { .. })) else {
            panic!("carol not called")
        };
        c.answer(c_call).unwrap();
        ra.wait_for(4000, |e| matches!(e, Event::TransferProgress { code: 200, .. })).expect("transfer success notify");
        ra.wait_for(4000, |e| matches!(e, Event::CallState { call_id, state: "terminated", .. } if *call_id == a_call)).expect("alice leg ends");
        rb.wait_for(4000, |e| matches!(e, Event::TransferRequested { new_call_id: Some(_), .. })).expect("bob executed transfer");
    }

    #[test]
    fn srtp_mandatory_call_negotiates_crypto() {
        let mut ca = cfg("alice");
        ca.srtp = SrtpMode::Mandatory;
        let mut cb = cfg("bob");
        cb.srtp = SrtpMode::Optional;
        let (a, ra, b, rb) = pair(ca, cb);
        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        a.send_audio(a_call, &tone(), 16000).unwrap();
        assert_eq!(b.call_stats(b_call).unwrap().srtp_active, 1);
        assert!(rb.wait_frames(10, 5000) >= 10);
        a.hangup(a_call).unwrap();

        // Disabled SRTP must reject an RTP/SAVP offer with 488.
        let (d, rd) = (cfg("dave"), Arc::new(Recorder::default()));
        let dave = Endpoint::start(d, rd).unwrap();
        let call = a.make_call(&format!("sip:dave@{}", dave.local_address())).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { call_id, code: 488, .. } if *call_id == call)).expect("488");
    }

    #[test]
    fn replaces_helpers() {
        let escaped = uri_escape("abc@host;to-tag=1;from-tag=2");
        let refer_to = format!("<sip:bob@example.com?Replaces={escaped}>");
        assert_eq!(extract_replaces(&refer_to).as_deref(), Some("abc@host;to-tag=1;from-tag=2"));
        assert_eq!(parse_dtmf_info("Signal=5\r\nDuration=160"), Some('5'));
    }

    fn tls_cfg(user: &str) -> EndpointConfig {
        EndpointConfig { transport: TransportKind::Tls, ..cfg(user) }
    }

    #[test]
    fn tls_call_with_pinned_certificate() {
        let (rb, ra) = (Arc::new(Recorder::default()), Arc::new(Recorder::default()));
        let b = Endpoint::start(tls_cfg("bob"), rb.clone()).unwrap();
        let fingerprint = b.tls_fingerprint().expect("tls fingerprint");
        let a = Endpoint::start(EndpointConfig { tls_pinned_fingerprints: vec![fingerprint], ..tls_cfg("alice") }, ra.clone()).unwrap();

        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        a.send_audio(a_call, &tone(), 16000).unwrap();
        b.send_dtmf(b_call, "7", 80).unwrap();
        assert!(rb.wait_frames(20, 5000) >= 20);
        assert_eq!(ra.wait_dtmf("7", 5000), "7");
        b.hangup(b_call).unwrap();
        ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "terminated", .. })).expect("caller terminated");
    }

    #[test]
    fn tls_rejects_untrusted_and_mismatched_certificates() {
        let (rb, ra) = (Arc::new(Recorder::default()), Arc::new(Recorder::default()));
        let b = Endpoint::start(tls_cfg("bob"), rb.clone()).unwrap();
        let target = format!("sip:bob@{}", b.local_address());

        // Self-signed certificate against the public roots.
        let a = Endpoint::start(tls_cfg("alice"), ra.clone()).unwrap();
        let call = a.make_call(&target).unwrap();
        ra.wait_for(8000, |e| matches!(e, Event::CallState { call_id, state: "terminated", .. } if *call_id == call)).expect("untrusted fails");

        // Wrong pin.
        let rc = Arc::new(Recorder::default());
        let c = Endpoint::start(EndpointConfig { tls_pinned_fingerprints: vec!["00:11".into()], ..tls_cfg("carol") }, rc.clone()).unwrap();
        let call = c.make_call(&target).unwrap();
        rc.wait_for(8000, |e| matches!(e, Event::CallState { call_id, state: "terminated", .. } if *call_id == call)).expect("pin mismatch fails");
        assert!(rb.incoming_calls().is_empty());

        // Verification disabled.
        let rd = Arc::new(Recorder::default());
        let d = Endpoint::start(EndpointConfig { tls_verify_server: false, ..tls_cfg("dave") }, rd.clone()).unwrap();
        d.make_call(&target).unwrap();
        rb.wait_for_incoming(3000, 0).expect("unverified connection reaches bob");
    }

    #[test]
    fn dtls_srtp_call_encrypts_media() {
        let dtls = |user: &str| EndpointConfig { srtp: SrtpMode::Mandatory, srtp_keying: SrtpKeying::Dtls, ..cfg(user) };
        // The callee only prefers SDES but accepts a DTLS offer.
        let (a, ra, b, rb) = pair(dtls("alice"), EndpointConfig { srtp: SrtpMode::Optional, ..cfg("bob") });
        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        ra.wait_for(5000, |e| matches!(e, Event::MediaEvent { kind, .. } if kind == "dtls-connected")).expect("caller DTLS keys");
        rb.wait_for(5000, |e| matches!(e, Event::MediaEvent { kind, .. } if kind == "dtls-connected")).expect("callee DTLS keys");
        a.send_audio(a_call, &tone(), 16000).unwrap();
        b.send_dtmf(b_call, "9", 80).unwrap();
        assert!(rb.wait_frames(20, 5000) >= 20, "bob heard {}", rb.audio_frames.load(Ordering::Relaxed));
        assert_eq!(ra.wait_dtmf("9", 5000), "9");
        assert_eq!(a.call_stats(a_call).unwrap().srtp_active, 1);
        assert_eq!(b.call_stats(b_call).unwrap().srtp_active, 1);
        a.hangup(a_call).unwrap();
    }


    #[test]
    fn mutual_tls_requires_a_trusted_caller() {
        // Alice accepts Bob's self-signed certificate; Bob asks callers for one and pins Alice's.
        let ra = Arc::new(Recorder::default());
        let a = Endpoint::start(EndpointConfig { tls_verify_server: false, ..tls_cfg("alice") }, ra.clone()).unwrap();
        let rb = Arc::new(Recorder::default());
        let b = Endpoint::start(
            EndpointConfig {
                tls_require_client_certificate: true,
                tls_pinned_fingerprints: vec![a.tls_fingerprint().expect("alice certificate")],
                ..tls_cfg("bob")
            },
            rb.clone(),
        )
        .unwrap();
        let target = format!("sip:bob@{}", b.local_address());

        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        a.send_audio(a_call, &tone(), 16000).unwrap();
        assert!(rb.wait_frames(20, 5000) >= 20);
        b.hangup(b_call).unwrap();

        // A caller Bob has not pinned is refused during the handshake.
        let rc = Arc::new(Recorder::default());
        let c = Endpoint::start(EndpointConfig { tls_verify_server: false, ..tls_cfg("mallory") }, rc.clone()).unwrap();
        let refused = c.make_call(&target).unwrap();
        rc.wait_for(8000, |e| matches!(e, Event::CallState { call_id, state: "terminated", .. } if *call_id == refused))
            .expect("untrusted caller is refused");
        assert_eq!(rb.incoming_calls().len(), 1, "only Alice got through");
    }


    /// Writes a fresh self-signed certificate and key to `dir`, returning its fingerprint.
    fn write_certificate(dir: &std::path::Path) -> String {
        let key = rcgen::KeyPair::generate().unwrap();
        let cert = rcgen::CertificateParams::new(vec!["localhost".to_owned()]).unwrap().self_signed(&key).unwrap();
        std::fs::write(dir.join("cert.pem"), cert.pem()).unwrap();
        std::fs::write(dir.join("key.pem"), key.serialize_pem()).unwrap();
        crate::sip::tls::fingerprint_sha256(cert.der())
    }

    #[test]
    fn tls_certificates_reload_without_a_restart() {
        let dir = std::env::temp_dir().join(format!("voipnet-tls-{}", random_token(8)));
        std::fs::create_dir_all(&dir).unwrap();
        let first = write_certificate(&dir);
        let bob_cfg = EndpointConfig {
            tls_certificate_file: Some(dir.join("cert.pem").to_string_lossy().into_owned()),
            tls_private_key_file: Some(dir.join("key.pem").to_string_lossy().into_owned()),
            ..tls_cfg("bob")
        };
        let rb = Arc::new(Recorder::default());
        let b = Endpoint::start(bob_cfg, rb.clone()).unwrap();
        assert_eq!(b.tls_fingerprint().as_deref(), Some(first.as_str()));

        let target = format!("sip:bob@{}", b.local_address());
        let ra = Arc::new(Recorder::default());
        let a = Endpoint::start(EndpointConfig { tls_pinned_fingerprints: vec![first.clone()], ..tls_cfg("alice") }, ra.clone()).unwrap();
        let call = a.make_call(&target).unwrap();
        rb.wait_for_incoming(5000, 0).expect("call with the first certificate");
        a.hangup(call).unwrap();

        // Renew the certificate on disk and pick it up without restarting.
        let second = write_certificate(&dir);
        assert_ne!(second, first);
        b.reload_tls().unwrap();
        assert_eq!(b.tls_fingerprint().as_deref(), Some(second.as_str()));

        let rc = Arc::new(Recorder::default());
        let c = Endpoint::start(EndpointConfig { tls_pinned_fingerprints: vec![second], ..tls_cfg("carol") }, rc.clone()).unwrap();
        c.make_call(&target).unwrap();
        rb.wait_for_incoming(5000, 1).expect("call with the renewed certificate");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn websocket_calls_over_ws_and_wss() {
        for kind in [TransportKind::Ws, TransportKind::Wss] {
            let (rb, ra) = (Arc::new(Recorder::default()), Arc::new(Recorder::default()));
            let b = Endpoint::start(EndpointConfig { transport: kind, ..cfg("bob") }, rb.clone()).unwrap();
            let pins = b.tls_fingerprint().into_iter().collect();
            let a = Endpoint::start(EndpointConfig { transport: kind, tls_pinned_fingerprints: pins, ..cfg("alice") }, ra.clone()).unwrap();
            let (a_call, b_call) = establish(&a, &b, &rb, &ra);
            a.send_audio(a_call, &tone(), 16000).unwrap();
            assert!(rb.wait_frames(20, 5000) >= 20, "{kind:?}");
            // In-dialog request from the callee travels back over the caller's connection.
            b.hangup(b_call).unwrap();
            ra.wait_for(3000, |e| matches!(e, Event::CallState { state: "terminated", .. })).expect("caller terminated");
        }
    }

    #[test]
    fn ice_nominates_a_pair_and_restarts_mid_call() {
        let ice = |user: &str| EndpointConfig { ice: true, srtp: SrtpMode::Mandatory, srtp_keying: SrtpKeying::Dtls, ..cfg(user) };
        let (a, ra, b, rb) = pair(ice("alice"), ice("bob"));
        let (a_call, b_call) = establish(&a, &b, &rb, &ra);
        let connected = |r: &Recorder| r.events.lock().iter().filter(|e| matches!(e, Event::MediaEvent { kind, .. } if kind == "ice-connected")).count();
        ra.wait_for(5000, |e| matches!(e, Event::MediaEvent { kind, .. } if kind == "dtls-connected")).expect("keys after ICE");
        assert_eq!(connected(&ra), 1);
        assert_eq!(connected(&rb), 1);
        assert_eq!(a.call_stats(a_call).unwrap().ice_connected, 1);

        a.restart_ice(a_call).unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while (connected(&ra) < 2 || connected(&rb) < 2) && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        assert_eq!((connected(&ra), connected(&rb)), (2, 2), "both sides select a pair again after the restart");

        a.send_audio(a_call, &tone(), 16000).unwrap();
        assert!(rb.wait_frames(20, 5000) >= 20);
        b.hangup(b_call).unwrap();
    }
}
