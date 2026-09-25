//! Per-call RTP media session: socket I/O, SRTP, ICE checks, jitter buffer,
//! decoding with concealment, paced transmission, DTMF and conferencing.

use std::collections::VecDeque;
use std::net::{IpAddr, SocketAddr, UdpSocket};
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, AtomicU8, Ordering};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use parking_lot::Mutex;

use super::conference::{Conference, CONFERENCE_RATE};
use super::dtls::{DtlsEvent, DtlsIdentity, DtlsRole, DtlsTransport};
use super::sctp::{SctpAssociation, SctpEvent};
use super::zrtp::{ZrtpEvent, ZrtpSession};
#[cfg(feature = "audio-processing")]
use super::enhance::AudioEnhancer;
use super::ice::{IceAgent, IceOutput, IceRole};
use super::resample::Resampler;
use crate::codec::dtmf::{self, DtmfDetector, TelephoneEvent};
use crate::codec::{create_audio_codec, AudioCodec, Concealer};
use crate::rtp::jitter::{JitterBuffer, JitterStats, Playout};
use crate::rtp::quality::BurstGapTracker;
use crate::rtp::video::{VideoDepacketizer, VideoFormat, VideoFrame, VideoPacketizer};
use crate::rtp::packet::{
    build_bye, build_keyframe_request, build_receiver_estimate, build_receiver_report, build_sender_report, build_xr_voip_metrics, classify, parse_rtcp,
    PacketClass, ReportBlock, RtcpPacket,
    RtpHeader, RtpPacketRef, VoipMetrics,
};
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
    /// A complete video frame arrived (H.264 access unit in Annex B form, or a VP8 frame). `content`
    /// says what the stream shows: `main` for a camera, `slides` for a shared screen.
    fn on_video_frame(&self, _call_id: u64, _timestamp: u32, _keyframe: bool, _frame: &[u8], _content: &str) {}
    /// A data channel message arrived on `stream` (RFC 8831). `text` says whether it is UTF-8.
    fn on_data_message(&self, _call_id: u64, _stream: u16, _text: bool, _data: &[u8]) {}
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
    /// ICE decides the media path. Without it the reflexive address goes straight into the SDP, so it
    /// has to be known before the offer; with it the address is just another candidate and can trickle.
    pub ice: bool,
    pub turn_server: Option<SocketAddr>,
    pub turn_username: String,
    pub turn_password: String,
    pub detect_inband_dtmf: bool,
    pub rtp_timeout_ms: u32,
    pub symmetric_rtp: bool,
    /// Certificate for DTLS-SRTP; required to offer or accept DTLS keying.
    pub dtls_identity: Option<Arc<DtlsIdentity>>,
    /// Let Opus stop sending during silence (RFC 6716 discontinuous transmission).
    pub opus_dtx: bool,
    /// Remove the echo of the audio this call plays out from the audio the application sends.
    pub echo_cancellation: bool,
    /// Round trip through the audio device in milliseconds, for the echo canceller; 0 means unknown.
    pub stream_delay_ms: u32,
    /// Suppress steady background noise in the audio the application sends.
    pub noise_suppression: bool,
    /// Even out the level of the audio the application sends.
    pub auto_gain: bool,
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
            ice: false,
            turn_server: None,
            turn_username: String::new(),
            turn_password: String::new(),
            detect_inband_dtmf: false,
            rtp_timeout_ms: 30_000,
            symmetric_rtp: true,
            dtls_identity: None,
            opus_dtx: false,
            echo_cancellation: false,
            stream_delay_ms: 0,
            noise_suppression: false,
            auto_gain: false,
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
    /// This side is the ICE controlling agent (it sent the offer).
    pub ice_controlling: bool,
    pub ptime_ms: Option<u32>,
    /// `a=fingerprint` of the peer when DTLS-SRTP keying was negotiated.
    pub remote_fingerprint: Option<String>,
    /// Local DTLS role derived from `a=setup`.
    pub dtls_role: Option<DtlsRole>,
}

/// The peer's view of the stream we send, taken from its RTCP reports.
#[derive(Debug, Clone, Copy, Default)]
struct RemoteQuality {
    loss_percent: f64,
    jitter_ms: f64,
    rtt_ms: f64,
    /// MOS the peer reports for what it hears (RFC 3611 VoIP metrics), or zero.
    mos: f64,
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
    /// Loss the peer reports on the stream we send, in percent (RTCP report blocks).
    pub remote_loss_percent: f64,
    /// Jitter the peer reports, in milliseconds.
    pub remote_jitter_ms: f64,
    /// Round-trip time from RTCP, in milliseconds; zero until the peer reports.
    pub round_trip_ms: f64,
    /// MOS the peer reports for the audio it receives (RTCP XR); zero when it sends no extended reports.
    pub remote_mos: f64,
    /// What the peer says it can receive, in bits per second (RTCP REMB); zero when it never says.
    /// An application that encodes video should keep its bitrate under this.
    pub remote_estimate_bps: u64,
}

/// Receive-side estimate of what this side can take, from loss and the rate actually arriving.
///
/// The rule is the loss-based half of Google congestion control: grow slowly while the stream is
/// clean, hold through moderate loss, and cut hard when loss is heavy. It is reported to the sender
/// as REMB, and a browser obeys it by lowering the bitrate it encodes at.
struct BandwidthEstimator {
    bits_per_second: f64,
    since: Instant,
    bytes: u64,
}

/// Where an estimate starts before anything has been measured.
const INITIAL_ESTIMATE: f64 = 600_000.0;
const MIN_ESTIMATE: f64 = 64_000.0;
const MAX_ESTIMATE: f64 = 8_000_000.0;

impl BandwidthEstimator {
    fn new() -> Self {
        Self { bits_per_second: INITIAL_ESTIMATE, since: Instant::now(), bytes: 0 }
    }

    /// Folds in what arrived since the last call and returns the estimate in bits per second.
    /// `loss` is the share of packets lost in that window, from 0 to 1.
    fn update(&mut self, now: Instant, bytes_received: u64, loss: f64) -> u64 {
        let elapsed = now.duration_since(self.since).as_secs_f64();
        if elapsed < 0.2 {
            return self.bits_per_second as u64;
        }

        let measured = (bytes_received.saturating_sub(self.bytes) as f64) * 8.0 / elapsed;
        self.since = now;
        self.bytes = bytes_received;
        self.bits_per_second = match loss {
            // Heavy loss: back off in proportion to it, as the sender is clearly sending too much.
            l if l > 0.10 => self.bits_per_second * (1.0 - 0.5 * l),
            // A little loss is normal on any path; hold the estimate steady.
            l if l >= 0.02 => self.bits_per_second,
            // Clean: allow a little more than is arriving, so the sender can find the ceiling.
            _ => (self.bits_per_second * 1.08).max(measured * 1.08),
        };
        // Never invite far more than the sender is actually using: the number is a limit, not a target.
        let ceiling = (measured * 1.5 + MIN_ESTIMATE).max(INITIAL_ESTIMATE);
        self.bits_per_second = self.bits_per_second.clamp(MIN_ESTIMATE, ceiling.min(MAX_ESTIMATE));
        self.bits_per_second as u64
    }
}

/// A reflexive address being looked up while the call sets up.
struct Gathering {
    server: SocketAddr,
    transaction: [u8; 12],
    sent: Instant,
    attempts: u8,
}

/// A session in video mode packetizes whole frames instead of encoding audio.
/// One encoding of a simulcast stream: the same picture at its own size and bitrate.
struct Layer {
    depacketizer: VideoDepacketizer,
    /// Bytes seen since the measurement started, and when that was.
    bytes: u64,
    since: Instant,
    /// What the last full second of this encoding worked out at, in bits per second.
    bitrate: u64,
    /// When a packet of this encoding last arrived: a sender may stop one at any time.
    last_packet: Instant,
    /// The stream this encoding arrives on. A browser labels its packets only until it knows the
    /// labels have been understood, and then sends the stream unlabelled — so after the first few
    /// seconds the synchronisation source is the only thing that says which encoding a packet is.
    ssrc: Option<u32>,
}

impl Layer {
    fn new(format: VideoFormat) -> Self {
        let now = Instant::now();
        Self { depacketizer: VideoDepacketizer::new(format), bytes: 0, since: now, bitrate: 0, last_packet: now, ssrc: None }
    }

    /// Folds in one packet and returns the measured rate once a second has gone by.
    fn measure(&mut self, bytes: usize, now: Instant) {
        self.bytes += bytes as u64;
        self.last_packet = now;
        let elapsed = now.duration_since(self.since);
        if elapsed >= Duration::from_secs(1) {
            self.bitrate = (self.bytes as f64 * 8.0 / elapsed.as_secs_f64()) as u64;
            self.bytes = 0;
            self.since = now;
        }
    }
}

struct VideoTrack {
    payload_type: u8,
    /// What this stream shows (RFC 4796 `a=content`), carried through to the sink.
    content: String,
    packetizer: VideoPacketizer,
    depacketizer: VideoDepacketizer,
    /// The picture format, so another encoding can be put back together the same way.
    format: VideoFormat,
    /// The header extension that names the encoding a packet belongs to (RFC 8852), when the peer
    /// said it would send several.
    rid_extension: Option<u8>,
    /// The same extension on the way out, with the encodings the peer agreed to receive.
    send_rid_extension: Option<u8>,
    send_rids: Vec<String>,
    /// One reassembler per encoding, in the order the peer offered them.
    layers: Vec<(String, Layer)>,
    /// Which encoding is being handed on; the others are dropped as they arrive.
    selected: Option<String>,
    /// When that last changed, so a noisy estimate cannot flip the picture back and forth.
    layer_changed_at: Option<Instant>,
    /// Incomplete frames already reported, so only new losses ask for a keyframe.
    incomplete_seen: u64,
    /// Whole frames handed to the application, and what was already counted towards an estimate.
    frames: u64,
    reported_frames: u64,
    reported_incomplete: u64,
    last_request: Option<Instant>,
    /// How long to wait before asking again. It doubles while the asking does not help, so a stream
    /// that is losing packets is not buried under keyframes it cannot receive either.
    request_backoff: Duration,
    /// Sequence number carried by Full Intra Requests (RFC 5104) so repeats can be told apart.
    fir_sequence: u8,
}

