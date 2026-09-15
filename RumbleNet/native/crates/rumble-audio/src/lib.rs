//! # rumble-audio
//!
//! Real-time audio pipeline for Rumble.Net:
//!
//! * [`codec`]: safe Opus encoder/decoder wrappers (pure Rust libopus port), PCM fallback.
//! * [`jitter`]: adaptive jitter buffer with packet-loss concealment signalling.
//! * [`mixer`]: lock-free (SPSC ring buffer) multi-speaker mixer with positional audio.
//! * [`capture`]: capture pipeline (DSP chain, voice activity / push-to-talk, Opus packetizer).
//! * [`positional`]: 3D spatialization (equal-power panning + distance attenuation).
//! * [`dsp`]: small building blocks (gain, noise gate, DC filter, VAD, soft clipping).
//! * [`resample`]: streaming resamplers used to bridge device sample rates and 48 kHz.
//! * [`device`]: cross-platform device I/O via `cpal` (WASAPI, ALSA/PulseAudio, CoreAudio).
//!
//! All hot paths are allocation-free after warm-up.

pub mod capture;
pub mod codec;
pub mod device;
pub mod dsp;
pub mod jitter;
pub mod mixer;
pub mod positional;
pub mod resample;

/// Mumble always uses 48 kHz audio.
pub const SAMPLE_RATE: u32 = 48_000;
/// One Mumble audio frame is 10 ms.
pub const FRAME_SIZE: usize = 480;
/// Largest Opus packet duration (120 ms) in samples.
pub const MAX_FRAME_SAMPLES: usize = 5760;
/// Largest encoded Opus packet we produce/accept.
pub const MAX_OPUS_PACKET: usize = 1275 * 3 + 7;

/// Errors produced by the audio subsystem.
#[derive(Debug, thiserror::Error)]
pub enum AudioError {
    #[error("opus error {code}: {message}")]
    Opus { code: i32, message: &'static str },
    #[error("audio device error: {0}")]
    Device(String),
    #[error("no audio device available: {0}")]
    NoDevice(&'static str),
    #[error("unsupported: {0}")]
    Unsupported(&'static str),
    #[error("invalid argument: {0}")]
    InvalidArgument(&'static str),
}

pub type Result<T> = std::result::Result<T, AudioError>;
