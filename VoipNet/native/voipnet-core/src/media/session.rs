//! Per-call RTP media session: socket I/O, SRTP, ICE checks, jitter buffer,
//! decoding with concealment, paced transmission, DTMF and conferencing.

use std::collections::VecDeque;
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicU8, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use parking_lot::Mutex;

use super::conference::{Conference, CONFERENCE_RATE};
use super::resample::Resampler;
use crate::codec::dtmf::{self, DtmfDetector, TelephoneEvent};
use crate::codec::{create_audio_codec, AudioCodec, Concealer};
use crate::rtp::jitter::{JitterBuffer, JitterStats, Playout};
use crate::rtp::packet::{build_bye, build_sender_report, classify, PacketClass, RtpHeader, RtpPacketRef};
use crate::sdp::{Direction, RtpMap};
use crate::srtp::SrtpContext;
use crate::stun::{self, Candidate, CandidateKind, StunMessage, TurnAllocation};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum AudioDirection {
    Inbound = 0,
    Outbound = 1,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum DtmfSource {
    Rfc4733 = 0,
    InBand = 1,
    SipInfo = 2,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DtmfMode {
    Rfc4733,
    InBand,
    SipInfo,
}

/// Receives media from a session. Called on engine threads — keep handlers fast.
pub trait MediaSink: Send + Sync {
    fn on_audio(&self, call_id: u64, direction: AudioDirection, sample_rate: u32, pcm: &[i16]);
    fn on_dtmf(&self, call_id: u64, digit: char, source: DtmfSource);
    fn on_encoded(&self, call_id: u64, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]);
    fn on_media_event(&self, call_id: u64, kind: &str, detail: &str);
}

#[derive(Debug, Clone)]
pub struct MediaConfig {
    pub local_ip: IpAddr,
    pub advertised_ip: Option<IpAddr>,
    pub port_min: u16,
    pub port_max: u16,
    pub ptime_ms: u32,
    pub jitter_min_ms: u32,
    pub jitter_max_ms: u32,
    pub stun_server: Option<SocketAddr>,
    pub turn_server: Option<SocketAddr>,
    pub turn_username: String,
    pub turn_password: String,
    pub detect_inband_dtmf: bool,
    pub rtp_timeout_ms: u32,
    pub symmetric_rtp: bool,
}

impl Default for MediaConfig {
    fn default() -> Self {
        Self {
            local_ip: IpAddr::from([0, 0, 0, 0]),
            advertised_ip: None,
            port_min: 10000,
            port_max: 20000,
            ptime_ms: 20,
            jitter_min_ms: 40,
            jitter_max_ms: 300,
            stun_server: None,
            turn_server: None,
            turn_username: String::new(),
            turn_password: String::new(),
            detect_inband_dtmf: false,
            rtp_timeout_ms: 30_000,
            symmetric_rtp: true,
        }
    }
}

/// Result of SDP negotiation applied to a session.
#[derive(Debug, Clone)]
pub struct NegotiatedMedia {
    pub remote: Option<SocketAddr>,
    pub codec: RtpMap,
    pub dtmf: Option<RtpMap>,
    pub direction: Direction,
    pub remote_srtp_key: Option<String>,
    pub remote_ice_ufrag: Option<String>,
    pub remote_ice_pwd: Option<String>,
    pub remote_candidates: Vec<Candidate>,
    pub ptime_ms: Option<u32>,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct MediaStats {
    pub packets_sent: u64,
    pub packets_received: u64,
    pub bytes_sent: u64,
    pub bytes_received: u64,
    pub packets_lost: u64,
    pub packets_late: u64,
    pub jitter_ms: f64,
    pub jitter_buffer_ms: u32,
    pub payload_type: u32,
    pub sample_rate: u32,
    pub mos: f64,
    pub srtp_active: u8,
    pub ice_connected: u8,
    pub outbound_queued_ms: u32,
}

struct TxState {
    codec: Option<Box<dyn AudioCodec>>,
    payload_type: u8,
    dtmf_pt: Option<u8>,
    sequence: u16,
    timestamp: u32,
    ssrc: u32,
    ts_per_frame: u32,
    samples_per_frame: usize,
    codec_rate: u32,
    pending: VecDeque<i16>,
    input_resampler: Option<Resampler>,
    resample_buf: Vec<i16>,
    frame: Vec<i16>,
    encoded: Vec<u8>,
    packet: Vec<u8>,
    srtp: Option<SrtpContext>,
    dtmf_queue: VecDeque<(char, u32)>,
    dtmf_active: Option<ActiveDtmf>,
    was_silent: bool,
    octets: u32,
    packets: u32,
    conf_resampler_in: Option<Resampler>,
    conf_resampler_out: Option<Resampler>,
    dtmf_mode: DtmfMode,
}

struct ActiveDtmf {
    event: u8,
    timestamp: u32,
    elapsed: u32,
    total: u32,
    end_sent: u8,
    gap_frames: u8,
}

struct RxState {
    jitter: JitterBuffer,
    codec: Option<Box<dyn AudioCodec>>,
    payload_type: u8,
    dtmf_pt: Option<u8>,
    plc: Concealer,
    detector: Option<DtmfDetector>,
    last_event_ts: Option<u32>,
    srtp: Option<SrtpContext>,
    buf: Vec<u8>,
    started: bool,
}

struct IceState {
    local_ufrag: String,
    local_pwd: String,
    remote_ufrag: Option<String>,
    remote_pwd: Option<String>,
    candidates: Vec<Candidate>,
    connected: bool,
    last_check: Instant,
}

struct Shared {
    call_id: u64,
    socket: UdpSocket,
    running: AtomicBool,
    remote: Mutex<Option<SocketAddr>>,
    latched: AtomicBool,
    direction: AtomicU8,
    muted: AtomicBool,
    tx: Mutex<TxState>,
    rx: Mutex<RxState>,
    ice: Mutex<IceState>,
    relay: Mutex<Option<TurnAllocation>>,
    conference: Mutex<Option<Arc<Conference>>>,
    sink: Arc<dyn MediaSink>,
    config: MediaConfig,
    packets_sent: AtomicU64,
    packets_received: AtomicU64,
    bytes_sent: AtomicU64,
    bytes_received: AtomicU64,
    last_rx: Mutex<Instant>,
    timeout_reported: AtomicBool,
}

pub struct MediaSession {
    shared: Arc<Shared>,
    local: SocketAddr,
    reflexive: Option<SocketAddr>,
    threads: Mutex<Vec<JoinHandle<()>>>,
    local_srtp_key: Mutex<Option<String>>,
}

fn direction_from(v: u8) -> Direction {
    match v {
        1 => Direction::SendOnly,
        2 => Direction::RecvOnly,
        3 => Direction::Inactive,
        _ => Direction::SendRecv,
    }
}

fn direction_to(d: Direction) -> u8 {
    match d {
        Direction::SendRecv => 0,
        Direction::SendOnly => 1,
        Direction::RecvOnly => 2,
        Direction::Inactive => 3,
    }
}

impl MediaSession {
    /// Binds a socket in the configured port range and gathers candidates.
    pub fn new(call_id: u64, config: MediaConfig, sink: Arc<dyn MediaSink>) -> std::io::Result<Arc<Self>> {
        let socket = bind_in_range(config.local_ip, config.port_min, config.port_max)?;
        let bound = socket.local_addr()?;
        let advertised_ip = config.advertised_ip.unwrap_or_else(|| {
            if bound.ip().is_unspecified() {
                crate::net::primary_local_ip()
            } else {
                bound.ip()
            }
        });
        let local = SocketAddr::new(advertised_ip, bound.port());

        let mut candidates = vec![Candidate::new(CandidateKind::Host, local, 1)];
        let reflexive = config.stun_server.and_then(|s| stun::discover_reflexive(&socket, s, Duration::from_millis(1500)));
        if let Some(r) = reflexive {
            if r != local {
                candidates.push(Candidate::new(CandidateKind::ServerReflexive, r, 1));
            }
        }
        let relay = config.turn_server.and_then(|server| {
            TurnAllocation::allocate(&socket, server, &config.turn_username, &config.turn_password, Duration::from_secs(3))
        });
        if let Some(r) = &relay {
            candidates.push(Candidate::new(CandidateKind::Relayed, r.relayed, 1));
        }

        let ptime = config.ptime_ms.clamp(10, 60);
        let min_depth = (config.jitter_min_ms / ptime).max(1) as usize;
        let max_depth = (config.jitter_max_ms / ptime).max(min_depth as u32 + 1) as usize;
        let ssrc: u32 = rand::random();
        let shared = Arc::new(Shared {
            call_id,
            socket,
            running: AtomicBool::new(false),
            remote: Mutex::new(None),
            latched: AtomicBool::new(false),
            direction: AtomicU8::new(0),
            muted: AtomicBool::new(false),
            tx: Mutex::new(TxState {
                codec: None,
                payload_type: 0,
                dtmf_pt: None,
                sequence: rand::random(),
                timestamp: rand::random(),
                ssrc,
                ts_per_frame: 160,
                samples_per_frame: 160,
                codec_rate: 8000,
                pending: VecDeque::with_capacity(16000),
                input_resampler: None,
                resample_buf: Vec::with_capacity(1024),
                frame: Vec::with_capacity(960),
                encoded: Vec::with_capacity(1500),
                packet: Vec::with_capacity(1500),
                srtp: None,
                dtmf_queue: VecDeque::new(),
                dtmf_active: None,
                was_silent: true,
                octets: 0,
                packets: 0,
                conf_resampler_in: None,
                conf_resampler_out: None,
                dtmf_mode: DtmfMode::Rfc4733,
            }),
            rx: Mutex::new(RxState {
                jitter: JitterBuffer::new(ptime, 8000, min_depth, max_depth),
                codec: None,
                payload_type: 0,
                dtmf_pt: None,
                plc: Concealer::default(),
                detector: None,
                last_event_ts: None,
                srtp: None,
                buf: Vec::with_capacity(1500),
                started: false,
            }),
            ice: Mutex::new(IceState {
                local_ufrag: crate::sip::message::random_token(8),
                local_pwd: crate::sip::message::random_token(24),
                remote_ufrag: None,
                remote_pwd: None,
                candidates,
                connected: false,
                last_check: Instant::now(),
            }),
            relay: Mutex::new(relay),
            conference: Mutex::new(None),
            sink,
            config: MediaConfig { ptime_ms: ptime, ..config },
            packets_sent: AtomicU64::new(0),
            packets_received: AtomicU64::new(0),
            bytes_sent: AtomicU64::new(0),
            bytes_received: AtomicU64::new(0),
            last_rx: Mutex::new(Instant::now()),
            timeout_reported: AtomicBool::new(false),
        });
        Ok(Arc::new(Self { shared, local, reflexive, threads: Mutex::new(Vec::new()), local_srtp_key: Mutex::new(None) }))
    }

    /// Address to advertise in SDP (server-reflexive when available).
    pub fn advertised_address(&self) -> SocketAddr {
        if let Some(r) = self.shared.relay.lock().as_ref() {
            return r.relayed;
        }
        self.reflexive.unwrap_or(self.local)
    }

    pub fn local_address(&self) -> SocketAddr {
        self.local
    }

    pub fn ice_credentials(&self) -> (String, String) {
        let ice = self.shared.ice.lock();
        (ice.local_ufrag.clone(), ice.local_pwd.clone())
    }

    pub fn local_candidates(&self) -> Vec<Candidate> {
        self.shared.ice.lock().candidates.clone()
    }

    /// Creates (once) and returns the local SDES key-params for SRTP.
    pub fn enable_srtp(&self) -> String {
        let mut key = self.local_srtp_key.lock();
        if let Some(k) = key.as_ref() {
            return k.clone();
        }
        let (params, ctx) = SrtpContext::generate();
        self.shared.tx.lock().srtp = Some(ctx);
        *key = Some(params.clone());
        params
    }

    pub fn srtp_enabled(&self) -> bool {
        self.local_srtp_key.lock().is_some()
    }

    pub fn set_dtmf_mode(&self, mode: DtmfMode) {
        self.shared.tx.lock().dtmf_mode = mode;
    }

    /// Applies (re)negotiated parameters and starts media threads if necessary.
    pub fn apply(self: &Arc<Self>, n: &NegotiatedMedia) -> Result<(), String> {
        let sh = &self.shared;
        let ptime = n.ptime_ms.unwrap_or(sh.config.ptime_ms).clamp(10, 60);

        if let Some(key) = &n.remote_srtp_key {
            let ctx = SrtpContext::from_sdes(key).map_err(|e| format!("invalid SRTP key: {e:?}"))?;
            sh.rx.lock().srtp = Some(ctx);
        }
        {
            let mut tx = sh.tx.lock();
            let codec = create_audio_codec(&n.codec);
            let codec_rate = codec.as_ref().map_or(n.codec.clock_rate, |c| c.sample_rate());
            tx.payload_type = n.codec.payload_type;
            tx.dtmf_pt = n.dtmf.as_ref().map(|d| d.payload_type);
            tx.ts_per_frame = n.codec.clock_rate * ptime / 1000;
            tx.samples_per_frame = (codec_rate * ptime / 1000) as usize;
            if tx.codec_rate != codec_rate || tx.input_resampler.is_some() {
                tx.input_resampler = None;
            }
            tx.codec_rate = codec_rate;
            tx.codec = codec;
            tx.conf_resampler_in = None;
            tx.conf_resampler_out = None;
            if n.remote_srtp_key.is_some() && tx.srtp.is_none() {
                return Err("remote offered SRTP but no local key was created".into());
            }
        }
        {
            let mut rx = sh.rx.lock();
            let codec = create_audio_codec(&n.codec);
            let rate = codec.as_ref().map_or(n.codec.clock_rate, |c| c.sample_rate());
            if rx.payload_type != n.codec.payload_type || rx.codec.is_none() {
                rx.jitter = JitterBuffer::new(
                    ptime,
                    n.codec.clock_rate,
                    (sh.config.jitter_min_ms / ptime).max(1) as usize,
                    (sh.config.jitter_max_ms / ptime).max(2) as usize,
                );
                rx.started = false;
            }
            rx.payload_type = n.codec.payload_type;
            rx.dtmf_pt = n.dtmf.as_ref().map(|d| d.payload_type);
            rx.codec = codec;
            rx.detector = sh.config.detect_inband_dtmf.then(|| DtmfDetector::new(rate));
        }
        {
            let mut ice = sh.ice.lock();
            ice.remote_ufrag = n.remote_ice_ufrag.clone();
            ice.remote_pwd = n.remote_ice_pwd.clone();
            for c in &n.remote_candidates {
                if !ice.candidates.iter().any(|x| x.address == c.address) {
                    ice.candidates.push(c.clone());
                }
            }
        }
        if let Some(remote) = n.remote {
            if !sh.latched.load(Ordering::Relaxed) {
                *sh.remote.lock() = Some(remote);
            }
            if let Some(relay) = sh.relay.lock().as_ref() {
                let _ = sh.socket.send_to(&relay.create_permission(remote), relay.server);
            }
        }
        sh.direction.store(direction_to(n.direction), Ordering::Relaxed);
        self.start();
        Ok(())
    }

    fn start(self: &Arc<Self>) {
        if self.shared.running.swap(true, Ordering::SeqCst) {
            return;
        }
        *self.shared.last_rx.lock() = Instant::now();
        let _ = self.shared.socket.set_read_timeout(Some(Duration::from_millis(100)));
        let rx_shared = self.shared.clone();
        let play_shared = self.shared.clone();
        let mut threads = self.threads.lock();
        threads.push(
            std::thread::Builder::new()
                .name(format!("voipnet-rtp-rx-{}", self.shared.call_id))
                .spawn(move || receive_loop(&rx_shared))
                .expect("spawn rtp receive thread"),
        );
        threads.push(
            std::thread::Builder::new()
                .name(format!("voipnet-rtp-play-{}", self.shared.call_id))
                .spawn(move || playout_loop(&play_shared))
                .expect("spawn rtp playout thread"),
        );
    }

    pub fn stop(&self) {
        if !self.shared.running.swap(false, Ordering::SeqCst) {
            return;
        }
        let ssrc = self.shared.tx.lock().ssrc;
        let mut bye = build_bye(ssrc);
        if let Some(ctx) = self.shared.tx.lock().srtp.as_mut() {
            let _ = ctx.protect_rtcp(&mut bye);
        }
        send_raw(&self.shared, &bye);
        if let Some(conf) = self.shared.conference.lock().take() {
            conf.leave(self.shared.call_id);
        }
        for t in self.threads.lock().drain(..) {
            let _ = t.join();
        }
    }

    pub fn set_direction(&self, d: Direction) {
        self.shared.direction.store(direction_to(d), Ordering::Relaxed);
    }

    pub fn direction(&self) -> Direction {
        direction_from(self.shared.direction.load(Ordering::Relaxed))
    }

    pub fn set_muted(&self, muted: bool) {
        self.shared.muted.store(muted, Ordering::Relaxed);
    }

    pub fn is_muted(&self) -> bool {
        self.shared.muted.load(Ordering::Relaxed)
    }

    /// Queues PCM for paced transmission. `sample_rate` is converted to the codec rate.
    /// Returns the total queued duration in milliseconds.
    pub fn send_audio(&self, pcm: &[i16], sample_rate: u32) -> u32 {
        let mut tx = self.shared.tx.lock();
        let tx = &mut *tx;
        if sample_rate != tx.codec_rate {
            let needs_new = tx.input_resampler.as_ref().is_none_or(|r| r.rates() != (sample_rate, tx.codec_rate));
            if needs_new {
                tx.input_resampler = Some(Resampler::new(sample_rate, tx.codec_rate));
            }
            tx.resample_buf.clear();
            if let Some(r) = tx.input_resampler.as_mut() {
                r.process(pcm, &mut tx.resample_buf);
            }
            tx.pending.extend(tx.resample_buf.iter().copied());
        } else {
            tx.pending.extend(pcm.iter().copied());
        }
        // Cap the queue at 5 minutes of audio to bound memory.
        let cap = tx.codec_rate as usize * 300;
        if tx.pending.len() > cap {
            let excess = tx.pending.len() - cap;
            tx.pending.drain(..excess);
        }
        (tx.pending.len() as u64 * 1000 / tx.codec_rate.max(1) as u64) as u32
    }

    /// Drops all queued outbound audio (barge-in / interruption).
    pub fn clear_audio(&self) {
        self.shared.tx.lock().pending.clear();
    }

    /// Sends an already-encoded payload (pass-through codecs and video).
    pub fn send_encoded(&self, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]) {
        let sh = &self.shared;
        let mut tx = sh.tx.lock();
        let tx = &mut *tx;
        tx.packet.clear();
        let header = RtpHeader { marker, payload_type, sequence: tx.sequence, timestamp, ssrc: tx.ssrc };
        tx.sequence = tx.sequence.wrapping_add(1);
        header.write(payload, &mut tx.packet);
        if let Some(ctx) = tx.srtp.as_mut() {
            if ctx.protect_rtp(&mut tx.packet).is_err() {
                return;
            }
        }
        send_raw(sh, &tx.packet);
        sh.packets_sent.fetch_add(1, Ordering::Relaxed);
        sh.bytes_sent.fetch_add(payload.len() as u64, Ordering::Relaxed);
    }

    /// Queues DTMF digits using the configured mode (SIP INFO is handled by the caller).
    pub fn send_dtmf(&self, digits: &str, duration_ms: u32) {
        let mut tx = self.shared.tx.lock();
        let duration_ms = duration_ms.clamp(40, 2000);
        match tx.dtmf_mode {
            DtmfMode::InBand | DtmfMode::Rfc4733 if tx.dtmf_mode == DtmfMode::InBand || tx.dtmf_pt.is_none() => {
                let rate = tx.codec_rate;
                let mut tone = Vec::new();
                for d in digits.chars() {
                    if dtmf::generate_tone(d, rate, duration_ms, 12000, &mut tone) {
                        tone.extend(std::iter::repeat(0).take((rate * 60 / 1000) as usize));
                    }
                }
                tx.pending.extend(tone);
            }
            _ => {
                for d in digits.chars().filter(|d| dtmf::digit_to_event(*d).is_some()) {
                    tx.dtmf_queue.push_back((d, duration_ms));
                }
            }
        }
    }

    pub fn join_conference(&self, conference: Arc<Conference>) {
        conference.join(self.shared.call_id);
        *self.shared.conference.lock() = Some(conference);
    }

    pub fn leave_conference(&self) {
        if let Some(c) = self.shared.conference.lock().take() {
            c.leave(self.shared.call_id);
        }
    }

    pub fn stats(&self) -> MediaStats {
        let sh = &self.shared;
        let (js, rate, pt, srtp_rx) = {
            let rx = sh.rx.lock();
            let rate = rx.codec.as_ref().map_or(8000, |c| c.sample_rate());
            (rx.jitter.stats(), rate, rx.payload_type, rx.srtp.is_some())
        };
        let (queued_ms, srtp_tx) = {
            let tx = sh.tx.lock();
            ((tx.pending.len() as u64 * 1000 / tx.codec_rate.max(1) as u64) as u32, tx.srtp.is_some())
        };
        let buffer_ms = js.target_depth as u32 * sh.config.ptime_ms;
        MediaStats {
            packets_sent: sh.packets_sent.load(Ordering::Relaxed),
            packets_received: sh.packets_received.load(Ordering::Relaxed),
            bytes_sent: sh.bytes_sent.load(Ordering::Relaxed),
            bytes_received: sh.bytes_received.load(Ordering::Relaxed),
            packets_lost: js.lost,
            packets_late: js.late,
            jitter_ms: js.jitter_ms,
            jitter_buffer_ms: buffer_ms,
            payload_type: pt as u32,
            sample_rate: rate,
            mos: estimate_mos(&js, buffer_ms, pt),
            srtp_active: u8::from(srtp_rx && srtp_tx),
            ice_connected: u8::from(sh.ice.lock().connected),
            outbound_queued_ms: queued_ms,
        }
    }

    pub fn call_id(&self) -> u64 {
        self.shared.call_id
    }
}

impl Drop for MediaSession {
    fn drop(&mut self) {
        self.stop();
    }
}

/// ITU-T G.107 simplified E-model MOS estimate.
fn estimate_mos(js: &JitterStats, buffer_ms: u32, pt: u8) -> f64 {
    let total = js.received + js.lost;
    if total == 0 {
        return 0.0;
    }
    let ppl = js.lost as f64 * 100.0 / total as f64;
    let (ie, bpl) = match pt {
        0 | 8 => (0.0, 25.1),
        9 => (0.0, 20.0),
        18 => (11.0, 19.0),
        _ => (5.0, 20.0),
    };
    let ie_eff = ie + (95.0 - ie) * ppl / (ppl + bpl);
    let d = buffer_ms as f64 + 20.0 + js.jitter_ms;
    let id = 0.024 * d + if d > 177.3 { 0.11 * (d - 177.3) } else { 0.0 };
    let r = (93.2 - id - ie_eff).clamp(0.0, 100.0);
    (1.0 + 0.035 * r + 7.0e-6 * r * (r - 60.0) * (100.0 - r)).clamp(1.0, 4.5)
}

fn bind_in_range(ip: IpAddr, min: u16, max: u16) -> std::io::Result<UdpSocket> {
    let (min, max) = (min.min(max), min.max(max));
    let span = (max - min) as u32 + 1;
    let start: u32 = rand::random::<u32>() % span;
    for i in 0..span.min(400) {
        let mut port = min as u32 + (start + i * 2) % span;
        port &= !1; // prefer even ports (RTCP convention)
        if port < min as u32 {
            continue;
        }
        if let Ok(s) = UdpSocket::bind(SocketAddr::new(ip, port as u16)) {
            return Ok(s);
        }
    }
    UdpSocket::bind(SocketAddr::new(ip, 0))
}

fn send_raw(sh: &Shared, data: &[u8]) {
    let Some(remote) = *sh.remote.lock() else { return };
    if let Some(relay) = sh.relay.lock().as_ref() {
        let _ = sh.socket.send_to(&relay.wrap(remote, data), relay.server);
    } else {
        let _ = sh.socket.send_to(data, remote);
    }
}

fn receive_loop(sh: &Arc<Shared>) {
    let mut buf = vec![0u8; 2048];
    while sh.running.load(Ordering::Relaxed) {
        let (n, from) = match sh.socket.recv_from(&mut buf) {
            Ok(v) => v,
            Err(ref e) if matches!(e.kind(), std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut) => continue,
            // Windows reports ICMP port unreachable as ConnectionReset on UDP sockets.
            Err(ref e) if e.kind() == std::io::ErrorKind::ConnectionReset => continue,
            Err(_) => {
                std::thread::sleep(Duration::from_millis(5));
                continue;
            }
        };
        let relay_server = sh.relay.lock().as_ref().map(|r| r.server);
        if Some(from) == relay_server {
            if let Some((peer, inner)) = TurnAllocation::unwrap(&buf[..n]) {
                handle_datagram(sh, &inner, peer);
            }
            continue;
        }
        let data = buf[..n].to_vec();
        handle_datagram(sh, &data, from);
    }
}

fn handle_datagram(sh: &Arc<Shared>, data: &[u8], from: SocketAddr) {
    match classify(data) {
        PacketClass::Stun => handle_stun(sh, data, from),
        PacketClass::Rtp => handle_rtp(sh, data, from),
        PacketClass::Rtcp => {
            let mut rx = sh.rx.lock();
            if let Some(ctx) = rx.srtp.as_mut() {
                let mut copy = data.to_vec();
                let _ = ctx.unprotect_rtcp(&mut copy);
            }
        }
        PacketClass::Dtls | PacketClass::Unknown => {}
    }
}

fn handle_stun(sh: &Arc<Shared>, data: &[u8], from: SocketAddr) {
    let Some(msg) = StunMessage::decode(data) else { return };
    match msg.msg_type {
        stun::BINDING_REQUEST => {
            let pwd = sh.ice.lock().local_pwd.clone();
            let has_integrity = msg.get(stun::ATTR_MESSAGE_INTEGRITY).is_some();
            if has_integrity && !StunMessage::verify_integrity(data, pwd.as_bytes()) {
                return;
            }
            let mut resp = msg.reply(stun::BINDING_SUCCESS);
            resp.add_xor_address(stun::ATTR_XOR_MAPPED_ADDRESS, from);
            let raw = resp.encode(has_integrity.then_some(pwd.as_bytes()), true);
            let _ = sh.socket.send_to(&raw, from);
            if has_integrity {
                mark_ice_connected(sh, from);
            }
        }
        stun::BINDING_SUCCESS => {
            let remote_pwd = sh.ice.lock().remote_pwd.clone();
            if remote_pwd.is_none_or(|p| StunMessage::verify_integrity(data, p.as_bytes())) {
                mark_ice_connected(sh, from);
            }
        }
        _ => {}
    }
}

fn mark_ice_connected(sh: &Arc<Shared>, from: SocketAddr) {
    let newly = {
        let mut ice = sh.ice.lock();
        let newly = !ice.connected;
        ice.connected = true;
        newly
    };
    *sh.remote.lock() = Some(from);
    if newly {
        sh.sink.on_media_event(sh.call_id, "ice-connected", &from.to_string());
    }
}

fn handle_rtp(sh: &Arc<Shared>, data: &[u8], from: SocketAddr) {
    // Sink callbacks are deferred until the rx lock is released so handlers may call back into the session.
    let mut dtmf_digit = None;
    let mut passthrough: Option<(RtpHeader, Vec<u8>)> = None;
    {
        let mut rx = sh.rx.lock();
        let rx = &mut *rx;
        rx.buf.clear();
        rx.buf.extend_from_slice(data);
        if let Some(ctx) = rx.srtp.as_mut() {
            if ctx.unprotect_rtp(&mut rx.buf).is_err() {
                return;
            }
        }
        let Some(pkt) = RtpPacketRef::parse(&rx.buf) else { return };

        if sh.config.symmetric_rtp {
            let mut remote = sh.remote.lock();
            if *remote != Some(from) && !sh.latched.swap(true, Ordering::Relaxed) {
                *remote = Some(from);
            }
        }
        sh.packets_received.fetch_add(1, Ordering::Relaxed);
        sh.bytes_received.fetch_add(pkt.payload.len() as u64, Ordering::Relaxed);
        *sh.last_rx.lock() = Instant::now();
        sh.timeout_reported.store(false, Ordering::Relaxed);

        let h = pkt.header;
        if Some(h.payload_type) == rx.dtmf_pt {
            if let Some(ev) = TelephoneEvent::decode(pkt.payload) {
                // Report each event once, keyed by its RTP timestamp.
                if rx.last_event_ts != Some(h.timestamp) {
                    rx.last_event_ts = Some(h.timestamp);
                    dtmf_digit = dtmf::event_to_digit(ev.event);
                }
            }
        } else if h.payload_type == 13 {
            // comfort noise
        } else if h.payload_type == rx.payload_type && rx.codec.is_some() {
            let payload = pkt.payload;
            rx.jitter.push(h.sequence, h.timestamp, h.marker, h.payload_type, payload);
            rx.started = true;
        } else {
            passthrough = Some((h, pkt.payload.to_vec()));
        }
    }
    if let Some(d) = dtmf_digit {
        sh.sink.on_dtmf(sh.call_id, d, DtmfSource::Rfc4733);
    }
    if let Some((h, payload)) = passthrough {
        sh.sink.on_encoded(sh.call_id, h.payload_type, h.timestamp, h.marker, &payload);
    }
}

fn playout_loop(sh: &Arc<Shared>) {
    let ptime = Duration::from_millis(sh.config.ptime_ms as u64);
    let mut next = Instant::now() + ptime;
    let mut last_sr = Instant::now();
    let mut last_refresh = Instant::now();
    let mut decoded: Vec<i16> = Vec::with_capacity(960);
    let mut conf_buf: Vec<i16> = Vec::with_capacity(960);
    let mut conf_mix: Vec<i16> = Vec::with_capacity(960);
    let mut inband_digits: Vec<char> = Vec::new();
    let mut outbound_tap: Vec<i16> = Vec::with_capacity(960);

    while sh.running.load(Ordering::Relaxed) {
        let now = Instant::now();
        if next > now {
            std::thread::sleep(next - now);
        } else if now - next > ptime * 5 {
            next = now; // we fell far behind (suspend/debugger) — resynchronize
        }
        next += ptime;

        let direction = direction_from(sh.direction.load(Ordering::Relaxed));

        // ---- Receive path ----
        decoded.clear();
        let mut rate = 8000;
        {
            let mut rx = sh.rx.lock();
            let rx = &mut *rx;
            if rx.started {
                let samples = rx.codec.as_ref().map_or(160, |c| (c.sample_rate() * sh.config.ptime_ms / 1000) as usize);
                rate = rx.codec.as_ref().map_or(8000, |c| c.sample_rate());
                match rx.jitter.pop() {
                    Playout::Frame { payload, .. } => {
                        if let Some(codec) = rx.codec.as_mut() {
                            codec.decode(&payload, &mut decoded);
                        }
                        rx.plc.remember(&decoded);
                        rx.jitter.give_back(payload);
                    }
                    Playout::Lost => rx.plc.conceal(samples, &mut decoded),
                    Playout::Empty => decoded.resize(samples, 0),
                }
                if let Some(det) = rx.detector.as_mut() {
                    det.process(&decoded, &mut inband_digits);
                }
            }
        }
        for d in inband_digits.drain(..) {
            sh.sink.on_dtmf(sh.call_id, d, DtmfSource::InBand);
        }
        let conference = sh.conference.lock().clone();
        if !decoded.is_empty() && direction.can_receive() {
            sh.sink.on_audio(sh.call_id, AudioDirection::Inbound, rate, &decoded);
            if let Some(conf) = &conference {
                let mut tx = sh.tx.lock();
                let r = tx.conf_resampler_in.get_or_insert_with(|| Resampler::new(rate, CONFERENCE_RATE));
                conf_buf.clear();
                r.process(&decoded, &mut conf_buf);
                drop(tx);
                conf.contribute(sh.call_id, &conf_buf);
            }
        }

        // ---- ICE connectivity checks ----
        run_ice_checks(sh);

        // ---- Transmit path ----
        if let Some(conf) = &conference {
            conf_mix.clear();
            conf.mix_for(sh.call_id, (CONFERENCE_RATE * sh.config.ptime_ms / 1000) as usize, &mut conf_mix);
            let mut tx = sh.tx.lock();
            let tx = &mut *tx;
            let codec_rate = tx.codec_rate;
            let r = tx.conf_resampler_out.get_or_insert_with(|| Resampler::new(CONFERENCE_RATE, codec_rate));
            tx.resample_buf.clear();
            r.process(&conf_mix, &mut tx.resample_buf);
            if tx.pending.len() < tx.samples_per_frame {
                tx.pending.extend(tx.resample_buf.iter().copied());
            }
        }
        outbound_tap.clear();
        let tap_rate = transmit_frame(sh, direction, &mut outbound_tap);
        if !outbound_tap.is_empty() {
            sh.sink.on_audio(sh.call_id, AudioDirection::Outbound, tap_rate, &outbound_tap);
        }

        // ---- RTCP / TURN / timeouts ----
        if last_sr.elapsed() >= Duration::from_secs(5) {
            last_sr = Instant::now();
            send_sender_report(sh);
        }
        if last_refresh.elapsed() >= Duration::from_secs(240) {
            last_refresh = Instant::now();
            if let Some(relay) = sh.relay.lock().as_ref() {
                let _ = sh.socket.send_to(&relay.refresh(600), relay.server);
            }
        }
        if sh.config.rtp_timeout_ms > 0
            && sh.last_rx.lock().elapsed() > Duration::from_millis(sh.config.rtp_timeout_ms as u64)
            && direction.can_receive()
            && !sh.timeout_reported.swap(true, Ordering::Relaxed)
        {
            sh.sink.on_media_event(sh.call_id, "rtp-timeout", "no RTP received");
        }
    }
}

fn run_ice_checks(sh: &Arc<Shared>) {
    let (targets, username, pwd) = {
        let mut ice = sh.ice.lock();
        let (Some(ru), Some(rp)) = (ice.remote_ufrag.clone(), ice.remote_pwd.clone()) else { return };
        if ice.connected || ice.last_check.elapsed() < Duration::from_millis(200) {
            return;
        }
        ice.last_check = Instant::now();
        let targets: Vec<SocketAddr> = ice
            .candidates
            .iter()
            .filter(|c| c.kind != CandidateKind::Relayed || c.component == 1)
            .map(|c| c.address)
            .filter(|a| Some(*a) != sh.socket.local_addr().ok())
            .collect();
        (targets, format!("{ru}:{}", ice.local_ufrag), rp)
    };
    for target in targets {
        let mut req = StunMessage::new(stun::BINDING_REQUEST);
        req.add(stun::ATTR_USERNAME, username.as_bytes().to_vec())
            .add(stun::ATTR_PRIORITY, (110u32 << 24).to_be_bytes().to_vec())
            .add(stun::ATTR_ICE_CONTROLLED, rand::random::<u64>().to_be_bytes().to_vec());
        let _ = sh.socket.send_to(&req.encode(Some(pwd.as_bytes()), true), target);
    }
}

/// Sends at most one frame. Copies the transmitted PCM into `tap` and returns its sample rate.
fn transmit_frame(sh: &Arc<Shared>, direction: Direction, tap: &mut Vec<i16>) -> u32 {
    let mut tx = sh.tx.lock();
    let tx = &mut *tx;
    let ts_step = tx.ts_per_frame;

    // RFC 4733 events take precedence over audio.
    if tx.dtmf_active.is_none() {
        if let (Some((digit, dur)), Some(_)) = (tx.dtmf_queue.pop_front(), tx.dtmf_pt) {
            tx.dtmf_active = Some(ActiveDtmf {
                event: dtmf::digit_to_event(digit).unwrap_or(0),
                timestamp: tx.timestamp,
                elapsed: 0,
                total: dur * tx.ts_per_frame / sh.config.ptime_ms,
                end_sent: 0,
                gap_frames: 0,
            });
        }
    }
    if let (Some(active), Some(pt)) = (tx.dtmf_active.as_mut(), tx.dtmf_pt) {
        let mut done = false;
        let mut payload: Option<([u8; 4], bool)> = None;
        if active.elapsed < active.total {
            let first = active.elapsed == 0;
            active.elapsed = (active.elapsed + ts_step).min(active.total);
            let end = active.elapsed >= active.total;
            let ev = TelephoneEvent { event: active.event, end, volume: 10, duration: active.elapsed.min(0xFFFF) as u16 };
            payload = Some((ev.encode(), first));
            if end {
                active.end_sent = 1;
            }
        } else if active.end_sent < 3 {
            let ev = TelephoneEvent { event: active.event, end: true, volume: 10, duration: active.total.min(0xFFFF) as u16 };
            payload = Some((ev.encode(), false));
            active.end_sent += 1;
        } else {
            active.gap_frames += 1;
            done = active.gap_frames >= 2;
        }
        let event_ts = active.timestamp;
        if done {
            tx.dtmf_active = None;
        }
        if let Some((p, marker)) = payload {
            if direction.can_send() {
                write_and_send(sh, tx, pt, event_ts, marker, &p);
            }
        }
        tx.timestamp = tx.timestamp.wrapping_add(ts_step);
        let drop_samples = tx.samples_per_frame.min(tx.pending.len());
        tx.pending.drain(..drop_samples);
        return tx.codec_rate;
    }

    let spf = tx.samples_per_frame;
    tx.frame.clear();
    if tx.pending.len() >= spf {
        tx.frame.extend(tx.pending.drain(..spf));
    } else if !tx.pending.is_empty() {
        // Pad a partial final frame with silence.
        tx.frame.extend(tx.pending.drain(..));
        tx.frame.resize(spf, 0);
    }

    let has_audio = !tx.frame.is_empty();
    let muted = sh.muted.load(Ordering::Relaxed);
    let timestamp = tx.timestamp;
    tx.timestamp = tx.timestamp.wrapping_add(ts_step);

    if !has_audio || !direction.can_send() || tx.codec.is_none() {
        tx.was_silent = true;
        return tx.codec_rate;
    }
    if muted {
        tx.frame.iter_mut().for_each(|s| *s = 0);
    }
    tap.extend_from_slice(&tx.frame);

    tx.encoded.clear();
    let (frame, encoded, codec) = (&tx.frame, &mut tx.encoded, tx.codec.as_mut().expect("checked above"));
    codec.encode(frame, encoded);
    let marker = tx.was_silent;
    tx.was_silent = false;
    let pt = tx.payload_type;
    let payload = std::mem::take(&mut tx.encoded);
    write_and_send(sh, tx, pt, timestamp, marker, &payload);
    tx.encoded = payload;
    tx.codec_rate
}

fn write_and_send(sh: &Shared, tx: &mut TxState, pt: u8, timestamp: u32, marker: bool, payload: &[u8]) {
    tx.packet.clear();
    RtpHeader { marker, payload_type: pt, sequence: tx.sequence, timestamp, ssrc: tx.ssrc }.write(payload, &mut tx.packet);
    tx.sequence = tx.sequence.wrapping_add(1);
    if let Some(ctx) = tx.srtp.as_mut() {
        if ctx.protect_rtp(&mut tx.packet).is_err() {
            return;
        }
    }
    send_raw(sh, &tx.packet);
    tx.packets = tx.packets.wrapping_add(1);
    tx.octets = tx.octets.wrapping_add(payload.len() as u32);
    sh.packets_sent.fetch_add(1, Ordering::Relaxed);
    sh.bytes_sent.fetch_add(payload.len() as u64, Ordering::Relaxed);
}

fn send_sender_report(sh: &Arc<Shared>) {
    let mut tx = sh.tx.lock();
    let since_epoch = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default();
    let ntp_secs = since_epoch.as_secs() + 2_208_988_800;
    let ntp_frac = ((since_epoch.subsec_nanos() as u64) << 32) / 1_000_000_000;
    let ntp = (ntp_secs << 32) | ntp_frac;
    let mut sr = build_sender_report(tx.ssrc, ntp, tx.timestamp, tx.packets, tx.octets, "voipnet");
    if let Some(ctx) = tx.srtp.as_mut() {
        if ctx.protect_rtcp(&mut sr).is_err() {
            return;
        }
    }
    drop(tx);
    send_raw(sh, &sr);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::codec::CodecKind;
    use std::sync::atomic::AtomicUsize;

    #[derive(Default)]
    struct Collect {
        inbound: Mutex<Vec<i16>>,
        dtmf: Mutex<Vec<char>>,
        frames: AtomicUsize,
    }

    impl MediaSink for Collect {
        fn on_audio(&self, _: u64, direction: AudioDirection, _: u32, pcm: &[i16]) {
            if direction == AudioDirection::Inbound {
                self.inbound.lock().extend_from_slice(pcm);
                self.frames.fetch_add(1, Ordering::Relaxed);
            }
        }
        fn on_dtmf(&self, _: u64, digit: char, _: DtmfSource) {
            self.dtmf.lock().push(digit);
        }
        fn on_encoded(&self, _: u64, _: u8, _: u32, _: bool, _: &[u8]) {}
        fn on_media_event(&self, _: u64, _: &str, _: &str) {}
    }

    fn loopback_config() -> MediaConfig {
        MediaConfig { local_ip: "127.0.0.1".parse().unwrap(), jitter_min_ms: 20, ..Default::default() }
    }

    fn connect(kind: CodecKind, srtp: bool) -> (Arc<MediaSession>, Arc<MediaSession>, Arc<Collect>, Arc<Collect>) {
        let (ca, cb) = (Arc::new(Collect::default()), Arc::new(Collect::default()));
        let a = MediaSession::new(1, loopback_config(), ca.clone()).unwrap();
        let b = MediaSession::new(2, loopback_config(), cb.clone()).unwrap();
        let (ka, kb) = if srtp { (Some(a.enable_srtp()), Some(b.enable_srtp())) } else { (None, None) };
        let neg = |remote: SocketAddr, key: Option<String>| NegotiatedMedia {
            remote: Some(remote),
            codec: kind.rtpmap(),
            dtmf: Some(CodecKind::TelephoneEvent.rtpmap()),
            direction: Direction::SendRecv,
            remote_srtp_key: key,
            remote_ice_ufrag: None,
            remote_ice_pwd: None,
            remote_candidates: vec![],
            ptime_ms: None,
        };
        a.apply(&neg(b.local_address(), kb)).unwrap();
        b.apply(&neg(a.local_address(), ka)).unwrap();
        (a, b, ca, cb)
    }

    fn tone(rate: u32, ms: u32) -> Vec<i16> {
        (0..rate * ms / 1000).map(|i| (8000.0 * (i as f64 * 2.0 * std::f64::consts::PI * 440.0 / rate as f64).sin()) as i16).collect()
    }

    #[test]
    fn audio_flows_over_loopback_with_srtp_and_g722() {
        let (a, b, _ca, cb) = connect(CodecKind::G722, true);
        a.send_audio(&tone(16000, 600), 16000);
        std::thread::sleep(Duration::from_millis(1000));
        let got = cb.inbound.lock().clone();
        let energy: f64 = got.iter().map(|&s| (s as f64).powi(2)).sum::<f64>() / got.len().max(1) as f64;
        assert!(energy.sqrt() > 1000.0, "rms too low: {}", energy.sqrt());
        let stats = b.stats();
        assert!(stats.packets_received >= 25, "{stats:?}");
        assert_eq!(stats.srtp_active, 1);
        assert_eq!(stats.sample_rate, 16000);
        a.stop();
        b.stop();
    }

    #[test]
    fn rfc4733_dtmf_is_delivered_once_per_digit() {
        let (a, b, _ca, cb) = connect(CodecKind::Pcmu, false);
        a.send_dtmf("12#", 80);
        std::thread::sleep(Duration::from_millis(1200));
        assert_eq!(cb.dtmf.lock().iter().collect::<String>(), "12#");
        a.stop();
        b.stop();
    }

    #[test]
    fn resamples_input_and_reports_queue() {
        let (a, b, _ca, _cb) = connect(CodecKind::Pcmu, false);
        let queued = a.send_audio(&tone(16000, 1000), 16000);
        assert!((990..=1010).contains(&queued), "queued {queued}");
        a.clear_audio();
        assert_eq!(a.stats().outbound_queued_ms, 0);
        a.stop();
        b.stop();
    }
}