impl VideoTrack {
    /// Puts one packet into the right reassembler.
    ///
    /// Returns the finished frame when the packet completed one *and* it belongs to the encoding
    /// being forwarded, and whether packets went missing, which is worth a keyframe request.
    fn push(&mut self, rid: Option<&str>, header: &RtpHeader, payload: &[u8], now: Instant) -> (Option<VideoFrame>, bool) {
        // A simulcast sender labels every packet with its encoding, and each one is a stream of its
        // own: its own sequence numbers, its own frames, its own bitrate. The label stops once the
        // sender is satisfied it was received, so an unlabelled packet is looked up by its stream.
        let format = self.format;
        let index = match rid {
            Some(rid) => Some(match self.layers.iter().position(|(name, _)| name == rid) {
                Some(index) => index,
                None => {
                    self.layers.push((rid.to_owned(), Layer::new(format)));
                    self.layers.len() - 1
                }
            }),
            None => self.layers.iter().position(|(_, layer)| layer.ssrc == Some(header.ssrc)),
        };

        let Some(index) = index else {
            // Not a simulcast stream at all: one encoding, one reassembler.
            let frame = self.depacketizer.push(header.sequence, header.timestamp, header.marker, payload);
            let lost = self.depacketizer.incomplete_frames > self.incomplete_seen;
            self.incomplete_seen = self.depacketizer.incomplete_frames;
            return (frame, lost);
        };

        let rid = self.layers[index].0.clone();
        let selected = self.selected.get_or_insert_with(|| rid.clone()).clone();
        let layer = &mut self.layers[index].1;
        layer.ssrc = Some(header.ssrc);
        layer.measure(payload.len(), now);
        let frame = layer.depacketizer.push(header.sequence, header.timestamp, header.marker, payload);
        // Only the chosen encoding is watched for loss: the others are dropped on purpose.
        if rid != selected {
            return (None, false);
        }

        let lost = layer.depacketizer.incomplete_frames > self.incomplete_seen;
        self.incomplete_seen = layer.depacketizer.incomplete_frames;
        (frame, lost)
    }

    /// The encodings seen so far, with what each is measured at, in bits per second.
    fn measured(&self) -> Vec<(String, u64)> {
        self.layers.iter().map(|(name, layer)| (name.clone(), layer.bitrate)).collect()
    }
}

/// Losses come in bursts; one request per interval is enough to get a fresh keyframe.
const KEYFRAME_REQUEST_INTERVAL: Duration = Duration::from_millis(500);

/// How far apart the asking is allowed to get while it keeps not working.
const KEYFRAME_REQUEST_MAX: Duration = Duration::from_secs(4);

/// How long an encoding is kept before another may be chosen. Every change costs a keyframe and a
/// moment of still picture, so changing often is worse than staying on a size that nearly fits.
const LAYER_DWELL: Duration = Duration::from_secs(3);

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
    /// When `timestamp` was last set, so a sender report can say what it is *now*.
    timestamp_at: Instant,
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
    /// SSRC of the stream we receive, and what the previous report about it covered.
    remote_ssrc: Option<u32>,
    reported_expected: u64,
    reported_received: u64,
    /// Middle 32 bits of the last sender report's NTP time, and when it arrived.
    last_sr: Option<(u32, Instant)>,
    /// The last sender report's two clocks: NTP time and the RTP timestamp of that same instant.
    sync: Option<(u64, u32)>,
    /// Timestamp of the frame being played out now, which is the moment video should line up with.
    playing: Option<u32>,
    /// How losses cluster, for the RTCP XR burst and gap metrics.
    burst_gap: BurstGapTracker,
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
    ice: Mutex<IceAgent>,
    /// The host address this session advertises, used to ignore a reflexive answer that matches it.
    local_address: SocketAddr,
    /// Candidates known so far. The reflexive one arrives later, so this grows during a call.
    local_candidates: Mutex<Vec<Candidate>>,
    /// The outstanding reflexive probe: the server, the transaction it belongs to, and when it went out.
    gathering: Mutex<Option<Gathering>>,
    relay: Mutex<Option<TurnAllocation>>,
    /// Media goes through the TURN relay (always when allocated without ICE; per the selected pair with ICE).
    via_relay: AtomicBool,
    relay_permissions: Mutex<Vec<IpAddr>>,
    conference: Mutex<Option<Arc<Conference>>>,
    /// Set when this session carries video rather than audio.
    video: Mutex<Option<VideoTrack>>,
    sink: Arc<dyn MediaSink>,
    config: MediaConfig,
    packets_sent: AtomicU64,
    packets_received: AtomicU64,
    bytes_sent: AtomicU64,
    bytes_received: AtomicU64,
    last_rx: Mutex<Instant>,
    timeout_reported: AtomicBool,
    /// What the peer reports about the stream we send (RFC 3550 receiver reports).
    remote_quality: Mutex<RemoteQuality>,
    /// Echo cancellation, noise suppression and gain control, when enabled.
    #[cfg(feature = "audio-processing")]
    enhancer: Mutex<Option<AudioEnhancer>>,
    /// Device round trip the application told us about, kept here so it survives a codec change.
    stream_delay_ms: AtomicU32,
    dtls: Mutex<Option<DtlsTransport>>,
    /// A client-role handshake waiting for ICE to find a path.
    dtls_start_pending: AtomicBool,
    /// Media must be encrypted (DTLS-SRTP): never send or accept plain RTP while keys are pending.
    secure_required: AtomicBool,
    /// When this stream sent its first forwarded frame, so relayed video keeps one rising clock.
    video_epoch: Mutex<Option<Instant>>,
    /// What this side can receive, reported to the peer as REMB.
    estimator: Mutex<BandwidthEstimator>,
    /// What the peer last said it can receive, in bits per second.
    remote_estimate: AtomicU64,
    /// Key agreement on the media path (RFC 6189), when the call uses it.
    zrtp: Mutex<Option<ZrtpSession>>,
    /// The short authentication string once ZRTP has finished, for the application to show.
    zrtp_sas: Mutex<Option<String>>,
    /// Data channels, which ride inside the same DTLS tunnel as the SRTP keying (RFC 8261).
    /// Created when the tunnel comes up, because only then is the role settled.
    sctp: Mutex<Option<SctpAssociation>>,
    data_channels: AtomicBool,
    /// Channels asked for before the association was up, opened as soon as it is.
    pending_channels: Mutex<Vec<String>>,
}

pub struct MediaSession {
    shared: Arc<Shared>,
    local: SocketAddr,
    reflexive: Option<SocketAddr>,
    threads: Mutex<Vec<JoinHandle<()>>>,
    local_srtp_key: Mutex<Option<String>>,
    dtls_role: Mutex<Option<DtlsRole>>,
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

