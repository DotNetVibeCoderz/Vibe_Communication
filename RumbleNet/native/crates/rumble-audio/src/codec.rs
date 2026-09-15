//! Codec wrappers.
//!
//! * **Opus** (default): encoder + decoder backed by `unsafe-libopus`, a pure Rust translation of
//!   libopus, so no C toolchain or system library is required on any platform.
//! * **PCM**: raw little-endian `i16` passthrough, used for local loopback, recording pipelines
//!   and testing without a codec.
//! * **CELT / Speex**: legacy codecs are recognised on the wire but cannot be decoded (modern
//!   Mumble servers negotiate Opus only). Their frames are concealed as silence.

use unsafe_libopus as opus;
// The ctl macros recurse without a `$crate::` path, so they must be in scope by name.
use unsafe_libopus::{opus_decoder_ctl, opus_encoder_ctl};

use crate::{AudioError, FRAME_SIZE, MAX_FRAME_SAMPLES, Result, SAMPLE_RATE};

fn check(code: i32) -> Result<i32> {
    if code < 0 {
        let message = match code {
            opus::OPUS_BAD_ARG => "bad argument",
            opus::OPUS_BUFFER_TOO_SMALL => "buffer too small",
            opus::OPUS_INTERNAL_ERROR => "internal error",
            opus::OPUS_INVALID_PACKET => "invalid packet",
            opus::OPUS_UNIMPLEMENTED => "unimplemented",
            opus::OPUS_INVALID_STATE => "invalid state",
            opus::OPUS_ALLOC_FAIL => "allocation failure",
            _ => "unknown error",
        };
        Err(AudioError::Opus { code, message })
    } else {
        Ok(code)
    }
}

/// Opus application profile.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum OpusApplication {
    /// Best for speech (default).
    #[default]
    Voip,
    /// Best for music / mixed content.
    Audio,
    /// Lowest algorithmic delay.
    LowDelay,
}

impl OpusApplication {
    fn raw(self) -> i32 {
        match self {
            OpusApplication::Voip => opus::OPUS_APPLICATION_VOIP,
            OpusApplication::Audio => opus::OPUS_APPLICATION_AUDIO,
            OpusApplication::LowDelay => opus::OPUS_APPLICATION_RESTRICTED_LOWDELAY,
        }
    }
}

/// Safe Opus encoder.
pub struct OpusEncoder {
    st: *mut opus::OpusEncoder,
    channels: usize,
}

// SAFETY: the encoder state is exclusively owned and never shared between threads concurrently.
unsafe impl Send for OpusEncoder {}

impl OpusEncoder {
    pub fn new(channels: u16, application: OpusApplication) -> Result<Self> {
        if channels != 1 && channels != 2 {
            return Err(AudioError::InvalidArgument("channels must be 1 or 2"));
        }
        let mut err = 0;
        // SAFETY: arguments are validated; the returned pointer is checked for null.
        let st = unsafe { opus::opus_encoder_create(SAMPLE_RATE as i32, channels as i32, application.raw(), &mut err) };
        check(err)?;
        if st.is_null() {
            return Err(AudioError::Opus { code: opus::OPUS_ALLOC_FAIL, message: "allocation failure" });
        }
        let mut enc = Self { st, channels: channels as usize };
        enc.set_signal_voice(application == OpusApplication::Voip)?;
        Ok(enc)
    }

    pub fn channels(&self) -> usize {
        self.channels
    }

