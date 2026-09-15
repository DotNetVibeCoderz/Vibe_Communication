//! Capture pipeline: DSP chain → transmit decision (continuous / VAD / push-to-talk) →
//! packetizer → Opus encoder → packet sink.
//!
//! The pipeline accepts arbitrary-sized chunks of mono 48 kHz samples via
//! [`CapturePipeline::push`], so it can be driven by a device callback or by an application
//! pushing PCM directly (bots, file playback, TTS).

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicU32, Ordering};

use bytes::Bytes;

use crate::codec::{OpusApplication, OpusEncoder, StreamCodec, encode_pcm};
use crate::dsp::{AudioProcessor, DcBlocker, NoiseGate, VoiceActivityDetector};
use crate::{FRAME_SIZE, MAX_OPUS_PACKET, Result};

/// When the local user transmits.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum TransmitMode {
    /// Always transmit.
    Continuous = 0,
    /// Transmit when the voice activity detector triggers.
    #[default]
    VoiceActivity = 1,
    /// Transmit while push-to-talk is held.
    PushToTalk = 2,
}

impl From<u8> for TransmitMode {
    fn from(v: u8) -> Self {
        match v {
            0 => TransmitMode::Continuous,
            2 => TransmitMode::PushToTalk,
            _ => TransmitMode::VoiceActivity,
        }
    }
}

/// Capture configuration.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct CaptureConfig {
    pub codec: StreamCodec,
    /// Opus bitrate in bits/s.
    pub bitrate: i32,
    /// 10 ms frames per packet: 1, 2, 4 or 6 (10–60 ms). Higher = less overhead, more latency.
    pub frames_per_packet: usize,
    pub complexity: i32,
    pub inband_fec: bool,
    pub expected_packet_loss: i32,
    pub vad_threshold_db: f32,
    pub vad_hold_frames: u32,
    /// Optional noise gate threshold (dBFS).
    pub noise_gate_db: Option<f32>,
    /// Remove DC offset.
    pub dc_filter: bool,
}

impl Default for CaptureConfig {
    fn default() -> Self {
        Self {
            codec: StreamCodec::Opus,
            bitrate: 48_000,
            frames_per_packet: 2,
            complexity: 8,
            inband_fec: true,
            expected_packet_loss: 5,
            vad_threshold_db: -45.0,
            vad_hold_frames: 25,
            noise_gate_db: None,
            dc_filter: true,
        }
    }
}

/// Runtime controls shared between the application and the capture thread (lock-free).
#[derive(Debug)]
pub struct CaptureControl {
    mode: AtomicU8,
    push_to_talk: AtomicBool,
    muted: AtomicBool,
    target: AtomicU8,
    transmitting: AtomicBool,
    level_db_bits: AtomicU32,
}

impl Default for CaptureControl {
    fn default() -> Self {
        Self {
            mode: AtomicU8::new(TransmitMode::VoiceActivity as u8),
            push_to_talk: AtomicBool::new(false),
            muted: AtomicBool::new(false),
            target: AtomicU8::new(0),
            transmitting: AtomicBool::new(false),
            level_db_bits: AtomicU32::new((-96.0f32).to_bits()),
        }
    }
}

impl CaptureControl {
    pub fn set_mode(&self, mode: TransmitMode) {
        self.mode.store(mode as u8, Ordering::Relaxed);
    }
    pub fn mode(&self) -> TransmitMode {
        TransmitMode::from(self.mode.load(Ordering::Relaxed))
    }
    pub fn set_push_to_talk(&self, pressed: bool) {
        self.push_to_talk.store(pressed, Ordering::Relaxed);
    }
    pub fn set_muted(&self, muted: bool) {
        self.muted.store(muted, Ordering::Relaxed);
    }
    pub fn is_muted(&self) -> bool {
        self.muted.load(Ordering::Relaxed)
    }
    /// Voice target (0 = normal talking, 1..=30 whisper/shout targets, 31 = server loopback).
    pub fn set_target(&self, target: u8) {
        self.target.store(target.min(31), Ordering::Relaxed);
    }
    pub fn target(&self) -> u8 {
        self.target.load(Ordering::Relaxed)
    }
    pub fn is_transmitting(&self) -> bool {
        self.transmitting.load(Ordering::Relaxed)
    }
    /// Last input level in dBFS.
    pub fn level_db(&self) -> f32 {
        f32::from_bits(self.level_db_bits.load(Ordering::Relaxed))
    }
}

/// An encoded outgoing voice packet.
#[derive(Debug, Clone, PartialEq)]
pub struct EncodedVoice {
    pub codec: StreamCodec,
    pub target: u8,
    pub sequence: u64,
    pub payload: Bytes,
    pub is_terminator: bool,
    pub frames: usize,
}