        // Without ICE the reflexive address is what the SDP advertises, so the offer has to wait for
        // it. With ICE it is one candidate among several: the probe goes out without blocking the call
        // and its answer is trickled to the peer (RFC 8838) whenever it arrives.
        let mut candidates = vec![Candidate::new(CandidateKind::Host, local, 1)];
        let reflexive = match (config.ice, config.stun_server) {
            (false, Some(server)) => stun::discover_reflexive(&socket, server, Duration::from_millis(1500)).filter(|r| *r != local),
            _ => None,
        };
        if let Some(r) = reflexive {
            candidates.push(Candidate::new(CandidateKind::ServerReflexive, r, 1));
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
                timestamp_at: Instant::now(),
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
                remote_ssrc: None,
                reported_expected: 0,
                reported_received: 0,
                last_sr: None,
                sync: None,
                playing: None,
                burst_gap: BurstGapTracker::default(),
            }),
            ice: Mutex::new(IceAgent::new(&candidates, IceRole::Controlled)),
            local_address: local,
            local_candidates: Mutex::new(candidates),
            gathering: Mutex::new(None),
            via_relay: AtomicBool::new(relay.is_some()),
            relay_permissions: Mutex::new(Vec::new()),
            relay: Mutex::new(relay),
            conference: Mutex::new(None),
            video: Mutex::new(None),
            sink,
            config: MediaConfig { ptime_ms: ptime, ..config },
            packets_sent: AtomicU64::new(0),
            packets_received: AtomicU64::new(0),
            bytes_sent: AtomicU64::new(0),
            bytes_received: AtomicU64::new(0),
            last_rx: Mutex::new(Instant::now()),
            timeout_reported: AtomicBool::new(false),
            remote_quality: Mutex::new(RemoteQuality::default()),
            #[cfg(feature = "audio-processing")]
            enhancer: Mutex::new(None),
            stream_delay_ms: AtomicU32::new(config.stream_delay_ms),
            dtls: Mutex::new(None),
            dtls_start_pending: AtomicBool::new(false),
            video_epoch: Mutex::new(None),
            estimator: Mutex::new(BandwidthEstimator::new()),
            remote_estimate: AtomicU64::new(0),
            zrtp: Mutex::new(None),
            zrtp_sas: Mutex::new(None),
            sctp: Mutex::new(None),
            data_channels: AtomicBool::new(false),
            pending_channels: Mutex::new(Vec::new()),
            secure_required: AtomicBool::new(false),
        });
        let session = Arc::new(Self { shared, local, reflexive, threads: Mutex::new(Vec::new()), local_srtp_key: Mutex::new(None), dtls_role: Mutex::new(None) });
        if reflexive.is_none() {
            if let Some(server) = session.shared.config.stun_server {
                probe_reflexive(&session.shared, server);
            }
        }

        Ok(session)
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
        let (ufrag, pwd) = ice.credentials();
        (ufrag.to_owned(), pwd.to_owned())
    }

    /// ICE username fragment the peer last signaled.
    pub fn remote_ice_ufrag(&self) -> Option<String> {
        self.shared.ice.lock().remote_ufrag().map(str::to_owned)
    }

    /// Starts an ICE restart with fresh local credentials; media keeps using the current pair until
    /// the new checks select one.
    pub fn restart_ice(&self) {
        self.shared.ice.lock().restart();
    }

    /// Adds candidates that arrived after the offer/answer (trickle ICE, RFC 8838).
    pub fn add_remote_candidates(&self, candidates: &[Candidate]) {
        {
            let mut ice = self.shared.ice.lock();
            for c in candidates {
                ice.add_remote_candidate(c.clone(), Instant::now());
            }
        }
        let detail = candidates.iter().map(|c| format!("{} {}", c.kind.as_str(), c.address)).collect::<Vec<_>>().join(", ");
        self.shared.sink.on_media_event(self.shared.call_id, "ice-candidates", &detail);
    }

    pub fn local_candidates(&self) -> Vec<Candidate> {
        self.shared.local_candidates.lock().clone()
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

    /// Switches the session to DTLS-SRTP keying and returns the local certificate fingerprint.
    /// Plain RTP is neither sent nor accepted from now on.
    pub fn enable_dtls(&self) -> Option<String> {
        let identity = self.shared.config.dtls_identity.as_ref()?;
        self.shared.secure_required.store(true, Ordering::Relaxed);
        Some(identity.fingerprint().to_owned())
    }

    pub fn dtls_enabled(&self) -> bool {
        self.shared.secure_required.load(Ordering::Relaxed)
    }

    /// Local certificate fingerprint when DTLS-SRTP keying is enabled.
    pub fn dtls_fingerprint(&self) -> Option<String> {
        self.shared.config.dtls_identity.as_ref().filter(|_| self.dtls_enabled()).map(|i| i.fingerprint().to_owned())
    }

    /// The DTLS role, once chosen. It stays fixed for the life of the call.
    pub fn dtls_role(&self) -> Option<DtlsRole> {
        *self.dtls_role.lock()
    }

    pub fn set_dtls_role(&self, role: DtlsRole) {
        self.dtls_role.lock().get_or_insert(role);
    }

    /// Turns on ZRTP: the two sides agree on SRTP keys over the media path itself (RFC 6189).
    ///
    /// The call starts in the clear and switches to encrypted when the exchange finishes, which is
    /// what ZRTP is for — no key material ever goes through the signalling.
    pub fn enable_zrtp(&self) {
        let ssrc = self.shared.tx.lock().ssrc;
        let mut guard = self.shared.zrtp.lock();
        if guard.is_none() {
            *guard = Some(ZrtpSession::new(ssrc));
        }
    }

    pub fn zrtp_enabled(&self) -> bool {
        self.shared.zrtp.lock().is_some()
    }

    /// The short authentication string, once both sides have agreed on keys.
    pub fn zrtp_sas(&self) -> Option<String> {
        self.shared.zrtp_sas.lock().clone()
    }

    /// The hash of this side's Hello message, for `a=zrtp-hash` in the SDP (RFC 6189 8.1).
    pub fn zrtp_hello_hash(&self) -> Option<String> {
        self.shared.zrtp.lock().as_ref().map(ZrtpSession::hello_hash)
    }

    /// Turns on data channels, which share the call's DTLS tunnel (RFC 8261). The association opens
    /// once that tunnel is up, so this can be called any time before or during the handshake.
    pub fn enable_data_channels(&self) {
        self.shared.data_channels.store(true, Ordering::Relaxed);
    }

    pub fn data_channels_enabled(&self) -> bool {
        self.shared.data_channels.load(Ordering::Relaxed)
    }

    /// Asks for a channel with this label. It is opened straight away when the association is up, and
    /// queued until then, so the application never has to wait for the handshake.
    pub fn open_data_channel(&self, label: &str) {
        let sh = &self.shared;
        let mut events = Vec::new();
        {
            let mut guard = sh.sctp.lock();
            match guard.as_mut().filter(|s| s.is_established()) {
                Some(sctp) => {
                    sctp.open_channel(label, Instant::now(), &mut events);
                }
                None => sh.pending_channels.lock().push(label.to_owned()),
            }
        }

        process_sctp_events(sh, events);
    }

    /// Sends a text message on an open channel. False when the channel is not usable yet.
    pub fn send_data_text(&self, stream: u16, text: &str) -> bool {
        self.send_data(stream, |sctp, events| sctp.send_text(stream, text, Instant::now(), events))
    }

    /// Sends a binary message on an open channel. False when the channel is not usable yet.
    pub fn send_data_binary(&self, stream: u16, data: &[u8]) -> bool {
        self.send_data(stream, |sctp, events| sctp.send_binary(stream, data, Instant::now(), events))
    }

    fn send_data(&self, stream: u16, send: impl FnOnce(&mut SctpAssociation, &mut Vec<SctpEvent>) -> bool) -> bool {
        let sh = &self.shared;
        let mut events = Vec::new();
        let sent = {
            let mut guard = sh.sctp.lock();
            match guard.as_mut() {
                Some(sctp) if sctp.channels().any(|(id, _)| *id == stream) => send(sctp, &mut events),
                _ => false,
            }
        };
        process_sctp_events(sh, events);
        sent
    }

    /// Channels that are open, as (stream number, label).
    pub fn data_channels(&self) -> Vec<(u16, String)> {
        self.shared
            .sctp
            .lock()
            .as_ref()
            .map(|s| s.channels().map(|(id, label)| (*id, label.clone())).collect())
            .unwrap_or_default()
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
            let mut codec = create_audio_codec(&n.codec);
            if let Some(c) = codec.as_mut() {
                c.set_dtx(sh.config.opus_dtx);
            }
            let codec_rate = codec.as_ref().map_or(n.codec.clock_rate, |c| c.sample_rate());
            #[cfg(feature = "audio-processing")]
            if tx.codec_rate != codec_rate || sh.enhancer.lock().is_none() {
                let mut enhancer = AudioEnhancer::new(
                    codec_rate,
                    sh.config.echo_cancellation,
                    sh.config.noise_suppression,
                    sh.config.auto_gain,
                );
                if let Some(enhancer) = enhancer.as_mut() {
                    enhancer.set_delay_ms(sh.stream_delay_ms.load(Ordering::Relaxed));
                }
                *sh.enhancer.lock() = enhancer;
            }
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
        if let (Some(ufrag), Some(pwd)) = (&n.remote_ice_ufrag, &n.remote_ice_pwd) {
            let role = if n.ice_controlling { IceRole::Controlling } else { IceRole::Controlled };
            sh.ice.lock().set_remote(ufrag, pwd, &n.remote_candidates, role, Instant::now());
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

        // ZRTP begins as soon as the peer's address is known: it travels on the media path.
        if sh.remote.lock().is_some() {
            let mut events = Vec::new();
            if let Some(zrtp) = sh.zrtp.lock().as_mut() {
                zrtp.start(Instant::now(), &mut events);
            }

            process_zrtp_events(sh, events);
        }

        if let (Some(fingerprint), true) = (&n.remote_fingerprint, self.dtls_enabled()) {
            let mut guard = sh.dtls.lock();
            if guard.is_none() {
                let identity = sh.config.dtls_identity.as_ref().ok_or("DTLS negotiated without a local certificate")?;
                if let Some(role) = n.dtls_role {
                    self.set_dtls_role(role);
                }
                let role = self.dtls_role().unwrap_or(DtlsRole::Client);
                let mut transport = DtlsTransport::new(identity, role, fingerprint, Instant::now())?;
                // A client waits for ICE to find a working path before sending its ClientHello.
                let wait_for_ice = role == DtlsRole::Client && n.remote_ice_ufrag.is_some() && !sh.ice.lock().is_connected();
                let mut events = Vec::new();
                if wait_for_ice {
                    sh.dtls_start_pending.store(true, Ordering::Relaxed);
                } else {
                    transport.start(Instant::now(), &mut events);
                }
                *guard = Some(transport);
                drop(guard);
                process_dtls_events(sh, events);
            }
        }
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
        match self.shared.tx.lock().srtp.as_mut() {
            Some(ctx) => {
                let _ = ctx.protect_rtcp(&mut bye);
            }
            None if self.shared.secure_required.load(Ordering::Relaxed) => bye.clear(),
            None => {}
        }
        if !bye.is_empty() {
            send_raw(&self.shared, &bye);
        }
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

    /// Switches this session to video: frames are packetized on the way out and reassembled on the
    /// way in (RFC 6184 for H.264, RFC 7741 for VP8). Returns false for formats without a payload format.
    pub fn enable_video(&self, encoding: &str, payload_type: u8, content: &str) -> bool {
        let format = match encoding.to_ascii_uppercase().as_str() {
            "H264" => VideoFormat::H264,
            "VP8" => VideoFormat::Vp8,
            _ => return false,
        };
        *self.shared.video.lock() = Some(VideoTrack {
            payload_type,
            content: content.to_owned(),
            packetizer: VideoPacketizer::new(format),
            depacketizer: VideoDepacketizer::new(format),
            format,
            rid_extension: None,
            send_rid_extension: None,
            send_rids: Vec::new(),
            layers: Vec::new(),
            selected: None,
            layer_changed_at: None,
            incomplete_seen: 0,
            frames: 0,
            reported_frames: 0,
            reported_incomplete: 0,
            last_request: None,
            request_backoff: KEYFRAME_REQUEST_INTERVAL,
            fir_sequence: 0,
        });
        true
    }

    /// Tells the session which header extension carries the encoding name, and which encodings the
    /// peer said it would send (RFC 8852). Without this a stream is treated as a single encoding.
    pub fn expect_simulcast(&self, extension_id: u8, rids: &[String]) {
        {
            let mut track = self.shared.video.lock();
            let Some(track) = track.as_mut() else { return };
            track.rid_extension = Some(extension_id);
            // Which encoding to keep is decided by what arrives: a sender may send fewer than it
            // offered, and choosing from the offer alone would drop everything it does send.
        }

        let detail = format!("{} encodings offered: {}", rids.len(), rids.join(", "));
        self.shared.sink.on_media_event(self.shared.call_id, "simulcast", &detail);
    }

    /// The encodings this stream carries, with what each is measured at in bits per second.
    /// Says which encodings this side may send, and under which header extension id, after the peer
    /// has agreed to receive them (RFC 8853).
    pub fn send_simulcast(&self, extension_id: u8, rids: &[String]) {
        let mut track = self.shared.video.lock();
        if let Some(track) = track.as_mut() {
            track.send_rid_extension = Some(extension_id);
            track.send_rids = rids.to_vec();
        }
    }

    /// The encodings this side agreed to send, in the order they were offered.
    pub fn sending_encodings(&self) -> Vec<String> {
        self.shared.video.lock().as_ref().map(|t| t.send_rids.clone()).unwrap_or_default()
    }

    pub fn video_layers(&self) -> Vec<(String, u64)> {
        self.shared.video.lock().as_ref().map(VideoTrack::measured).unwrap_or_default()
    }

    /// The encoding being forwarded right now.
    pub fn video_layer(&self) -> Option<String> {
        self.shared.video.lock().as_ref().and_then(|t| t.selected.clone())
    }

    /// Picks the encoding that fits what the receivers can take.
    ///
    /// The best layer whose measured bitrate leaves a little headroom wins; when none of them fit,
    /// the smallest one does, because some picture beats none. Changing the choice costs a keyframe,
    /// so a layer has to be measured before it can be picked.
    pub fn apply_layer_budget(&self, budget_bps: u64) {
        choose_layer(&self.shared, budget_bps);
    }

    /// Asks the peer for a keyframe. `full` sends a Full Intra Request (RFC 5104) instead of a Picture
    /// Loss Indication (RFC 4585). Returns false when there is no video stream, no peer yet, or one
    /// was asked for a moment ago — a keyframe takes time to arrive, and asking again while it is on
    /// its way only makes the sender produce another (RFC 5104 asks for the same restraint).
    pub fn request_keyframe(&self, full: bool) -> bool {
        let sequence = {
            let mut track = self.shared.video.lock();
            let Some(track) = track.as_mut() else { return false };
            if track.last_request.is_some_and(|at| at.elapsed() < KEYFRAME_REQUEST_INTERVAL) {
                return false;
            }

            track.last_request = Some(Instant::now());
            track.fir_sequence = track.fir_sequence.wrapping_add(1);
            track.fir_sequence
        };
        send_keyframe_request(&self.shared, full, sequence)
    }

    /// Tells the echo canceller how long audio takes to travel out of the speaker and back into the
    /// microphone. Applications that know their device latency (see `CallAudioBridge`) report it here.
    pub fn set_stream_delay_ms(&self, delay_ms: u32) {
        self.shared.stream_delay_ms.store(delay_ms, Ordering::Relaxed);
        #[cfg(feature = "audio-processing")]
        if let Some(enhancer) = self.shared.enhancer.lock().as_mut() {
            enhancer.set_delay_ms(delay_ms);
        }
    }

    pub fn video_enabled(&self) -> bool {
        self.shared.video.lock().is_some()
    }

    /// Sends one encoded video frame, split across as many RTP packets as it needs. `timestamp` is in
    /// the 90 kHz video clock.
    pub fn send_video_frame(&self, timestamp: u32, frame: &[u8]) -> bool {
        self.send_video_frame_as(timestamp, frame, None)
    }

    /// Sends a frame as one of several encodings of the same picture (RFC 8853).
    ///
    /// Each packet carries the encoding's name in a header extension, which is the only thing that
    /// tells the receiver which stream a packet belongs to. An encoding the peer did not agree to
    /// receive is sent without the label, exactly as a single-encoding call would be.
    pub fn send_video_frame_as(&self, timestamp: u32, frame: &[u8], rid: Option<&str>) -> bool {
        let Some((payload_type, packets, extension)) = ({
            let mut track = self.shared.video.lock();
            track.as_mut().map(|t| {
                let extension = rid.filter(|name| t.send_rids.iter().any(|r| r == name)).and_then(|name| {
                    t.send_rid_extension.map(|id| (id, name.as_bytes().to_vec()))
                });
                (t.payload_type, t.packetizer.packetize(frame), extension)
            })
        }) else {
            return false;
        };
        if !direction_from(self.shared.direction.load(Ordering::Relaxed)).can_send() {
            return false;
        }
        let last = packets.len().saturating_sub(1);
        for (i, payload) in packets.iter().enumerate() {
            let label = extension.as_ref().map(|(id, name)| (*id, name.as_slice()));
            self.send_encoded_labelled(payload_type, timestamp, i == last, payload, label);
        }
        !packets.is_empty()
    }

    /// Sends a frame that came from another call, as a conference forwards one.
    ///
    /// The timestamp is this stream's own, not the sender's: when the room looks at somebody else the
    /// source clock jumps to an unrelated base, and a receiver that sees time move backwards throws the
    /// frame away, which is what tore the picture on every change of speaker.
    pub fn forward_video_frame(&self, frame: &[u8]) -> bool {
        let timestamp = {
            let mut epoch = self.shared.video_epoch.lock();
            let start = *epoch.get_or_insert_with(Instant::now);
            // 90 kHz video clock (RFC 6184 8.2.1), from microseconds so short gaps still advance it.
            (start.elapsed().as_micros() as u64 * 9 / 100) as u32
        };
        self.send_video_frame(timestamp, frame)
    }

    /// Sends an already-encoded payload (pass-through codecs and video).
    pub fn send_encoded(&self, payload_type: u8, timestamp: u32, marker: bool, payload: &[u8]) {
        self.send_encoded_labelled(payload_type, timestamp, marker, payload, None);
    }

    /// The same, with a header extension naming the encoding the packet belongs to.
    pub fn send_encoded_labelled(
        &self,
        payload_type: u8,
        timestamp: u32,
        marker: bool,
        payload: &[u8],
        extension: Option<(u8, &[u8])>,
    ) {
        let sh = &self.shared;
        let mut tx = sh.tx.lock();
        let tx = &mut *tx;
        tx.packet.clear();
        let header = RtpHeader { marker, payload_type, sequence: tx.sequence, timestamp, ssrc: tx.ssrc };
        tx.sequence = tx.sequence.wrapping_add(1);
        header.write_with(payload, extension, &mut tx.packet);
        let secured = match tx.srtp.as_mut() {
            Some(ctx) => ctx.protect_rtp(&mut tx.packet).is_ok(),
            None => !sh.secure_required.load(Ordering::Relaxed),
        };
        if !secured {
            return;
        }
        send_raw(sh, &tx.packet);
        // Video and pass-through payloads go out here rather than through the audio path, and they
        // are still a stream the peer expects reports about: without these a video stream sends no
        // sender report at all, so the peer can measure neither its round trip nor its lip sync.
        tx.packets = tx.packets.wrapping_add(1);
        tx.octets = tx.octets.wrapping_add(payload.len() as u32);
        tx.timestamp = timestamp;
        tx.timestamp_at = Instant::now();
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

    /// Turns an RTP timestamp from this stream into the sender's wall clock, as NTP time.
    ///
    /// Sender reports carry the same instant on both clocks (RFC 3550 6.4.1), which is how audio and
    /// video are lined up: ask each stream when its timestamp was sent and present them in that order.
    /// Returns nothing until the peer has sent a report.
    pub fn presentation_ntp(&self, rtp_timestamp: u32) -> Option<u64> {
        let rx = self.shared.rx.lock();
        let (ntp, reference) = rx.sync?;
        let rate = match self.shared.video.lock().is_some() {
            // Video always runs on the 90 kHz clock (RFC 6184 8.2.1); audio on the codec's rate.
            true => 90_000,
            false => rx.codec.as_ref().map_or(8000, |c| c.clock_rate()),
        };
        // The difference is signed: a timestamp before the report is a time before it.
        let elapsed = rtp_timestamp.wrapping_sub(reference) as i32 as i64;
        let ticks = (elapsed << 32) / i64::from(rate.max(1));
        Some((ntp as i64).wrapping_add(ticks) as u64)
    }

    /// When the audio being played right now was sent, as NTP time.
    ///
    /// This is the other half of lip sync: compare a frame's [`presentation_ntp`](Self::presentation_ntp)
    /// with this, and show the frame when the audio for the same moment is heard.
    pub fn playout_ntp(&self) -> Option<u64> {
        let playing = self.shared.rx.lock().playing?;
        self.presentation_ntp(playing)
    }

    /// The conference this session is mixed into, if any.
    pub fn conference(&self) -> Option<Arc<Conference>> {
        self.shared.conference.lock().clone()
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
        let remote = *sh.remote_quality.lock();
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
            ice_connected: u8::from(sh.ice.lock().is_connected()),
            outbound_queued_ms: queued_ms,
            remote_loss_percent: remote.loss_percent,
            remote_jitter_ms: remote.jitter_ms,
            round_trip_ms: remote.rtt_ms,
            remote_mos: remote.mos,
            remote_estimate_bps: sh.remote_estimate.load(Ordering::Relaxed),
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
    send_to(sh, remote, sh.via_relay.load(Ordering::Relaxed), data);
}

fn send_to(sh: &Shared, to: SocketAddr, via_relay: bool, data: &[u8]) {
    match sh.relay.lock().as_ref().filter(|_| via_relay) {
        Some(relay) => {
            let mut permitted = sh.relay_permissions.lock();
            if !permitted.contains(&to.ip()) {
                permitted.push(to.ip());
                let _ = sh.socket.send_to(&relay.create_permission(to), relay.server);
            }
            let _ = sh.socket.send_to(&relay.wrap(to, data), relay.server);
        }
        None => {
            let _ = sh.socket.send_to(data, to);
        }
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
                handle_datagram(sh, &inner, peer, true);
            }
            continue;
        }
        let data = buf[..n].to_vec();
        handle_datagram(sh, &data, from, false);
    }
}

fn handle_datagram(sh: &Arc<Shared>, data: &[u8], from: SocketAddr, via_relay: bool) {
    match classify(data) {
        PacketClass::Stun => handle_stun(sh, data, from, via_relay),
        PacketClass::Rtp => handle_rtp(sh, data, from),
        PacketClass::Rtcp => handle_rtcp(sh, data),
        PacketClass::Zrtp => {
            let mut events = Vec::new();
            if let Some(zrtp) = sh.zrtp.lock().as_mut() {
                zrtp.handle_packet(data, Instant::now(), &mut events);
            }

            process_zrtp_events(sh, events);
        }
        PacketClass::Dtls => {
            let mut events = Vec::new();
            if let Some(dtls) = sh.dtls.lock().as_mut() {
                dtls.handle_packet(data, &mut events);
            }
            process_dtls_events(sh, events);
        }
        PacketClass::Unknown => {}
    }
}

/// Reads the peer's RTCP: sender reports feed the round-trip estimate, and report blocks about our
/// own stream say what the network does on the way out.
fn handle_rtcp(sh: &Arc<Shared>, data: &[u8]) {
    let mut plain = data.to_vec();
    {
        let mut rx = sh.rx.lock();
        let secured = match rx.srtp.as_mut() {
            Some(ctx) => ctx.unprotect_rtcp(&mut plain).is_ok(),
            None => !sh.secure_required.load(Ordering::Relaxed),
        };
        if !secured {
            return;
        }
    }

    let now = Instant::now();
    let our_ssrc = sh.tx.lock().ssrc;
    let mut quality = None;
    for packet in parse_rtcp(&plain) {
        let reports = match packet {
            RtcpPacket::SenderReport { ssrc, ntp, rtp_ts, reports } => {
                let mut rx = sh.rx.lock();
                rx.remote_ssrc.get_or_insert(ssrc);
                rx.last_sr = Some(((ntp >> 16) as u32, now));
                rx.sync = Some((ntp, rtp_ts));
                reports
            }
            RtcpPacket::ReceiverReport { reports, .. } => reports,
            RtcpPacket::ExtendedReport { metrics, .. } => {
                if metrics.mos_lq > 0 && metrics.mos_lq != 127 {
                    sh.remote_quality.lock().mos = metrics.mos_lq as f64 / 10.0;
                }
                continue;
            }
            RtcpPacket::KeyframeRequest { full, .. } => {
                sh.sink.on_media_event(sh.call_id, "keyframe-request", if full { "fir" } else { "pli" });
                continue;
            }
            RtcpPacket::ReceiverEstimate { bitrate, .. } => {
                // Report a change worth acting on; a few percent either way is not worth re-encoding for.
                let previous = sh.remote_estimate.swap(bitrate, Ordering::Relaxed);
                if previous == 0 || bitrate.abs_diff(previous) * 10 > previous {
                    sh.sink.on_media_event(sh.call_id, "bandwidth-estimate", &format!("{} kbit/s", bitrate / 1000));
                }

                continue;
            }
            RtcpPacket::Bye { .. } => continue,
        };
        for block in reports.into_iter().filter(|b| b.ssrc == our_ssrc) {
            let rtt = if block.last_sr == 0 {
                sh.remote_quality.lock().rtt_ms
            } else {
                // RFC 3550 6.4.1: now - LSR - DLSR, all in units of 1/65536 s. The delay is measured on a
                // monotonic clock while NTP comes from the system clock, whose resolution is coarse on
                // Windows, so the difference can come out slightly negative: that means "below resolution".
                let delta = ((ntp_now() >> 16) as u32).wrapping_sub(block.last_sr).wrapping_sub(block.delay_since_last_sr) as i32;
                f64::from(delta.max(0)) * 1000.0 / 65536.0
            };
            let clock = sh.tx.lock().codec.as_ref().map_or(8000, |c| c.clock_rate()) as f64;
            let mos = sh.remote_quality.lock().mos;
            quality = Some(RemoteQuality {
                loss_percent: block.fraction_lost as f64 * 100.0 / 256.0,
                jitter_ms: block.jitter as f64 * 1000.0 / clock,
                rtt_ms: rtt.clamp(0.0, 10_000.0),
                mos,
            });
        }
    }

    if let Some(q) = quality {
        *sh.remote_quality.lock() = q;
        // Codecs with loss recovery (Opus) adapt their bitrate and FEC to what the peer sees.
        if let Some(codec) = sh.tx.lock().codec.as_mut() {
            codec.set_network_quality(q.loss_percent, q.rtt_ms);
        }
        sh.sink.on_media_event(sh.call_id, "remote-quality", &format!("{:.1}% loss, rtt {:.0} ms", q.loss_percent, q.rtt_ms));
    }
}

fn ntp_now() -> u64 {
    let since_epoch = SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default();
    ((since_epoch.as_secs() + 2_208_988_800) << 32) | (((since_epoch.subsec_nanos() as u64) << 32) / 1_000_000_000)
}

fn handle_stun(sh: &Arc<Shared>, data: &[u8], from: SocketAddr, via_relay: bool) {
    let Some(msg) = StunMessage::decode(data) else { return };
    let now = Instant::now();
    let outputs = match msg.msg_type {
        stun::BINDING_REQUEST if msg.get(stun::ATTR_MESSAGE_INTEGRITY).is_none() => {
            // A plain keep-alive or reflexive probe from a peer without ICE.
            let mut resp = msg.reply(stun::BINDING_SUCCESS);
            resp.add_xor_address(stun::ATTR_XOR_MAPPED_ADDRESS, from);
            send_to(sh, from, via_relay, &resp.encode(None, true));
            return;
        }
        stun::BINDING_REQUEST => sh.ice.lock().handle_request(data, &msg, from, via_relay, now),
        stun::BINDING_SUCCESS if sh.gathering.lock().as_ref().is_some_and(|g| g.transaction == msg.transaction_id) => {
            *sh.gathering.lock() = None;
            if let Some(address) = msg.mapped_address() {
                if address != sh.local_address {
                    gathered(sh, address);
                }
            }

            return;
        }
        stun::BINDING_SUCCESS | stun::BINDING_ERROR => sh.ice.lock().handle_response(data, &msg, from, now).1,
        _ => return,
    };
    process_ice_outputs(sh, outputs);
}

/// Sends a STUN binding request to learn this socket's address as the world sees it. The answer is
/// picked up by the receive loop, because that is the only thread reading the socket.
fn probe_reflexive(sh: &Arc<Shared>, server: SocketAddr) {
    let mut request = StunMessage::new(stun::BINDING_REQUEST);
    request.add(stun::ATTR_SOFTWARE, b"Voip.NET".to_vec());
    let attempts = sh.gathering.lock().as_ref().map_or(0, |g| g.attempts);
    *sh.gathering.lock() = Some(Gathering {
        server,
        transaction: request.transaction_id,
        sent: Instant::now(),
        attempts: attempts + 1,
    });
    let _ = sh.socket.send_to(&request.encode(None, true), server);
}

/// Resends the probe while it goes unanswered; a lost packet, or a server that takes its time, should
/// not cost the call its candidate. Five tries half a second apart cover the first seconds of a call,
/// which is where a reflexive candidate is still worth having.
fn retry_gathering(sh: &Arc<Shared>) {
    let retry = {
        let gathering = sh.gathering.lock();
        match gathering.as_ref() {
            Some(g) if g.attempts < 5 && g.sent.elapsed() > Duration::from_millis(500) => Some(g.server),
            _ => None,
        }
    };
    if let Some(server) = retry {
        probe_reflexive(sh, server);
    }
}

/// Records the reflexive address the STUN server reported and offers it to the peer.
fn gathered(sh: &Arc<Shared>, address: SocketAddr) {
    let candidate = Candidate::new(CandidateKind::ServerReflexive, address, 1);
    {
        let mut candidates = sh.local_candidates.lock();
        if candidates.iter().any(|c| c.address == address) {
            return;
        }

        candidates.push(candidate.clone());
    }

    sh.sink.on_media_event(sh.call_id, "ice-candidate", &candidate.to_sdp());
}

/// Acts on ICE agent output. Runs without the ICE lock held.
fn process_ice_outputs(sh: &Arc<Shared>, outputs: Vec<IceOutput>) {
    for output in outputs {
        match output {
            IceOutput::Send { to, relay, data } => send_to(sh, to, relay, &data),
            IceOutput::Selected { remote, relay, local_kind, remote_kind } => {
                *sh.remote.lock() = Some(remote);
                // The selected pair decides the path; RTP latching must not override it.
                sh.latched.store(true, Ordering::Relaxed);
                sh.via_relay.store(relay, Ordering::Relaxed);
                let detail = format!("{local_kind} → {remote_kind} {remote}");
                sh.sink.on_media_event(sh.call_id, "ice-connected", &detail);
                if sh.dtls_start_pending.swap(false, Ordering::Relaxed) {
                    let mut events = Vec::new();
                    if let Some(dtls) = sh.dtls.lock().as_mut() {
                        dtls.start(Instant::now(), &mut events);
                    }
                    process_dtls_events(sh, events);
                }
            }
            IceOutput::Disconnected => sh.sink.on_media_event(sh.call_id, "ice-disconnected", "consent expired"),
            IceOutput::Failed => sh.sink.on_media_event(sh.call_id, "ice-failed", "no candidate pair worked"),
        }
    }
}

/// Acts on DTLS output. Runs without the DTLS lock held; takes the tx/rx locks briefly.
fn process_dtls_events(sh: &Arc<Shared>, events: Vec<DtlsEvent>) {
    for event in events {
        match event {
            DtlsEvent::Send(packet) => send_raw(sh, &packet),
            DtlsEvent::Keys(outbound, inbound, profile) => {
                sh.tx.lock().srtp = Some(outbound);
                sh.rx.lock().srtp = Some(inbound);
                sh.sink.on_media_event(sh.call_id, "dtls-connected", profile.name());
            }
            DtlsEvent::Connected => start_sctp(sh),
            DtlsEvent::Data(packet) => {
                let mut out = Vec::new();
                if let Some(sctp) = sh.sctp.lock().as_mut() {
                    sctp.handle_packet(&packet, Instant::now(), &mut out);
                }

                process_sctp_events(sh, out);
            }
            DtlsEvent::Failed(reason) => sh.sink.on_media_event(sh.call_id, "dtls-failed", &reason),
        }
    }
}

/// Opens the SCTP association once the tunnel is up. Only the DTLS client sends INIT, which is also
/// the side that owns the even stream numbers (RFC 8832 §6), so both ends agree without negotiating.
fn start_sctp(sh: &Arc<Shared>) {
    if !sh.data_channels.load(Ordering::Relaxed) {
        return;
    }

    let client = sh.dtls.lock().as_ref().map(|d| d.role() == DtlsRole::Client).unwrap_or(true);
    let mut events = Vec::new();
    {
        let mut guard = sh.sctp.lock();
        let sctp = guard.get_or_insert_with(|| SctpAssociation::new(client));
        if sctp.is_established() {
            return;
        }

        sctp.connect(Instant::now(), &mut events);
    }

    process_sctp_events(sh, events);
}

/// The encoding to keep for a budget: the largest that leaves a tenth of it spare, or the smallest
/// there is when none of them fit, because some picture beats none.
///
/// What is already playing gets the benefit of the doubt. A measured bitrate moves about — a keyframe
/// alone can double a second's worth — so an encoding that fits is only given up once it stops
/// fitting, and a larger one has to fit with a third of the budget to spare before it is taken. Both
/// margins exist because changing encoding freezes the picture until a keyframe arrives.
fn best_layer(measured: &[(String, u64)], budget_bps: u64, current: Option<&str>) -> Option<String> {
    let headroom = budget_bps * 9 / 10;
    if let Some((_, rate)) = current.and_then(|name| measured.iter().find(|(n, _)| n == name)) {
        let step_up = measured
            .iter()
            .filter(|(_, other)| *other > *rate && *other <= budget_bps * 2 / 3)
            .max_by_key(|(_, other)| *other);
        if let Some((name, _)) = step_up {
            return Some(name.clone());
        }

        if *rate <= headroom {
            return current.map(str::to_owned);
        }
    }

    measured
        .iter()
        .filter(|(_, rate)| *rate <= headroom)
        .max_by_key(|(_, rate)| *rate)
        .or_else(|| measured.iter().min_by_key(|(_, rate)| *rate))
        .map(|(name, _)| name.clone())
}

/// Picks the encoding that fits `budget_bps`, and asks for a keyframe when the choice changes.
fn choose_layer(sh: &Arc<Shared>, budget_bps: u64) {
    let (changed, selected) = {
        let mut track = sh.video.lock();
        let Some(track) = track.as_mut() else { return };
        if track.layers.is_empty() || budget_bps == 0 {
            return;
        }

        // Only encodings still arriving are candidates: a sender is free to stop one at any time, and
        // holding the selection on a dead encoding would show the viewer nothing at all.
        let now = Instant::now();
        let measured: Vec<(String, u64)> = track
            .layers
            .iter()
            .filter(|(_, layer)| now.duration_since(layer.last_packet) < Duration::from_secs(2))
            .map(|(name, layer)| (name.clone(), layer.bitrate))
            .collect();
        let selected_lives = track.selected.as_ref().is_some_and(|name| measured.iter().any(|(n, _)| n == name));
        if measured.is_empty() || (selected_lives && (measured.len() < 2 || measured.iter().any(|(_, rate)| *rate == 0))) {
            // Nothing to choose between yet, and what was chosen is still coming through.
            return;
        }

        if track.layer_changed_at.is_some_and(|at| at.elapsed() < LAYER_DWELL) {
            return;
        }

        let Some(name) = best_layer(&measured, budget_bps, track.selected.as_deref()) else { return };
        let changed = track.selected.as_deref() != Some(name.as_str());
        if changed {
            track.selected = Some(name.clone());
            track.layer_changed_at = Some(now);
        }

        (changed, name)
    };

    if changed {
        // The new encoding is a different picture: a decoder cannot start on a delta frame.
        let sequence = {
            let mut track = sh.video.lock();
            match track.as_mut() {
                Some(track) => {
                    track.last_request = Some(Instant::now());
                    track.fir_sequence = track.fir_sequence.wrapping_add(1);
                    track.fir_sequence
                }
                None => return,
            }
        };
        send_keyframe_request(sh, false, sequence);
        sh.sink.on_media_event(sh.call_id, "video-layer", &selected);
    }
}

/// Acts on ZRTP output. Runs without the ZRTP lock held, like the other protocol pumps.
fn process_zrtp_events(sh: &Arc<Shared>, events: Vec<ZrtpEvent>) {
    for event in events {
        match event {
            ZrtpEvent::Send(packet) => send_raw(sh, &packet),
            ZrtpEvent::Secure(result) => {
                // From here the stream is encrypted; until now it was in the clear, which is how
                // ZRTP works — the call starts, then the keys arrive over the same path.
                sh.tx.lock().srtp = Some(result.outbound);
                sh.rx.lock().srtp = Some(result.inbound);
                *sh.zrtp_sas.lock() = Some(result.sas.clone());
                sh.sink.on_media_event(sh.call_id, "zrtp-connected", &result.sas);
            }
            ZrtpEvent::Failed(reason) => sh.sink.on_media_event(sh.call_id, "zrtp-failed", &reason),
        }
    }
}

/// Acts on SCTP output: packets go back into the tunnel, messages go to the application.
fn process_sctp_events(sh: &Arc<Shared>, events: Vec<SctpEvent>) {
    for event in events {
        match event {
            SctpEvent::Send(packet) => {
                let mut out = Vec::new();
                if let Some(dtls) = sh.dtls.lock().as_mut() {
                    dtls.send_data(&packet, &mut out);
                }

                for event in out {
                    // Only the encrypted records matter here; a nested handshake event cannot happen.
                    if let DtlsEvent::Send(record) = event {
                        send_raw(sh, &record);
                    }
                }
            }
            SctpEvent::ChannelOpen { stream, label } => {
                sh.sink.on_media_event(sh.call_id, "data-channel-open", &format!("{stream} {label}"));
            }
            SctpEvent::Message { stream, text, data } => sh.sink.on_data_message(sh.call_id, stream, text, &data),
            SctpEvent::Closed(reason) => sh.sink.on_media_event(sh.call_id, "data-channel-closed", &reason),
        }
    }

    open_pending_channels(sh);
}

/// Opens the channels the application asked for before the association finished its handshake.
fn open_pending_channels(sh: &Arc<Shared>) {
    let mut events = Vec::new();
    {
        let mut guard = sh.sctp.lock();
        let Some(sctp) = guard.as_mut() else { return };
        if !sctp.is_established() {
            return;
        }

        let labels = std::mem::take(&mut *sh.pending_channels.lock());
        for label in labels {
            sctp.open_channel(&label, Instant::now(), &mut events);
        }
    }

    if !events.is_empty() {
        process_sctp_events(sh, events);
    }
}

fn handle_rtp(sh: &Arc<Shared>, data: &[u8], from: SocketAddr) {
    // Sink callbacks are deferred until the rx lock is released so handlers may call back into the session.
    let mut dtmf_digit = None;
    let mut passthrough: Option<(RtpHeader, Vec<u8>, Option<String>)> = None;
    // Which header extension carries the encoding name, when the peer said it would send several.
    let rid_extension = sh.video.lock().as_ref().and_then(|t| t.rid_extension);
    {
        let mut rx = sh.rx.lock();
        let rx = &mut *rx;
        rx.buf.clear();
        rx.buf.extend_from_slice(data);
        let secured = match rx.srtp.as_mut() {
            Some(ctx) => ctx.unprotect_rtp(&mut rx.buf).is_ok(),
            None => !sh.secure_required.load(Ordering::Relaxed),
        };
        if !secured {
            return;
        }
        let Some(pkt) = RtpPacketRef::parse(&rx.buf) else { return };
        rx.remote_ssrc.get_or_insert(pkt.header.ssrc);

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
            passthrough = Some((h, pkt.payload.to_vec(), rid_extension.and_then(|id| pkt.rid(id)).map(str::to_owned)));
        }
    }

    // Video frames are reassembled outside the rx lock: a frame can be large and the handler may call
    // back into the session.
    if let Some((h, payload, rid)) = passthrough.as_ref() {
        let (assembled, request, content) = {
            let mut track = sh.video.lock();
            match track.as_mut().filter(|t| t.payload_type == h.payload_type) {
                Some(t) => {
                    let (frame, lost) = t.push(rid.as_deref(), h, payload, Instant::now());
                    t.frames += u64::from(frame.is_some());
                    if frame.as_ref().is_some_and(|f| f.keyframe) {
                        // The asking worked: a whole picture to start from. Ask quickly again if the
                        // next one is damaged.
                        t.request_backoff = KEYFRAME_REQUEST_INTERVAL;
                    }

                    // A frame that lost packets is dropped, so ask the sender to start again from a keyframe.
                    let due = lost && t.last_request.is_none_or(|at| at.elapsed() >= t.request_backoff);
                    if due {
                        t.last_request = Some(Instant::now());
                        t.request_backoff = (t.request_backoff * 2).min(KEYFRAME_REQUEST_MAX);
                        t.fir_sequence = t.fir_sequence.wrapping_add(1);
                    }
                    (Some(frame), due.then_some(t.fir_sequence), t.content.clone())
                }
                None => (None, None, String::new()),
            }
        };
        if let Some(sequence) = request {
            send_keyframe_request(sh, false, sequence);
        }
        if let Some(frame) = assembled {
            if let Some(frame) = frame {
                sh.sink.on_video_frame(sh.call_id, frame.timestamp, frame.keyframe, &frame.data, &content);
            }
            return;
        }
    }
    if let Some(d) = dtmf_digit {
        sh.sink.on_dtmf(sh.call_id, d, DtmfSource::Rfc4733);
    }
    if let Some((h, payload, _)) = passthrough {
        sh.sink.on_encoded(sh.call_id, h.payload_type, h.timestamp, h.marker, &payload);
    }
}

fn playout_loop(sh: &Arc<Shared>) {
    let ptime = Duration::from_millis(sh.config.ptime_ms as u64);
    let mut next = Instant::now() + ptime;
    // The first report goes out after a second so both sides learn about each other quickly.
    let mut last_sr = Instant::now() - Duration::from_secs(3);
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
                    Playout::Frame { payload, timestamp, .. } => {
                        rx.playing = Some(timestamp);
                        if let Some(codec) = rx.codec.as_mut() {
                            codec.decode(&payload, &mut decoded);
                        }
                        rx.plc.remember(&decoded);
                        rx.jitter.give_back(payload);
                        rx.burst_gap.packet(false);
                    }
                    Playout::Lost => {
                        rx.burst_gap.packet(true);
                        let next = rx.jitter.next_payload();
                        let concealed = rx.codec.as_mut().is_some_and(|c| c.conceal(samples, next, &mut decoded));
                        if !concealed {
                            rx.plc.conceal(samples, &mut decoded);
                        }
                    }
                    Playout::Empty => decoded.resize(samples, 0),
                }
                if let Some(det) = rx.detector.as_mut() {
                    det.process(&decoded, &mut inband_digits);
                }
            }
        }
        #[cfg(feature = "audio-processing")]
        if !decoded.is_empty() {
            if let Some(enhancer) = sh.enhancer.lock().as_mut() {
                enhancer.render(&decoded);
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

        // ---- ICE connectivity checks and DTLS retransmissions ----
        let ice_outputs = sh.ice.lock().poll(Instant::now());
        process_ice_outputs(sh, ice_outputs);
        let mut dtls_events = Vec::new();
        if let Some(dtls) = sh.dtls.lock().as_mut() {
            dtls.poll_timeout(Instant::now(), &mut dtls_events);
        }
        process_dtls_events(sh, dtls_events);
        let mut sctp_events = Vec::new();
        if let Some(sctp) = sh.sctp.lock().as_mut() {
            sctp.poll_timeout(Instant::now(), &mut sctp_events);
        }
        process_sctp_events(sh, sctp_events);
        let mut zrtp_events = Vec::new();
        if let Some(zrtp) = sh.zrtp.lock().as_mut() {
            zrtp.poll_timeout(Instant::now(), &mut zrtp_events);
        }
        process_zrtp_events(sh, zrtp_events);

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
        retry_gathering(sh);
        if last_sr.elapsed() >= Duration::from_secs(4) {
            last_sr = Instant::now();
            send_sender_report(sh);
            // On its own path: a receive-only video stream sends no report blocks to hang it on.
            send_receiver_estimate(sh);
        }
        if last_refresh.elapsed() >= Duration::from_secs(240) {
            last_refresh = Instant::now();
            if let Some(relay) = sh.relay.lock().as_ref() {
                let _ = sh.socket.send_to(&relay.refresh(600), relay.server);
            }
        }
        // A video stream may legitimately stay silent (camera off), so only audio reports a timeout.
        if sh.config.rtp_timeout_ms > 0
            && sh.video.lock().is_none()
            && sh.last_rx.lock().elapsed() > Duration::from_millis(sh.config.rtp_timeout_ms as u64)
            && direction.can_receive()
            && !sh.timeout_reported.swap(true, Ordering::Relaxed)
        {
            sh.sink.on_media_event(sh.call_id, "rtp-timeout", "no RTP received");
        }
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
    tx.timestamp_at = Instant::now();

    if !has_audio || !direction.can_send() || tx.codec.is_none() {
        tx.was_silent = true;
        return tx.codec_rate;
    }
    if muted {
        tx.frame.iter_mut().for_each(|s| *s = 0);
    }
    #[cfg(feature = "audio-processing")]
    if !muted {
        if let Some(enhancer) = sh.enhancer.lock().as_mut() {
            enhancer.capture(&mut tx.frame);
        }
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
    let secured = match tx.srtp.as_mut() {
        Some(ctx) => ctx.protect_rtp(&mut tx.packet).is_ok(),
        None => !sh.secure_required.load(Ordering::Relaxed),
    };
    if !secured {
        return;
    }
    send_raw(sh, &tx.packet);
    tx.packets = tx.packets.wrapping_add(1);
    tx.octets = tx.octets.wrapping_add(payload.len() as u32);
    sh.packets_sent.fetch_add(1, Ordering::Relaxed);
    sh.bytes_sent.fetch_add(payload.len() as u64, Ordering::Relaxed);
}

/// Sends a sender report (or a receiver report when this side has not transmitted), carrying what we
/// received from the peer.
fn send_sender_report(sh: &Arc<Shared>) {
    let report = reception_report(sh);
    let video = sh.video.lock().is_some();
    let mut tx = sh.tx.lock();
    let mut packet = if tx.packets > 0 {
        // The two timestamps have to be the same instant, so the last one sent is carried forward
        // to now at the stream's own clock rate (RFC 3550 6.4.1).
        let rate = if video { 90_000 } else { tx.codec.as_ref().map_or(8000, |c| c.clock_rate()) };
        let ticks = (tx.timestamp_at.elapsed().as_secs_f64() * f64::from(rate)) as u32;
        build_sender_report(tx.ssrc, ntp_now(), tx.timestamp.wrapping_add(ticks), tx.packets, tx.octets, "voipnet", report)
    } else if let Some(block) = report {
        build_receiver_report(tx.ssrc, block)
    } else {
        return;
    };
    let secured = match tx.srtp.as_mut() {
        Some(ctx) => ctx.protect_rtcp(&mut packet).is_ok(),
        None => !sh.secure_required.load(Ordering::Relaxed),
    };
    drop(tx);
    if secured {
        send_raw(sh, &packet);
    }

    send_extended_report(sh);
}

/// Tells the sender of a video stream how much this side can take (REMB).
///
/// Only video streams get one: audio runs at a fixed bitrate the peer cannot usefully lower, while a
/// browser reads REMB on video and encodes to fit.
fn send_receiver_estimate(sh: &Arc<Shared>) {
    if sh.video.lock().is_none() {
        return;
    }

    let Some(media_ssrc) = sh.rx.lock().remote_ssrc else { return };
    // Video does not go through the jitter buffer, so loss is measured the way it shows up here:
    // frames that arrived with packets missing, as a share of the frames that arrived at all.
    let loss = {
        let mut track = sh.video.lock();
        let Some(track) = track.as_mut() else { return };
        let frames = track.frames.saturating_sub(track.reported_frames);
        let incomplete = track.depacketizer.incomplete_frames.saturating_sub(track.reported_incomplete);
        track.reported_frames = track.frames;
        track.reported_incomplete = track.depacketizer.incomplete_frames;
        match frames + incomplete {
            0 => 0.0,
            total => incomplete as f64 / total as f64,
        }
    };
    let bitrate = sh.estimator.lock().update(Instant::now(), sh.bytes_received.load(Ordering::Relaxed), loss);
    // What this side can take is also what decides which encoding of a simulcast stream to keep.
    choose_layer(sh, bitrate);
    let mut tx = sh.tx.lock();
    let mut packet = build_receiver_estimate(tx.ssrc, media_ssrc, bitrate);
    let secured = match tx.srtp.as_mut() {
        Some(ctx) => ctx.protect_rtcp(&mut packet).is_ok(),
        None => !sh.secure_required.load(Ordering::Relaxed),
    };
    drop(tx);
    if secured {
        send_raw(sh, &packet);
    }
}

/// Asks the peer to send a keyframe, over the same path and encryption as the other RTCP.
fn send_keyframe_request(sh: &Arc<Shared>, full: bool, sequence: u8) -> bool {
    let Some(media_ssrc) = sh.rx.lock().remote_ssrc else { return false };
    let mut tx = sh.tx.lock();
    let mut packet = build_keyframe_request(tx.ssrc, media_ssrc, full, sequence);
    let secured = match tx.srtp.as_mut() {
        Some(ctx) => ctx.protect_rtcp(&mut packet).is_ok(),
        None => !sh.secure_required.load(Ordering::Relaxed),
    };
    drop(tx);
    if !secured {
        return false;
    }
    send_raw(sh, &packet);
    true
}

/// Tells the peer how the audio sounds on this end (RFC 3611 VoIP metrics).
fn send_extended_report(sh: &Arc<Shared>) {
    let stats = {
        let rx = sh.rx.lock();
        let Some(ssrc) = rx.remote_ssrc else { return };
        (ssrc, rx.jitter.stats(), rx.burst_gap.metrics())
    };
    let (remote_ssrc, js, burst_gap) = stats;
    let total = js.received + js.lost;
    if total == 0 {
        return;
    }
    let buffer_ms = js.target_depth as u32 * sh.config.ptime_ms;
    let payload_type = sh.rx.lock().payload_type;
    let mos = estimate_mos(&js, buffer_ms, payload_type);
    let rtt = sh.remote_quality.lock().rtt_ms;
    let discarded = js.late + js.dropped_for_latency;
    let metrics = VoipMetrics {
        ssrc: remote_ssrc,
        loss_rate: (js.lost * 256 / total).min(255) as u8,
        discard_rate: (discarded * 256 / total.max(discarded)).min(255) as u8,
        burst_density: burst_gap.burst_density,
        gap_density: burst_gap.gap_density,
        burst_duration_ms: (burst_gap.burst_packets * sh.config.ptime_ms).min(65_535) as u16,
        gap_duration_ms: (burst_gap.gap_packets * sh.config.ptime_ms).min(65_535) as u16,
        round_trip_ms: rtt.min(65_535.0) as u16,
        end_system_delay_ms: (buffer_ms + sh.config.ptime_ms).min(65_535) as u16,
        // The MOS estimate comes from the same E-model as the R factor.
        r_factor: (((mos - 1.0) / 3.5 * 93.0).clamp(0.0, 100.0)) as u8,
        mos_lq: (mos * 10.0).round().clamp(10.0, 50.0) as u8,
        mos_cq: (mos * 10.0).round().clamp(10.0, 50.0) as u8,
        jb_nominal_ms: buffer_ms.min(65_535) as u16,
        jb_maximum_ms: sh.config.jitter_max_ms.min(65_535) as u16,
    };

    let mut packet = build_xr_voip_metrics(sh.tx.lock().ssrc, metrics);
    let mut tx = sh.tx.lock();
    let secured = match tx.srtp.as_mut() {
        Some(ctx) => ctx.protect_rtcp(&mut packet).is_ok(),
        None => !sh.secure_required.load(Ordering::Relaxed),
    };
    drop(tx);
    if secured {
        send_raw(sh, &packet);
    }
}

/// Describes what we received from the peer since the previous report (RFC 3550 6.4.1).
fn reception_report(sh: &Arc<Shared>) -> Option<ReportBlock> {
    let mut rx = sh.rx.lock();
    let ssrc = rx.remote_ssrc?;
    let reception = rx.jitter.reception()?;
    let (expected, received) = (reception.expected, reception.received);
    let interval_expected = expected.saturating_sub(rx.reported_expected);
    let interval_received = received.saturating_sub(rx.reported_received);
    rx.reported_expected = expected;
    rx.reported_received = received;
    let fraction_lost = if interval_expected > interval_received {
        ((interval_expected - interval_received) * 256 / interval_expected).min(255) as u8
    } else {
        0
    };
    let (last_sr, delay_since_last_sr) = match rx.last_sr {
        Some((ntp, at)) => (ntp, (at.elapsed().as_secs_f64() * 65536.0) as u32),
        None => (0, 0),
    };
    Some(ReportBlock {
        ssrc,
        fraction_lost,
        cumulative_lost: expected.saturating_sub(received) as i32,
        highest_seq: reception.highest_ext as u32,
        jitter: reception.jitter_clock,
        last_sr,
        delay_since_last_sr,
    })
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
        video: Mutex<Vec<(u32, bool, Vec<u8>, String)>>,
        events: Mutex<Vec<String>>,
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
        fn on_media_event(&self, _: u64, kind: &str, detail: &str) {
            self.events.lock().push(format!("{kind} {detail}"));
        }
        fn on_video_frame(&self, _: u64, timestamp: u32, keyframe: bool, frame: &[u8], content: &str) {
            self.video.lock().push((timestamp, keyframe, frame.to_vec(), content.to_owned()));
        }
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
            ice_controlling: false,
            ptime_ms: None,
            remote_fingerprint: None,
            dtls_role: None,
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
        // Wait for the paced stream rather than a fixed time; CI machines can be slow.
        let deadline = Instant::now() + Duration::from_secs(5);
        while b.stats().packets_received < 25 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        std::thread::sleep(Duration::from_millis(200));
        let peak = cb.inbound.lock().iter().map(|s| s.unsigned_abs()).max().unwrap_or(0);
        assert!(peak > 4000, "decoded tone too quiet: peak {peak}");
        let stats = b.stats();
        assert!(stats.packets_received >= 25, "{stats:?}");
        assert_eq!(stats.srtp_active, 1);
        assert_eq!(stats.sample_rate, 16000);
        a.stop();
        b.stop();
    }


    #[cfg(feature = "audio-processing")]
    #[test]
    fn audio_processing_keeps_the_call_audible() {
        let (ca, cb) = (Arc::new(Collect::default()), Arc::new(Collect::default()));
        let config = MediaConfig { noise_suppression: true, echo_cancellation: true, auto_gain: true, ..loopback_config() };
        let a = MediaSession::new(1, config.clone(), ca.clone()).unwrap();
        let b = MediaSession::new(2, config, cb.clone()).unwrap();
        let neg = |remote: SocketAddr| NegotiatedMedia {
            remote: Some(remote),
            codec: CodecKind::G722.rtpmap(),
            dtmf: None,
            direction: Direction::SendRecv,
            remote_srtp_key: None,
            remote_ice_ufrag: None,
            remote_ice_pwd: None,
            remote_candidates: vec![],
            ice_controlling: false,
            ptime_ms: None,
            remote_fingerprint: None,
            dtls_role: None,
        };
        a.apply(&neg(b.local_address())).unwrap();
        b.apply(&neg(a.local_address())).unwrap();

        a.send_audio(&tone(16000, 800), 16000);
        let deadline = Instant::now() + Duration::from_secs(5);
        while b.stats().packets_received < 25 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        std::thread::sleep(Duration::from_millis(200));
        let peak = cb.inbound.lock().iter().map(|s| s.unsigned_abs()).max().unwrap_or(0);
        assert!(peak > 2000, "speech was suppressed as well: peak {peak}");
        a.stop();
        b.stop();
    }


    #[test]
    fn video_frames_are_packetized_and_reassembled_over_rtp() {
        let (ca, cb) = (Arc::new(Collect::default()), Arc::new(Collect::default()));
        let a = MediaSession::new(1, loopback_config(), ca.clone()).unwrap();
        let b = MediaSession::new(2, loopback_config(), cb.clone()).unwrap();
        let map = CodecKind::H264.rtpmap();
        assert!(a.enable_video(&map.encoding, map.payload_type, "main"));
        assert!(b.enable_video(&map.encoding, map.payload_type, "main"));
        let neg = |remote: SocketAddr| NegotiatedMedia {
            remote: Some(remote),
            codec: map.clone(),
            dtmf: None,
            direction: Direction::SendRecv,
            remote_srtp_key: None,
            remote_ice_ufrag: None,
            remote_ice_pwd: None,
            remote_candidates: vec![],
            ice_controlling: false,
            ptime_ms: None,
            remote_fingerprint: None,
            dtls_role: None,
        };
        a.apply(&neg(b.local_address())).unwrap();
        b.apply(&neg(a.local_address())).unwrap();

        // A keyframe big enough to need fragmenting, then a small frame that fits one packet.
        let mut keyframe = vec![0, 0, 0, 1, 0x67, 1, 2, 3];
        keyframe.extend_from_slice(&[0, 0, 0, 1, 0x65]);
        keyframe.extend((0..4000).map(|i| (i % 251) as u8 | 1));
        let delta: Vec<u8> = [0, 0, 0, 1, 0x61, 9, 8, 7].to_vec();
        assert!(a.send_video_frame(90_000, &keyframe));
        assert!(a.send_video_frame(93_000, &delta));

        let deadline = Instant::now() + Duration::from_secs(5);
        while cb.video.lock().len() < 2 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        let received = cb.video.lock().clone();
        assert_eq!(received.len(), 2, "received {} frames", received.len());
        assert_eq!(received[0], (90_000, true, keyframe, "main".to_owned()));
        assert_eq!(received[1], (93_000, false, delta, "main".to_owned()));
        a.stop();
        b.stop();
    }

    #[test]
    fn a_keyframe_request_reaches_the_sender() {
        let (ca, cb) = (Arc::new(Collect::default()), Arc::new(Collect::default()));
        let a = MediaSession::new(1, loopback_config(), ca.clone()).unwrap();
        let b = MediaSession::new(2, loopback_config(), cb.clone()).unwrap();
        let map = CodecKind::H264.rtpmap();
        assert!(a.enable_video(&map.encoding, map.payload_type, "main"));
        assert!(b.enable_video(&map.encoding, map.payload_type, "main"));
        let neg = |remote: SocketAddr| NegotiatedMedia {
            remote: Some(remote),
            codec: map.clone(),
            dtmf: None,
            direction: Direction::SendRecv,
            remote_srtp_key: None,
            remote_ice_ufrag: None,
            remote_ice_pwd: None,
            remote_candidates: vec![],
            ice_controlling: false,
            ptime_ms: None,
            remote_fingerprint: None,
            dtls_role: None,
        };
        a.apply(&neg(b.local_address())).unwrap();
        b.apply(&neg(a.local_address())).unwrap();

        // The receiver learns the sender's SSRC from its first frame, which a request needs.
        assert!(a.send_video_frame(90_000, &[0, 0, 0, 1, 0x65, 1, 2, 3]));
        let deadline = Instant::now() + Duration::from_secs(3);
        while cb.video.lock().is_empty() && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        assert!(b.request_keyframe(false), "the request was sent");
        while !ca.events.lock().iter().any(|e| e == "keyframe-request pli") && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        assert!(ca.events.lock().iter().any(|e| e == "keyframe-request pli"), "events: {:?}", ca.events.lock());
        a.stop();
        b.stop();
    }

    #[test]
    fn rfc4733_dtmf_is_delivered_once_per_digit() {
        let (a, b, _ca, cb) = connect(CodecKind::Pcmu, false);
        a.send_dtmf("12#", 80);
        let deadline = Instant::now() + Duration::from_secs(5);
        while cb.dtmf.lock().len() < 3 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(20));
        }
        std::thread::sleep(Duration::from_millis(300)); // a duplicate report would show up here
        assert_eq!(cb.dtmf.lock().iter().collect::<String>(), "12#");
        a.stop();
        b.stop();
    }


    #[test]
    fn the_encoding_kept_is_the_biggest_that_fits() {
        let layers = [("h".to_owned(), 900_000u64), ("m".to_owned(), 300_000), ("l".to_owned(), 90_000)];

        assert_eq!(best_layer(&layers, 2_000_000, None).as_deref(), Some("h"), "plenty of room: the best picture");
        assert_eq!(best_layer(&layers, 500_000, None).as_deref(), Some("m"), "enough for the middle one");
        assert_eq!(best_layer(&layers, 120_000, None).as_deref(), Some("l"));
        // Nothing fits: the smallest encoding is still better than a blank screen.
        assert_eq!(best_layer(&layers, 40_000, None).as_deref(), Some("l"));
        assert_eq!(best_layer(&[], 500_000, None), None);
    }

    #[test]
    fn an_encoding_that_nearly_fits_is_kept_rather_than_swapped_back_and_forth() {
        // A budget of 600 kbit/s with an encoding measured either side of it: whichever way the
        // estimate moves, the viewer keeps the picture they already have rather than losing it to a
        // keyframe wait every second.
        let over = [("h".to_owned(), 620_000u64), ("l".to_owned(), 90_000)];
        let under = [("h".to_owned(), 540_000u64), ("l".to_owned(), 90_000)];
        assert_eq!(best_layer(&under, 600_000, Some("h")).as_deref(), Some("h"), "it still fits");
        assert_eq!(best_layer(&over, 600_000, Some("h")).as_deref(), Some("l"), "it no longer does");
        // Going back up waits until the bigger encoding fits with room to spare, not the moment it
        // squeezes in, so one good second does not undo the decision.
        let squeezed = [("h".to_owned(), 530_000u64), ("l".to_owned(), 90_000)];
        assert_eq!(best_layer(&squeezed, 600_000, Some("l")).as_deref(), Some("l"), "not yet");
        let roomy = [("h".to_owned(), 380_000u64), ("l".to_owned(), 90_000)];
        assert_eq!(best_layer(&roomy, 600_000, Some("l")).as_deref(), Some("h"), "now there is room");
    }

    #[test]
    fn an_unlabelled_packet_is_placed_by_the_stream_it_arrives_on() {
        // Chrome labels its simulcast packets with an encoding name until it is satisfied the labels
        // arrived, then sends the same streams unlabelled. Without remembering which stream is which,
        // every frame after that would be reassembled with the other encodings' packets and thrown
        // away — which is a keyframe request for every frame, for the rest of the call.
        let mut track = VideoTrack {
            payload_type: 96,
            content: "main".into(),
            packetizer: VideoPacketizer::new(VideoFormat::Vp8),
            depacketizer: VideoDepacketizer::new(VideoFormat::Vp8),
            format: VideoFormat::Vp8,
            rid_extension: Some(10),
            send_rid_extension: None,
            send_rids: Vec::new(),
            layers: Vec::new(),
            selected: None,
            layer_changed_at: None,
            incomplete_seen: 0,
            frames: 0,
            reported_frames: 0,
            reported_incomplete: 0,
            last_request: None,
            request_backoff: KEYFRAME_REQUEST_INTERVAL,
            fir_sequence: 0,
        };

        let now = Instant::now();
        let frame = |seed: u8| {
            let mut payload = vec![0x10, 0x02, 0x00];
            payload.extend((0..40).map(|i| i as u8 ^ seed));
            payload
        };
        let header = |ssrc: u32, sequence: u16, timestamp: u32| RtpHeader {
            marker: true,
            payload_type: 96,
            sequence,
            timestamp,
            ssrc,
        };

        // Labelled: the first encoding seen is the one kept.
        assert!(track.push(Some("h"), &header(11, 1, 9000), &frame(1), now).0.is_some());
        assert!(track.push(Some("l"), &header(22, 1, 9000), &frame(2), now).0.is_none());

        // Unlabelled, from the same two streams: the chosen one still arrives, the other is still dropped.
        let (kept, lost) = track.push(None, &header(11, 2, 12_000), &frame(3), now);
        assert!(kept.is_some() && !lost, "the chosen encoding carries on unlabelled");
        assert!(track.push(None, &header(22, 2, 12_000), &frame(4), now).0.is_none(), "the other one is still dropped");
        assert_eq!(track.measured().len(), 2, "both are still measured: {:?}", track.measured());
    }

    #[test]
    fn packets_are_reassembled_per_encoding_and_only_one_is_passed_on() {
        let mut track = VideoTrack {
            payload_type: 96,
            content: "main".into(),
            packetizer: VideoPacketizer::new(VideoFormat::Vp8),
            depacketizer: VideoDepacketizer::new(VideoFormat::Vp8),
            format: VideoFormat::Vp8,
            rid_extension: Some(10),
            send_rid_extension: None,
            send_rids: Vec::new(),
            layers: Vec::new(),
            selected: None,
            layer_changed_at: None,
            incomplete_seen: 0,
            frames: 0,
            reported_frames: 0,
            reported_incomplete: 0,
            last_request: None,
            request_backoff: KEYFRAME_REQUEST_INTERVAL,
            fir_sequence: 0,
        };

        let now = Instant::now();
        let frame = |seed: u8| {
            let mut payload = vec![0x10, 0x02, 0x00];
            payload.extend((0..40).map(|i| i as u8 ^ seed));
            payload
        };
        let header = |sequence: u16, timestamp: u32| RtpHeader {
            marker: true,
            payload_type: 96,
            sequence,
            timestamp,
            ssrc: 1,
        };

        // The first encoding seen is the one kept, because a sender may send fewer than it offered.
        let (first, _) = track.push(Some("h"), &header(1, 9000), &frame(1), now);
        assert!(first.is_some(), "the first encoding to arrive is passed on");
        assert_eq!(track.selected.as_deref(), Some("h"));

        // A second encoding is measured but dropped: the viewers get one picture, not three.
        let (other, _) = track.push(Some("l"), &header(500, 12_000), &frame(2), now);
        assert!(other.is_none(), "the encoding that was not chosen is dropped");
        assert_eq!(track.measured().len(), 2, "both encodings are known: {:?}", track.measured());

        // Each encoding has its own sequence numbers, so the chosen one carries on undisturbed.
        let (again, lost) = track.push(Some("h"), &header(2, 12_000), &frame(3), now);
        assert!(again.is_some() && !lost, "the chosen encoding keeps flowing");
    }

    #[test]
    fn the_bandwidth_estimate_grows_on_a_clean_stream_and_falls_under_loss() {
        let start = Instant::now();
        let mut estimator = BandwidthEstimator::new();
        let mut bytes = 0u64;
        // 1 Mbit/s arriving cleanly: the estimate climbs to a little over what is being sent.
        let mut clean = 0;
        for second in 1..=12 {
            bytes += 125_000;
            clean = estimator.update(start + Duration::from_secs(second), bytes, 0.0);
        }

        assert!(clean > 1_000_000, "a clean megabit should be allowed at least itself, got {clean}");
        assert!(clean < 2_000_000, "and not far more than the sender is using, got {clean}");

        // Heavy loss: the estimate is cut back rather than held.
        let mut lossy = clean;
        for second in 13..=16 {
            bytes += 125_000;
            lossy = estimator.update(start + Duration::from_secs(second), bytes, 0.30);
        }

        // The rule is a 15% cut a second at this loss rate, so four seconds take roughly half of it.
        assert!(lossy < clean * 3 / 5, "{lossy} should be well below {clean}");
        assert!(lossy >= 64_000, "but never below the floor, got {lossy}");

        // A little loss is normal on any path and must not move the estimate.
        let held = estimator.update(start + Duration::from_secs(17), bytes + 125_000, 0.05);
        assert_eq!(held, lossy);
    }

    #[test]
    fn rtcp_reports_carry_loss_and_round_trip() {
        let (a, b, _ca, _cb) = connect(CodecKind::Pcmu, false);
        a.send_audio(&tone(8000, 9000), 8000);
        b.send_audio(&tone(8000, 9000), 8000);
        // Reports start after a second and repeat every four; two rounds are needed for a round trip.
        let deadline = Instant::now() + Duration::from_secs(12);
        while a.stats().remote_mos == 0.0 && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(100));
        }
        let stats = a.stats();
        // On loopback the round trip is under a millisecond, which the RTCP clock may report as zero.
        assert!(stats.round_trip_ms < 500.0, "rtt {} ms", stats.round_trip_ms);
        assert!(stats.remote_loss_percent < 5.0, "loss {}%", stats.remote_loss_percent);
        // The peer also reports how the audio sounds on its side (RTCP XR).
        assert!(stats.remote_mos > 3.0, "remote MOS {}", stats.remote_mos);
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