    /// Target bitrate in bits per second (Mumble default: 40 000 – 72 000).
    pub fn set_bitrate(&mut self, bits_per_second: i32) -> Result<()> {
        // SAFETY: valid encoder pointer; request/argument types match the ctl definition.
        check(unsafe { opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_BITRATE_REQUEST, bits_per_second) }).map(drop)
    }

    pub fn set_vbr(&mut self, enabled: bool) -> Result<()> {
        // SAFETY: see `set_bitrate`.
        check(unsafe { opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_VBR_REQUEST, enabled as i32) }).map(drop)
    }

    /// Complexity 0..=10.
    pub fn set_complexity(&mut self, complexity: i32) -> Result<()> {
        // SAFETY: see `set_bitrate`.
        check(unsafe { opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_COMPLEXITY_REQUEST, complexity.clamp(0, 10)) })
            .map(drop)
    }

    /// Enables in-band forward error correction (helps on lossy networks).
    pub fn set_inband_fec(&mut self, enabled: bool) -> Result<()> {
        // SAFETY: see `set_bitrate`.
        check(unsafe { opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_INBAND_FEC_REQUEST, enabled as i32) }).map(drop)
    }

    /// Expected packet loss percentage (0..=100), tunes FEC.
    pub fn set_packet_loss_percent(&mut self, percent: i32) -> Result<()> {
        // SAFETY: see `set_bitrate`.
        check(unsafe {
            opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_PACKET_LOSS_PERC_REQUEST, percent.clamp(0, 100))
        })
        .map(drop)
    }

    /// Discontinuous transmission.
    pub fn set_dtx(&mut self, enabled: bool) -> Result<()> {
        // SAFETY: see `set_bitrate`.
        check(unsafe { opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_DTX_REQUEST, enabled as i32) }).map(drop)
    }

    fn set_signal_voice(&mut self, voice: bool) -> Result<()> {
        let signal = if voice { opus::OPUS_SIGNAL_VOICE } else { opus::OPUS_AUTO };
        // SAFETY: see `set_bitrate`.
        check(unsafe { opus::opus_encoder_ctl!(self.st, opus::OPUS_SET_SIGNAL_REQUEST, signal) }).map(drop)
    }

    /// Encodes interleaved float PCM. `pcm.len() / channels` must be a valid Opus frame size
    /// (2.5, 5, 10, 20, 40 or 60 ms at 48 kHz). Returns the packet length written to `out`.
    pub fn encode_float(&mut self, pcm: &[f32], out: &mut [u8]) -> Result<usize> {
        let frame = pcm.len() / self.channels;
        // SAFETY: buffers are valid for the given lengths; frame size validated by libopus.
        let n = unsafe { opus::opus_encode_float(self.st, pcm.as_ptr(), frame as i32, out.as_mut_ptr(), out.len() as i32) };
        check(n).map(|n| n as usize)
    }

    /// Encodes interleaved `i16` PCM.
    pub fn encode_i16(&mut self, pcm: &[i16], out: &mut [u8]) -> Result<usize> {
        let frame = pcm.len() / self.channels;
        // SAFETY: see `encode_float`.
        let n = unsafe { opus::opus_encode(self.st, pcm.as_ptr(), frame as i32, out.as_mut_ptr(), out.len() as i32) };
        check(n).map(|n| n as usize)
    }

    /// Resets the encoder state (e.g. between transmissions).
    pub fn reset(&mut self) {
        // SAFETY: valid encoder pointer; OPUS_RESET_STATE takes no argument.
        unsafe {
            opus::opus_encoder_ctl!(self.st, opus::OPUS_RESET_STATE);
        }
    }
}

impl Drop for OpusEncoder {
    fn drop(&mut self) {
        // SAFETY: pointer was created by opus_encoder_create and is destroyed exactly once.
        unsafe { opus::opus_encoder_destroy(self.st) }
    }
}

/// Safe Opus decoder.
pub struct OpusDecoder {
    st: *mut opus::OpusDecoder,
    channels: usize,
}

// SAFETY: exclusively owned decoder state.
unsafe impl Send for OpusDecoder {}

impl OpusDecoder {
    pub fn new(channels: u16) -> Result<Self> {
        if channels != 1 && channels != 2 {
            return Err(AudioError::InvalidArgument("channels must be 1 or 2"));
        }
        let mut err = 0;
        // SAFETY: arguments validated; pointer checked.
        let st = unsafe { opus::opus_decoder_create(SAMPLE_RATE as i32, channels as i32, &mut err) };
        check(err)?;
        if st.is_null() {
            return Err(AudioError::Opus { code: opus::OPUS_ALLOC_FAIL, message: "allocation failure" });
        }
        Ok(Self { st, channels: channels as usize })
    }

