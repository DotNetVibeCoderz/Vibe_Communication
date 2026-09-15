//! Lock-free multi-speaker mixer.
//!
//! The mixer is split into two halves connected by wait-free SPSC ring buffers:
//!
//! * [`VoiceRouter`] lives on the network side. It routes decrypted voice packets to per-speaker
//!   queues and sends control commands (volume, listener pose, …).
//! * [`Mixer`] lives on the audio thread (device callback or a headless ticker). Every call to
//!   [`Mixer::mix`] drains the queues into per-speaker jitter buffers, decodes / conceals exactly
//!   10 ms of audio per speaker, applies volume and positional gains and sums the result.
//!
//! No locks are taken and no memory is allocated on the audio thread after a speaker is created.

use std::collections::HashMap;
use std::time::Instant;

use bytes::Bytes;
use rtrb::{Consumer, Producer, RingBuffer};

use crate::codec::{StreamCodec, StreamDecoder};
use crate::dsp::soft_clip;
use crate::jitter::{BufferedPacket, JitterBuffer, JitterConfig, JitterStats, Playout};
use crate::positional::{Listener, PositionalSettings, StereoGain, compute_gains};
use crate::{FRAME_SIZE, MAX_FRAME_SAMPLES};

/// A received voice packet on its way to the audio thread.
#[derive(Debug, Clone)]
pub struct IncomingVoice {
    pub codec: StreamCodec,
    pub sequence: u64,
    pub payload: Bytes,
    pub position: Option<[f32; 3]>,
    pub is_terminator: bool,
    pub volume_adjustment: f32,
    /// Monotonic arrival time in microseconds (see [`VoiceRouter::now_us`]).
    pub arrival_us: i64,
}

/// Commands from the control side to the audio thread.
enum MixerCommand {
    AddSpeaker { session: u32, queue: Consumer<IncomingVoice> },
    RemoveSpeaker(u32),
    SetUserVolume(u32, f32),
    SetUserMuted(u32, bool),
    SetListener(Listener),
    SetPositionalSettings(PositionalSettings),
    SetPositionalEnabled(bool),
    SetMasterVolume(f32),
    SetDeafened(bool),
    Clear,
}

/// Mixer configuration.
#[derive(Debug, Clone, Copy)]
pub struct MixerConfig {
    pub jitter: JitterConfig,
    /// Per-speaker packet queue capacity.
    pub speaker_queue: usize,
    /// Control command queue capacity.
    pub command_queue: usize,
}

impl Default for MixerConfig {
    fn default() -> Self {
        Self { jitter: JitterConfig::default(), speaker_queue: 128, command_queue: 1024 }
    }
}

/// Decoded audio of one speaker for one 10 ms frame, delivered to the optional frame sink.
#[derive(Debug)]
pub struct SpeakerFrame<'a> {
    pub session: u32,
    /// Mono 48 kHz samples (always [`FRAME_SIZE`] long).
    pub samples: &'a [f32],
    pub position: Option<[f32; 3]>,
    /// True if (part of) this frame was synthesized by packet loss concealment.
    pub concealed: bool,
}

