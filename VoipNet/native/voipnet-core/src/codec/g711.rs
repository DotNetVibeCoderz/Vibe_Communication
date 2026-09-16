//! ITU-T G.711 μ-law (PCMU) and A-law (PCMA) with precomputed lookup tables.

use std::sync::OnceLock;

use super::AudioCodec;

const BIAS: i32 = 0x84;
const CLIP: i32 = 32635;

struct Tables {
    ulaw_decode: [i16; 256],
    alaw_decode: [i16; 256],
    /// Indexed by the top 14 bits of the linear sample (sample >> 2 as u16).
    ulaw_encode: Box<[u8; 16384]>,
    alaw_encode: Box<[u8; 16384]>,
}

fn tables() -> &'static Tables {
    static TABLES: OnceLock<Tables> = OnceLock::new();
    TABLES.get_or_init(|| {
        let mut t = Tables {
            ulaw_decode: [0; 256],
            alaw_decode: [0; 256],
            ulaw_encode: Box::new([0; 16384]),
            alaw_encode: Box::new([0; 16384]),
        };
        for i in 0..256 {
            t.ulaw_decode[i] = ulaw_to_linear(i as u8);
            t.alaw_decode[i] = alaw_to_linear(i as u8);
        }
        for i in 0..16384usize {
            let sample = ((i as u16) << 2) as i16;
            t.ulaw_encode[i] = linear_to_ulaw(sample);
            t.alaw_encode[i] = linear_to_alaw(sample);
        }
        t
    })
}

pub fn linear_to_ulaw(sample: i16) -> u8 {
    let mut pcm = sample as i32;
    let sign = if pcm < 0 {
        pcm = -pcm;
        0x80
    } else {
        0
    };
    pcm = pcm.min(CLIP) + BIAS;
    let mut exponent = 7;
    let mut mask = 0x4000;
    while exponent > 0 && (pcm & mask) == 0 {
        exponent -= 1;
        mask >>= 1;
    }
    let mantissa = (pcm >> (exponent + 3)) & 0x0F;
    !(sign | (exponent << 4) | mantissa) as u8
}

pub fn ulaw_to_linear(code: u8) -> i16 {
    let u = !code as i32;
    let sign = u & 0x80;
    let exponent = (u >> 4) & 0x07;
    let mantissa = u & 0x0F;
    let magnitude = (((mantissa << 3) + BIAS) << exponent) - BIAS;
    (if sign != 0 { -magnitude } else { magnitude }) as i16
}

pub fn linear_to_alaw(sample: i16) -> u8 {
    let mut pcm = sample as i32;
    let mask = if pcm >= 0 {
        0xD5
    } else {
        pcm = -pcm - 1;
        0x55
    };
    pcm = pcm.min(32767);
    let code = if pcm < 256 {
        pcm >> 4
    } else {
        let mut seg = 1;
        let mut v = pcm >> 8;
        while v > 1 && seg < 7 {
            v >>= 1;
            seg += 1;
        }
        (seg << 4) | ((pcm >> (seg + 3)) & 0x0F)
    };
    (code ^ mask) as u8
}

pub fn alaw_to_linear(code: u8) -> i16 {
    let a = (code ^ 0x55) as i32;
    let seg = (a & 0x70) >> 4;
    let mut t = (a & 0x0F) << 4;
    t = match seg {
        0 => t + 8,
        1 => t + 0x108,
        _ => (t + 0x108) << (seg - 1),
    };
    (if a & 0x80 != 0 { t } else { -t }) as i16
}

/// Bulk μ-law encode. The loop body is branch-free table lookups so it vectorizes well.
#[inline]
pub fn encode_ulaw(pcm: &[i16], out: &mut Vec<u8>) {
    let t = &tables().ulaw_encode;
    out.extend(pcm.iter().map(|&s| t[((s as u16) >> 2) as usize]));
}

#[inline]
pub fn decode_ulaw(data: &[u8], out: &mut Vec<i16>) {
    let t = &tables().ulaw_decode;
    out.extend(data.iter().map(|&b| t[b as usize]));
}

#[inline]
pub fn encode_alaw(pcm: &[i16], out: &mut Vec<u8>) {
    let t = &tables().alaw_encode;
    out.extend(pcm.iter().map(|&s| t[((s as u16) >> 2) as usize]));
}

#[inline]
pub fn decode_alaw(data: &[u8], out: &mut Vec<i16>) {
    let t = &tables().alaw_decode;
    out.extend(data.iter().map(|&b| t[b as usize]));
}

#[derive(Default)]
pub struct Pcmu;

impl AudioCodec for Pcmu {
    fn name(&self) -> &'static str {
        "PCMU"
    }
    fn payload_type(&self) -> u8 {
        0
    }
    fn clock_rate(&self) -> u32 {
        8000
    }
    fn sample_rate(&self) -> u32 {
        8000
    }
    fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>) {
        encode_ulaw(pcm, out)
    }
    fn decode(&mut self, payload: &[u8], out: &mut Vec<i16>) {
        decode_ulaw(payload, out)
    }
}

#[derive(Default)]
pub struct Pcma;

impl AudioCodec for Pcma {
    fn name(&self) -> &'static str {
        "PCMA"
    }
    fn payload_type(&self) -> u8 {
        8
    }
    fn clock_rate(&self) -> u32 {
        8000
    }
    fn sample_rate(&self) -> u32 {
        8000
    }
    fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>) {
        encode_alaw(pcm, out)
    }
    fn decode(&mut self, payload: &[u8], out: &mut Vec<i16>) {
        decode_alaw(payload, out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ulaw_known_values() {
        assert_eq!(linear_to_ulaw(0), 0xFF);
        assert_eq!(linear_to_ulaw(-1), 0x7F);
        assert_eq!(ulaw_to_linear(0xFF), 0);
        assert_eq!(ulaw_to_linear(0x00), -32124);
        assert_eq!(ulaw_to_linear(0x80), 32124);
    }

    #[test]
    fn alaw_known_values() {
        assert_eq!(linear_to_alaw(0), 0xD5);
        assert_eq!(alaw_to_linear(0xD5), 8);
        assert_eq!(alaw_to_linear(0x55), -8);
        assert_eq!(alaw_to_linear(0xAA), 32256);
    }

    #[test]
    fn roundtrip_error_is_bounded() {
        for s in (-32000i32..32000).step_by(97) {
            let s = s as i16;
            let u = ulaw_to_linear(linear_to_ulaw(s)) as i32;
            let a = alaw_to_linear(linear_to_alaw(s)) as i32;
            let tol = (s as i32).abs() / 10 + 40;
            assert!((u - s as i32).abs() <= tol, "ulaw {s} -> {u}");
            assert!((a - s as i32).abs() <= tol, "alaw {s} -> {a}");
        }
    }

    #[test]
    fn table_matches_reference_encoder() {
        let pcm: Vec<i16> = (-32768i32..32768).step_by(4).map(|s| s as i16).collect();
        let mut out = Vec::new();
        encode_ulaw(&pcm, &mut out);
        for (s, c) in pcm.iter().zip(&out) {
            assert_eq!(*c, linear_to_ulaw(*s));
        }
    }
}