    pub fn channels(&self) -> usize {
        self.channels
    }

    /// Decodes a packet into interleaved float PCM, returning samples per channel.
    /// `out` should hold [`MAX_FRAME_SAMPLES`] × channels samples.
    pub fn decode_float(&mut self, packet: &[u8], out: &mut [f32]) -> Result<usize> {
        let frame = out.len() / self.channels;
        // SAFETY: buffers valid for the given lengths.
        let n = unsafe {
            opus::opus_decode_float(self.st, packet.as_ptr(), packet.len() as i32, out.as_mut_ptr(), frame as i32, 0)
        };
        check(n).map(|n| n as usize)
    }

    /// Packet loss concealment: synthesizes `out.len() / channels` samples.
    pub fn conceal(&mut self, out: &mut [f32]) -> Result<usize> {
        let frame = out.len() / self.channels;
        // SAFETY: a null packet requests PLC; output buffer valid.
        let n = unsafe { opus::opus_decode_float(self.st, std::ptr::null(), 0, out.as_mut_ptr(), frame as i32, 0) };
        check(n).map(|n| n as usize)
    }

    /// Number of samples (per channel) contained in a packet.
    pub fn packet_samples(packet: &[u8]) -> Result<usize> {
        if packet.is_empty() {
            return Ok(0);
        }
        // SAFETY: packet slice is valid.
        let n = unsafe { opus::opus_packet_get_nb_samples(packet.as_ptr(), packet.len() as i32, SAMPLE_RATE as i32) };
        check(n).map(|n| n as usize)
    }

    pub fn reset(&mut self) {
        // SAFETY: valid decoder pointer.
        unsafe {
            opus::opus_decoder_ctl!(self.st, opus::OPUS_RESET_STATE);
        }
    }
}

impl Drop for OpusDecoder {
    fn drop(&mut self) {
        // SAFETY: created by opus_decoder_create, destroyed once.
        unsafe { opus::opus_decoder_destroy(self.st) }
    }
}

/// Codec used for a received stream.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum StreamCodec {
    #[default]
    Opus,
    Pcm,
    /// CELT alpha/beta or Speex: recognised but concealed as silence.
    Legacy,
}

/// Mono stream decoder dispatching on codec.
pub enum StreamDecoder {
    Opus(OpusDecoder),
    Pcm,
    Legacy,
}

impl StreamDecoder {
    pub fn new(codec: StreamCodec) -> Result<Self> {
        Ok(match codec {
            StreamCodec::Opus => StreamDecoder::Opus(OpusDecoder::new(1)?),
            StreamCodec::Pcm => StreamDecoder::Pcm,
            StreamCodec::Legacy => StreamDecoder::Legacy,
        })
    }

    pub fn codec(&self) -> StreamCodec {
        match self {
            StreamDecoder::Opus(_) => StreamCodec::Opus,
            StreamDecoder::Pcm => StreamCodec::Pcm,
            StreamDecoder::Legacy => StreamCodec::Legacy,
        }
    }

    /// Decodes into `out` (mono), returning sample count.
    pub fn decode(&mut self, payload: &[u8], out: &mut [f32]) -> usize {
        match self {
            StreamDecoder::Opus(d) => d.decode_float(payload, out).unwrap_or(0),
            StreamDecoder::Pcm => {
                let n = (payload.len() / 2).min(out.len());
                for (i, o) in out[..n].iter_mut().enumerate() {
                    *o = i16::from_le_bytes([payload[2 * i], payload[2 * i + 1]]) as f32 / 32768.0;
                }
                n
            }
            StreamDecoder::Legacy => {
                let n = FRAME_SIZE.min(out.len());
                out[..n].fill(0.0);
                n
            }
        }
    }