/// Callback receiving every decoded speaker frame (bots, recording, visualisation).
pub type FrameSink = Box<dyn FnMut(&SpeakerFrame<'_>) + Send>;

/// Creates a connected router/mixer pair.
pub fn channel(config: MixerConfig) -> (VoiceRouter, Mixer) {
    let (tx, rx) = RingBuffer::new(config.command_queue);
    let router = VoiceRouter { config, speakers: HashMap::new(), commands: tx, epoch: Instant::now(), dropped: 0 };
    let mixer = Mixer {
        config,
        commands: rx,
        speakers: Vec::with_capacity(16),
        listener: Listener::default(),
        positional: PositionalSettings::default(),
        positional_enabled: false,
        master_volume: 1.0,
        deafened: false,
        frame: vec![0.0; FRAME_SIZE],
        positions: Vec::with_capacity(16),
        gains: Vec::with_capacity(16),
        sink: None,
    };
    (router, mixer)
}

/// Network-side half of the mixer. See module docs.
pub struct VoiceRouter {
    config: MixerConfig,
    speakers: HashMap<u32, Producer<IncomingVoice>>,
    commands: Producer<MixerCommand>,
    epoch: Instant,
    dropped: u64,
}

impl VoiceRouter {
    /// Monotonic timestamp suitable for [`IncomingVoice::arrival_us`].
    pub fn now_us(&self) -> i64 {
        self.epoch.elapsed().as_micros() as i64
    }

    /// Packets dropped because a queue was full.
    pub fn dropped_packets(&self) -> u64 {
        self.dropped
    }

    fn send(&mut self, cmd: MixerCommand) -> bool {
        if self.commands.push(cmd).is_err() {
            self.dropped += 1;
            false
        } else {
            true
        }
    }

    /// Routes a packet for `session`, creating the speaker stream on first use.
    pub fn route(&mut self, session: u32, voice: IncomingVoice) -> bool {
        if !self.speakers.contains_key(&session) {
            let (tx, rx) = RingBuffer::new(self.config.speaker_queue);
            if !self.send(MixerCommand::AddSpeaker { session, queue: rx }) {
                return false;
            }
            self.speakers.insert(session, tx);
        }
        let queue = self.speakers.get_mut(&session).expect("speaker inserted above");
        if queue.is_abandoned() {
            // The audio side dropped the speaker; recreate on next packet.
            self.speakers.remove(&session);
            return self.route(session, voice);
        }
        if queue.push(voice).is_err() {
            self.dropped += 1;
            return false;
        }
        true
    }

    pub fn remove_speaker(&mut self, session: u32) {
        if self.speakers.remove(&session).is_some() {
            self.send(MixerCommand::RemoveSpeaker(session));
        }
    }

    /// Local per-user volume (linear, 1.0 = unchanged).
    pub fn set_user_volume(&mut self, session: u32, volume: f32) {
        self.send(MixerCommand::SetUserVolume(session, volume.max(0.0)));
    }

    /// Local mute of a user (does not affect the server).
    pub fn set_user_muted(&mut self, session: u32, muted: bool) {
        self.send(MixerCommand::SetUserMuted(session, muted));
    }

    pub fn set_listener(&mut self, listener: Listener) {
        self.send(MixerCommand::SetListener(listener));
    }

    pub fn set_positional_settings(&mut self, settings: PositionalSettings) {
        self.send(MixerCommand::SetPositionalSettings(settings));
    }

    pub fn set_positional_enabled(&mut self, enabled: bool) {
        self.send(MixerCommand::SetPositionalEnabled(enabled));
    }

    pub fn set_master_volume(&mut self, volume: f32) {
        self.send(MixerCommand::SetMasterVolume(volume.max(0.0)));
    }

    /// When deafened the mixer outputs silence but still feeds the frame sink.
    pub fn set_deafened(&mut self, deafened: bool) {
        self.send(MixerCommand::SetDeafened(deafened));
    }

    pub fn clear(&mut self) {
        self.speakers.clear();
        self.send(MixerCommand::Clear);
    }
}

struct Speaker {
    session: u32,
    queue: Consumer<IncomingVoice>,
    jitter: JitterBuffer,
    decoder: Option<StreamDecoder>,
    pcm: Box<[f32]>,
    pcm_len: usize,
    pcm_pos: usize,
    position: Option<[f32; 3]>,
    volume: f32,
    server_volume: f32,
    muted: bool,
    removed: bool,
}

impl Speaker {
    fn new(session: u32, queue: Consumer<IncomingVoice>, jitter: JitterConfig) -> Self {
        Self {
            session,
            queue,
            jitter: JitterBuffer::new(jitter),
            decoder: None,
            pcm: vec![0.0; MAX_FRAME_SAMPLES].into_boxed_slice(),
            pcm_len: 0,
            pcm_pos: 0,
            position: None,
            volume: 1.0,
            server_volume: 1.0,
            muted: false,
            removed: false,
        }
    }

    fn ensure_decoder(&mut self, codec: StreamCodec) {
        if self.decoder.as_ref().map(StreamDecoder::codec) != Some(codec) {
            self.decoder = StreamDecoder::new(codec).ok();
            self.pcm_len = 0;
            self.pcm_pos = 0;
        }
    }

    fn drain_queue(&mut self) {
        while let Ok(v) = self.queue.pop() {
            self.ensure_decoder(v.codec);
            let frames = self.decoder.as_ref().map_or(1, |d| d.frames_in(&v.payload));
            self.jitter.insert(
                BufferedPacket {
                    sequence: v.sequence,
                    frames,
                    payload: v.payload,
                    position: v.position,
                    is_terminator: v.is_terminator,
                    volume_adjustment: v.volume_adjustment,
                },
                v.arrival_us,
            );
        }
    }

    /// Renders one frame into `out`. Returns `(has_audio, concealed)`.
    fn render(&mut self, out: &mut [f32]) -> (bool, bool) {
        let mut written = 0;
        let mut concealed = false;
        let mut has_audio = false;
        while written < out.len() {
            if self.pcm_pos < self.pcm_len {
                let n = (self.pcm_len - self.pcm_pos).min(out.len() - written);
                out[written..written + n].copy_from_slice(&self.pcm[self.pcm_pos..self.pcm_pos + n]);
                self.pcm_pos += n;
                written += n;
                has_audio = true;
                continue;
            }
            let Some(decoder) = self.decoder.as_mut() else { break };
            match self.jitter.pop() {
                Playout::Packet(p) => {
                    if p.position.is_some() {
                        self.position = p.position;
                    }
                    self.server_volume = if p.volume_adjustment > 0.0 { p.volume_adjustment } else { 1.0 };
                    self.pcm_len = decoder.decode(&p.payload, &mut self.pcm);
                    self.pcm_pos = 0;
                    if self.pcm_len == 0 {
                        self.pcm_len = decoder.conceal(&mut self.pcm);
                        concealed = true;
                    }
                }
                Playout::Lost => {
                    self.pcm_len = decoder.conceal(&mut self.pcm);
                    self.pcm_pos = 0;
                    concealed = true;
                }
                Playout::Waiting | Playout::Ended => break,
            }
        }
        out[written..].fill(0.0);
        (has_audio, concealed)
    }
}

/// Audio-thread half of the mixer. See module docs.
pub struct Mixer {
    config: MixerConfig,
    commands: Consumer<MixerCommand>,
    speakers: Vec<Speaker>,
    listener: Listener,
    positional: PositionalSettings,
    positional_enabled: bool,
    master_volume: f32,
    deafened: bool,
    frame: Vec<f32>,
    positions: Vec<[f32; 3]>,
    gains: Vec<StereoGain>,
    sink: Option<FrameSink>,
}

impl Mixer {
    /// Installs a callback receiving every decoded speaker frame.
    pub fn set_frame_sink(&mut self, sink: Option<FrameSink>) {
        self.sink = sink;
    }

    /// Number of speakers currently tracked.
    pub fn speaker_count(&self) -> usize {
        self.speakers.len()
    }

    /// Jitter statistics for a speaker.
    pub fn jitter_stats(&self, session: u32) -> Option<JitterStats> {
        self.speakers.iter().find(|s| s.session == session).map(|s| s.jitter.stats())
    }

    fn apply_commands(&mut self) {
        while let Ok(cmd) = self.commands.pop() {
            match cmd {
                MixerCommand::AddSpeaker { session, queue } => {
                    if let Some(existing) = self.speakers.iter_mut().find(|s| s.session == session) {
                        existing.queue = queue;
                        existing.removed = false;
                        existing.jitter.clear();
                    } else {
                        self.speakers.push(Speaker::new(session, queue, self.config.jitter));
                    }
                }
                MixerCommand::RemoveSpeaker(session) => {
                    if let Some(s) = self.speakers.iter_mut().find(|s| s.session == session) {
                        s.removed = true;
                    }
                }
                MixerCommand::SetUserVolume(session, v) => {
                    if let Some(s) = self.speakers.iter_mut().find(|s| s.session == session) {
                        s.volume = v;
                    }
                }
                MixerCommand::SetUserMuted(session, m) => {
                    if let Some(s) = self.speakers.iter_mut().find(|s| s.session == session) {
                        s.muted = m;
                    }
                }
                MixerCommand::SetListener(l) => self.listener = l,
                MixerCommand::SetPositionalSettings(p) => self.positional = p,
                MixerCommand::SetPositionalEnabled(e) => self.positional_enabled = e,
                MixerCommand::SetMasterVolume(v) => self.master_volume = v,
                MixerCommand::SetDeafened(d) => self.deafened = d,
                MixerCommand::Clear => self.speakers.clear(),
            }
        }
    }

    /// Mixes exactly one 10 ms frame into `out` (`FRAME_SIZE * channels` samples, interleaved).
    /// `channels` must be 1 or 2. Returns the number of speakers that produced audio.
    pub fn mix(&mut self, out: &mut [f32], channels: usize) -> usize {
        debug_assert!(channels == 1 || channels == 2);
        debug_assert_eq!(out.len(), FRAME_SIZE * channels);
        self.apply_commands();
        out.fill(0.0);

        // Drop speakers that were removed and have nothing left to play.
        self.speakers.retain(|s| !(s.removed && !s.jitter.is_active() && s.queue.is_empty()));

        // Batch-compute positional gains.
        let spatial = self.positional_enabled && channels == 2;
        if spatial {
            self.positions.clear();
            self.positions.extend(self.speakers.iter().map(|s| s.position.unwrap_or(self.listener.position)));
            self.gains.resize(self.speakers.len(), StereoGain::CENTER);
            compute_gains(&self.listener, &self.positional, &self.positions, &mut self.gains);
        }

        let mut active = 0;
        for (i, speaker) in self.speakers.iter_mut().enumerate() {
            speaker.drain_queue();
            let (has_audio, concealed) = speaker.render(&mut self.frame);
            if !has_audio && !concealed {
                continue;
            }
            active += 1;

            if let Some(sink) = self.sink.as_mut() {
                sink(&SpeakerFrame {
                    session: speaker.session,
                    samples: &self.frame,
                    position: speaker.position,
                    concealed,
                });
            }

            if speaker.muted || self.deafened {
                continue;
            }
            let volume = speaker.volume * speaker.server_volume * self.master_volume;
            if channels == 1 {
                for (o, s) in out.iter_mut().zip(&self.frame) {
                    *o += s * volume;
                }
            } else {
                let g = if spatial && speaker.position.is_some() { self.gains[i] } else { StereoGain::CENTER };
                let (gl, gr) = (g.left * volume, g.right * volume);
                for (o, s) in out.chunks_exact_mut(2).zip(&self.frame) {
                    o[0] += s * gl;
                    o[1] += s * gr;
                }
            }
        }

        if active > 0 {
            soft_clip(out);
        }
        active
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::codec::{OpusApplication, OpusEncoder};

    fn encode_sine(enc: &mut OpusEncoder, i: usize) -> Bytes {
        let frame: Vec<f32> = (0..FRAME_SIZE)
            .map(|n| (((i * FRAME_SIZE + n) as f32) * 440.0 * std::f32::consts::TAU / 48_000.0).sin() * 0.4)
            .collect();
        let mut buf = [0u8; crate::MAX_OPUS_PACKET];
        let len = enc.encode_float(&frame, &mut buf).unwrap();
        Bytes::copy_from_slice(&buf[..len])
    }

    #[test]
    fn mixes_two_speakers_with_sink() {
        let (mut router, mut mixer) = channel(MixerConfig::default());
        let frames = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let f2 = frames.clone();
        mixer.set_frame_sink(Some(Box::new(move |f: &SpeakerFrame<'_>| {
            assert_eq!(f.samples.len(), FRAME_SIZE);
            f2.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        })));

        let mut enc_a = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
        let mut enc_b = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
        let mut out = vec![0f32; FRAME_SIZE * 2];
        let mut energy = 0.0;
        for i in 0..60 {
            for (session, enc) in [(1u32, &mut enc_a), (2u32, &mut enc_b)] {
                let payload = encode_sine(enc, i);
                router.route(
                    session,
                    IncomingVoice {
                        codec: StreamCodec::Opus,
                        sequence: i as u64,
                        payload,
                        position: Some([session as f32 * 2.0 - 3.0, 0.0, 1.0]),
                        is_terminator: i == 59,
                        volume_adjustment: 0.0,
                        arrival_us: i as i64 * 10_000,
                    },
                );
            }
            router.set_positional_enabled(true);
            mixer.mix(&mut out, 2);
            energy += out.iter().map(|s| s * s).sum::<f32>();
        }
        assert_eq!(mixer.speaker_count(), 2);
        assert!(energy > 1.0, "energy {energy}");
        assert!(frames.load(std::sync::atomic::Ordering::Relaxed) > 80);
    }

    #[test]
    fn removed_speaker_is_dropped() {
        let (mut router, mut mixer) = channel(MixerConfig::default());
        router.route(
            7,
            IncomingVoice {
                codec: StreamCodec::Pcm,
                sequence: 0,
                payload: Bytes::from(vec![0u8; FRAME_SIZE * 2]),
                position: None,
                is_terminator: true,
                volume_adjustment: 0.0,
                arrival_us: 0,
            },
        );
        let mut out = vec![0f32; FRAME_SIZE];
        mixer.mix(&mut out, 1);
        assert_eq!(mixer.speaker_count(), 1);
        router.remove_speaker(7);
        for _ in 0..3 {
            mixer.mix(&mut out, 1);
        }
        assert_eq!(mixer.speaker_count(), 0);
    }
}
