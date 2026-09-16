//! Linear 16-bit PCM, network byte order (RFC 3551 §4.5.11).

use super::AudioCodec;

pub struct L16 {
    payload_type: u8,
    rate: u32,
}

impl L16 {
    pub fn new(payload_type: u8, rate: u32) -> Self {
        Self { payload_type, rate }
    }
}

impl AudioCodec for L16 {
    fn name(&self) -> &'static str {
        "L16"
    }
    fn payload_type(&self) -> u8 {
        self.payload_type
    }
    fn clock_rate(&self) -> u32 {
        self.rate
    }
    fn sample_rate(&self) -> u32 {
        self.rate
    }
    fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>) {
        out.reserve(pcm.len() * 2);
        for s in pcm {
            out.extend_from_slice(&s.to_be_bytes());
        }
    }
    fn decode(&mut self, payload: &[u8], out: &mut Vec<i16>) {
        out.extend(payload.chunks_exact(2).map(|b| i16::from_be_bytes([b[0], b[1]])));
    }
}