    /// Conceals one lost 10 ms frame.
    pub fn conceal(&mut self, out: &mut [f32]) -> usize {
        let n = FRAME_SIZE.min(out.len());
        match self {
            StreamDecoder::Opus(d) => d.conceal(&mut out[..n]).unwrap_or_else(|_| {
                out[..n].fill(0.0);
                n
            }),
            _ => {
                out[..n].fill(0.0);
                n
            }
        }
    }

    /// Number of 10 ms frames represented by a payload (used to advance sequence numbers).
    pub fn frames_in(&self, payload: &[u8]) -> u64 {
        let samples = match self {
            StreamDecoder::Opus(_) => OpusDecoder::packet_samples(payload).unwrap_or(FRAME_SIZE),
            StreamDecoder::Pcm => payload.len() / 2,
            StreamDecoder::Legacy => FRAME_SIZE,
        };
        (samples.min(MAX_FRAME_SAMPLES) / FRAME_SIZE).max(1) as u64
    }
}

/// Encodes mono `i16` PCM to the PCM wire payload.
pub fn encode_pcm(samples: &[f32], out: &mut Vec<u8>) {
    out.clear();
    out.reserve(samples.len() * 2);
    for s in samples {
        let v = (s.clamp(-1.0, 1.0) * 32767.0) as i16;
        out.extend_from_slice(&v.to_le_bytes());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sine(freq: f32, len: usize) -> Vec<f32> {
        (0..len).map(|i| (i as f32 * freq * std::f32::consts::TAU / SAMPLE_RATE as f32).sin() * 0.5).collect()
    }

    #[test]
    fn opus_roundtrip_preserves_energy() {
        let mut enc = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
        enc.set_bitrate(48_000).unwrap();
        let mut dec = OpusDecoder::new(1).unwrap();
        let input = sine(440.0, FRAME_SIZE * 50);
        let mut packet = [0u8; crate::MAX_OPUS_PACKET];
        let mut out = vec![0f32; MAX_FRAME_SAMPLES];
        let mut in_energy = 0.0;
        let mut out_energy = 0.0;
        for (i, frame) in input.chunks(FRAME_SIZE).enumerate() {
            let n = enc.encode_float(frame, &mut packet).unwrap();
            assert!(n > 0);
            assert_eq!(OpusDecoder::packet_samples(&packet[..n]).unwrap(), FRAME_SIZE);
            let m = dec.decode_float(&packet[..n], &mut out).unwrap();
            assert_eq!(m, FRAME_SIZE);
            if i > 10 {
                in_energy += frame.iter().map(|s| s * s).sum::<f32>();
                out_energy += out[..m].iter().map(|s| s * s).sum::<f32>();
            }
        }
        let ratio = out_energy / in_energy;
        assert!((0.5..1.5).contains(&ratio), "energy ratio {ratio}");
        assert_eq!(dec.conceal(&mut out[..FRAME_SIZE]).unwrap(), FRAME_SIZE);
    }

    #[test]
    fn multi_frame_packets() {
        let mut enc = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
        let input = sine(300.0, FRAME_SIZE * 4);
        let mut packet = [0u8; crate::MAX_OPUS_PACKET];
        let n = enc.encode_float(&input[..FRAME_SIZE * 2], &mut packet).unwrap();
        let dec = StreamDecoder::new(StreamCodec::Opus).unwrap();
        assert_eq!(dec.frames_in(&packet[..n]), 2);
    }

    #[test]
    fn pcm_roundtrip() {
        let input = sine(1000.0, 100);
        let mut wire = Vec::new();
        encode_pcm(&input, &mut wire);
        let mut dec = StreamDecoder::new(StreamCodec::Pcm).unwrap();
        let mut out = vec![0f32; 100];
        assert_eq!(dec.decode(&wire, &mut out), 100);
        for (a, b) in input.iter().zip(&out) {
            assert!((a - b).abs() < 1e-3);
        }
    }
}
