//! Audio codecs, codec registry and packet-loss concealment.

pub mod dtmf;
pub mod g711;
pub mod g722;
pub mod l16;
#[cfg(feature = "opus")]
pub mod opus;

use crate::sdp::RtpMap;

/// A stateful audio codec. Implementations must not allocate per call beyond growing `out`.
pub trait AudioCodec: Send {
    fn name(&self) -> &'static str;
    /// Static or preferred dynamic payload type.
    fn payload_type(&self) -> u8;
    /// RTP timestamp clock rate.
    fn clock_rate(&self) -> u32;
    /// PCM sample rate of encoder input / decoder output.
    fn sample_rate(&self) -> u32;
    fn channels(&self) -> u8 {
        1
    }
    fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>);
    fn decode(&mut self, payload: &[u8], out: &mut Vec<i16>);
    /// Conceals one lost frame of `samples` samples, optionally using the packet that follows it
    /// (codecs with in-band FEC). Returns false when the codec has no concealment of its own.
    fn conceal(&mut self, _samples: usize, _next_payload: Option<&[u8]>, _out: &mut Vec<i16>) -> bool {
        false
    }
    /// Reports what the peer sees (RTCP), so adaptive codecs can change bitrate or redundancy.
    fn set_network_quality(&mut self, _loss_percent: f64, _round_trip_ms: f64) {}
    /// Enables discontinuous transmission, for codecs that support it.
    fn set_dtx(&mut self, _enabled: bool) {}
}

/// Every media format the engine can put into SDP. Codecs without a native
/// implementation are negotiated as pass-through: the application supplies/receives
/// encoded payloads (e.g. hardware H.264/VP8 encoders, licensed G.729 stacks).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CodecKind {
    Pcmu,
    Pcma,
    G722,
    L16,
    G729,
    Opus,
    Silk,
    Speex,
    H264,
    Vp8,
    Vp9,
    TelephoneEvent,
}

impl CodecKind {
    pub const ALL: [CodecKind; 12] = [
        Self::Pcmu,
        Self::Pcma,
        Self::G722,
        Self::L16,
        Self::G729,
        Self::Opus,
        Self::Silk,
        Self::Speex,
        Self::H264,
        Self::Vp8,
        Self::Vp9,
        Self::TelephoneEvent,
    ];

    pub fn parse(name: &str) -> Option<Self> {
        Self::ALL.into_iter().find(|k| k.rtpmap().encoding.eq_ignore_ascii_case(name))
    }

    pub fn is_video(self) -> bool {
        matches!(self, Self::H264 | Self::Vp8 | Self::Vp9)
    }

    /// True when the engine encodes/decodes this format itself.
    pub fn is_native(self) -> bool {
        matches!(self, Self::Pcmu | Self::Pcma | Self::G722 | Self::L16 | Self::TelephoneEvent) || (self == Self::Opus && cfg!(feature = "opus"))
    }

    pub fn rtpmap(self) -> RtpMap {
        let (pt, enc, rate, ch, fmtp): (u8, &str, u32, u8, Option<&str>) = match self {
            Self::Pcmu => (0, "PCMU", 8000, 1, None),
            Self::Pcma => (8, "PCMA", 8000, 1, None),
            Self::G722 => (9, "G722", 8000, 1, None),
            Self::G729 => (18, "G729", 8000, 1, Some("annexb=no")),
            Self::L16 => (97, "L16", 16000, 1, None),
            Self::Opus => (111, "opus", 48000, 2, Some("minptime=10;useinbandfec=1")),
            Self::Silk => (112, "SILK", 16000, 1, None),
            Self::Speex => (113, "speex", 16000, 1, None),
            Self::TelephoneEvent => (101, "telephone-event", 8000, 1, Some("0-16")),
            Self::H264 => (96, "H264", 90000, 1, Some("profile-level-id=42e01f;packetization-mode=1")),
            Self::Vp8 => (98, "VP8", 90000, 1, None),
            Self::Vp9 => (100, "VP9", 90000, 1, None),
        };
        RtpMap { payload_type: pt, encoding: enc.to_owned(), clock_rate: rate, channels: ch, fmtp: fmtp.map(str::to_owned) }
    }
}

/// Creates a native codec instance for a negotiated rtpmap. Returns `None` for pass-through formats.
pub fn create_audio_codec(map: &RtpMap) -> Option<Box<dyn AudioCodec>> {
    let codec: Box<dyn AudioCodec> = match CodecKind::parse(&map.encoding)? {
        CodecKind::Pcmu => Box::new(g711::Pcmu),
        CodecKind::Pcma => Box::new(g711::Pcma),
        CodecKind::G722 => Box::new(g722::G722::default()),
        CodecKind::L16 => Box::new(l16::L16::new(map.payload_type, map.clock_rate)),
        #[cfg(feature = "opus")]
        CodecKind::Opus => Box::new(opus::Opus::new(map.payload_type)?),
        _ => return None,
    };
    Some(codec)
}

/// Simple waveform-substitution packet loss concealment: repeats the last frame
/// with exponential attenuation, then fades to silence.
pub struct Concealer {
    last: Vec<i16>,
    consecutive: u32,
}

impl Default for Concealer {
    fn default() -> Self {
        Self { last: Vec::with_capacity(960), consecutive: 0 }
    }
}

impl Concealer {
    pub fn remember(&mut self, frame: &[i16]) {
        self.last.clear();
        self.last.extend_from_slice(frame);
        self.consecutive = 0;
    }

    pub fn conceal(&mut self, samples: usize, out: &mut Vec<i16>) {
        self.consecutive += 1;
        // -6 dB per lost frame, silence after 5 consecutive losses.
        let shift = self.consecutive.min(16);
        if self.last.is_empty() || self.consecutive > 5 {
            out.extend(std::iter::repeat(0).take(samples));
            return;
        }
        let start = out.len();
        out.extend((0..samples).map(|i| self.last[i % self.last.len()] >> shift));
        // Short linear fade-in to avoid a click at the splice point.
        let fade = (samples / 8).max(1);
        for (i, s) in out[start..start + fade.min(samples)].iter_mut().enumerate() {
            *s = ((*s as i32 * i as i32) / fade as i32) as i16;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn registry_creates_native_codecs() {
        for kind in [CodecKind::Pcmu, CodecKind::Pcma, CodecKind::G722, CodecKind::L16] {
            let c = create_audio_codec(&kind.rtpmap()).expect("native codec");
            assert!(kind.is_native());
            assert!(c.sample_rate() >= 8000);
        }
        assert!(create_audio_codec(&CodecKind::G729.rtpmap()).is_none());
        assert_eq!(CodecKind::parse("opus"), Some(CodecKind::Opus));
    }

    #[test]
    fn concealment_fades_out() {
        let mut plc = Concealer::default();
        plc.remember(&[1000; 160]);
        let mut out = Vec::new();
        for _ in 0..7 {
            plc.conceal(160, &mut out);
        }
        assert_eq!(out.len(), 7 * 160);
        assert!(out[160 * 6..].iter().all(|&s| s == 0));
        assert!(out[80] > 0 && out[80] < 1000);
    }
}