/// Callback receiving encoded packets. Must not block.
pub type PacketSink = Box<dyn FnMut(EncodedVoice) + Send>;

/// See module documentation.
pub struct CapturePipeline {
    config: CaptureConfig,
    control: Arc<CaptureControl>,
    encoder: Option<OpusEncoder>,
    dc: DcBlocker,
    gate: Option<NoiseGate>,
    vad: VoiceActivityDetector,
    processors: Vec<Box<dyn AudioProcessor>>,
    frame: Vec<f32>,
    frame_fill: usize,
    pending: Vec<f32>,
    pending_frames: usize,
    sequence: u64,
    was_transmitting: bool,
    packet_buf: Vec<u8>,
    sink: PacketSink,
}

impl CapturePipeline {
    pub fn new(config: CaptureConfig, control: Arc<CaptureControl>, sink: PacketSink) -> Result<Self> {
        let frames_per_packet = match config.frames_per_packet {
            0 | 1 => 1,
            2 | 3 => 2,
            4 | 5 => 4,
            _ => 6,
        };
        let config = CaptureConfig { frames_per_packet, ..config };
        let encoder = match config.codec {
            StreamCodec::Opus => {
                let mut e = OpusEncoder::new(1, OpusApplication::Voip)?;
                e.set_bitrate(config.bitrate)?;
                e.set_vbr(true)?;
                e.set_complexity(config.complexity)?;
                e.set_inband_fec(config.inband_fec)?;
                e.set_packet_loss_percent(config.expected_packet_loss)?;
                Some(e)
            }
            _ => None,
        };
        Ok(Self {
            gate: config.noise_gate_db.map(NoiseGate::new),
            vad: VoiceActivityDetector::new(config.vad_threshold_db, config.vad_hold_frames),
            config,
            control,
            encoder,
            dc: DcBlocker::default(),
            processors: Vec::new(),
            frame: vec![0.0; FRAME_SIZE],
            frame_fill: 0,
            pending: Vec::with_capacity(FRAME_SIZE * frames_per_packet),
            pending_frames: 0,
            sequence: 0,
            was_transmitting: false,
            packet_buf: vec![0; MAX_OPUS_PACKET],
            sink,
        })
    }

    pub fn control(&self) -> &Arc<CaptureControl> {
        &self.control
    }

    /// Appends a custom processor to the DSP chain (runs after the built-in filters).
    pub fn add_processor(&mut self, processor: Box<dyn AudioProcessor>) {
        self.processors.push(processor);
    }

    pub fn clear_processors(&mut self) {
        self.processors.clear();
    }

    /// Changes the Opus bitrate at runtime.
    pub fn set_bitrate(&mut self, bitrate: i32) -> Result<()> {
        self.config.bitrate = bitrate;
        if let Some(e) = self.encoder.as_mut() {
            e.set_bitrate(bitrate)?;
        }
        Ok(())
    }

    /// Pushes mono 48 kHz samples of any length.
    pub fn push(&mut self, mut samples: &[f32]) {
        while !samples.is_empty() {
            let n = (FRAME_SIZE - self.frame_fill).min(samples.len());
            self.frame[self.frame_fill..self.frame_fill + n].copy_from_slice(&samples[..n]);
            self.frame_fill += n;
            samples = &samples[n..];
            if self.frame_fill == FRAME_SIZE {
                self.frame_fill = 0;
                self.process_frame();
            }
        }
    }

    /// Pushes interleaved `i16` PCM (mono).
    pub fn push_i16(&mut self, samples: &[i16]) {
        let mut tmp = [0f32; FRAME_SIZE];
        for chunk in samples.chunks(FRAME_SIZE) {
            crate::dsp::i16_to_f32(chunk, &mut tmp[..chunk.len()]);
            self.push(&tmp[..chunk.len()]);
        }
    }

    fn process_frame(&mut self) {
        let mut frame = std::mem::take(&mut self.frame);

        if self.config.dc_filter {
            self.dc.process(&mut frame);
        }
        if let Some(g) = self.gate.as_mut() {
            g.process(&mut frame);
        }
        for p in &mut self.processors {
            p.process(&mut frame);
        }

        let vad_active = self.vad.is_active(&frame);
        self.control.level_db_bits.store(self.vad.level_db.to_bits(), Ordering::Relaxed);

        let wants = !self.control.is_muted()
            && match self.control.mode() {
                TransmitMode::Continuous => true,
                TransmitMode::VoiceActivity => vad_active,
                TransmitMode::PushToTalk => self.control.push_to_talk.load(Ordering::Relaxed),
            };

        if wants || self.was_transmitting {
            self.pending.extend_from_slice(&frame);
            self.pending_frames += 1;
            let terminator = !wants;
            if self.pending_frames >= self.config.frames_per_packet || terminator {
                self.flush(terminator);
            }
        }

        self.was_transmitting = wants;
        self.control.transmitting.store(wants, Ordering::Relaxed);
        self.frame = frame;
    }

    fn flush(&mut self, terminator: bool) {
        if self.pending_frames == 0 {
            return;
        }
        // Opus only accepts 10/20/40/60 ms frames: pad partial packets with silence.
        let valid = [1usize, 2, 4, 6].into_iter().find(|&f| f >= self.pending_frames).unwrap_or(6);
        self.pending.resize(valid * FRAME_SIZE, 0.0);

        let payload = match self.config.codec {
            StreamCodec::Opus => {
                let enc = self.encoder.as_mut().expect("opus encoder");
                match enc.encode_float(&self.pending, &mut self.packet_buf) {
                    Ok(n) => Bytes::copy_from_slice(&self.packet_buf[..n]),
                    Err(_) => {
                        self.pending.clear();
                        self.pending_frames = 0;
                        return;
                    }
                }
            }
            _ => {
                let mut v = Vec::new();
                encode_pcm(&self.pending, &mut v);
                Bytes::from(v)
            }
        };

        let packet = EncodedVoice {
            codec: self.config.codec,
            target: self.control.target(),
            sequence: self.sequence,
            payload,
            is_terminator: terminator,
            frames: valid,
        };
        self.sequence += valid as u64;
        self.pending.clear();
        self.pending_frames = 0;
        if terminator {
            if let Some(e) = self.encoder.as_mut() {
                e.reset();
            }
        }
        (self.sink)(packet);
    }

    /// Ends the current transmission immediately (sends a terminator if talking).
    pub fn end_transmission(&mut self) {
        if self.was_transmitting {
            if self.pending_frames == 0 {
                self.pending.extend(std::iter::repeat_n(0.0, FRAME_SIZE));
                self.pending_frames = 1;
            }
            self.flush(true);
            self.was_transmitting = false;
            self.control.transmitting.store(false, Ordering::Relaxed);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    fn pipeline(mode: TransmitMode) -> (CapturePipeline, Arc<Mutex<Vec<EncodedVoice>>>) {
        let out = Arc::new(Mutex::new(Vec::new()));
        let o2 = out.clone();
        let control = Arc::new(CaptureControl::default());
        control.set_mode(mode);
        let p = CapturePipeline::new(
            CaptureConfig::default(),
            control,
            Box::new(move |v| o2.lock().unwrap().push(v)),
        )
        .unwrap();
        (p, out)
    }

    fn tone(len: usize) -> Vec<f32> {
        (0..len).map(|i| (i as f32 * 0.05).sin() * 0.5).collect()
    }

    #[test]
    fn continuous_packetizes_20ms() {
        let (mut p, out) = pipeline(TransmitMode::Continuous);
        p.push(&tone(FRAME_SIZE * 10 + 100));
        let packets = out.lock().unwrap();
        assert_eq!(packets.len(), 5);
        assert_eq!(packets[1].sequence, 2);
        assert!(packets.iter().all(|p| !p.is_terminator && p.frames == 2));
    }

    #[test]
    fn vad_sends_terminator_after_speech() {
        let (mut p, out) = pipeline(TransmitMode::VoiceActivity);
        p.push(&vec![0.0; FRAME_SIZE * 5]);
        assert!(out.lock().unwrap().is_empty());
        p.push(&tone(FRAME_SIZE * 4));
        p.push(&vec![0.0; FRAME_SIZE * 40]);
        let packets = out.lock().unwrap();
        assert!(!packets.is_empty());
        assert!(packets.last().unwrap().is_terminator);
        assert_eq!(packets.iter().filter(|p| p.is_terminator).count(), 1);
    }

    #[test]
    fn push_to_talk_and_mute() {
        let (mut p, out) = pipeline(TransmitMode::PushToTalk);
        p.push(&tone(FRAME_SIZE * 4));
        assert!(out.lock().unwrap().is_empty());
        p.control().set_push_to_talk(true);
        p.push(&tone(FRAME_SIZE * 4));
        assert_eq!(out.lock().unwrap().len(), 2);
        p.control().set_muted(true);
        p.push(&tone(FRAME_SIZE * 4));
        let packets = out.lock().unwrap();
        assert!(packets.last().unwrap().is_terminator);
    }
}
