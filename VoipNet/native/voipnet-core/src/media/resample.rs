//! Streaming linear-interpolation resampler for 16-bit mono PCM.
//!
//! Adequate for telephony rate conversion (8 kHz ↔ 16 kHz ↔ 24/48 kHz) with low CPU cost;
//! a short 3-tap smoothing is applied when downsampling to limit aliasing.

pub struct Resampler {
    from: u32,
    to: u32,
    /// Fractional read position carried between calls, in units of 1/to.
    pos: u64,
    last: i16,
}

impl Resampler {
    pub fn new(from: u32, to: u32) -> Self {
        Self { from: from.max(1), to: to.max(1), pos: 0, last: 0 }
    }

    pub fn rates(&self) -> (u32, u32) {
        (self.from, self.to)
    }

    pub fn process(&mut self, input: &[i16], out: &mut Vec<i16>) {
        if self.from == self.to {
            out.extend_from_slice(input);
            return;
        }
        if input.is_empty() {
            return;
        }
        let (from, to) = (self.from as u64, self.to as u64);
        let downsampling = from > to;
        let sample = |i: isize, last: i16| -> i32 {
            if i < 0 {
                last as i32
            } else {
                input[(i as usize).min(input.len() - 1)] as i32
            }
        };
        out.reserve((input.len() as u64 * to / from) as usize + 1);
        // pos is measured in input samples × `to`; each output step advances by `from`.
        // Index -1 refers to the last sample of the previous block.
        let total = input.len() as u64 * to;
        while self.pos < total {
            let idx = (self.pos / to) as isize;
            let frac = (self.pos % to) as i32;
            let (a, b) = (sample(idx - 1, self.last), sample(idx, self.last));
            let mut v = a + ((b - a) * frac) / to as i32;
            if downsampling {
                let c = sample(idx + 1, self.last);
                v = (a + 2 * v + c) / 4;
            }
            out.push(v.clamp(-32768, 32767) as i16);
            self.pos += from;
        }
        self.pos -= total;
        self.last = *input.last().expect("non-empty");
    }
}

/// Converts between i16 PCM and little-endian byte buffers.
pub fn pcm_to_bytes(pcm: &[i16], out: &mut Vec<u8>) {
    out.reserve(pcm.len() * 2);
    for s in pcm {
        out.extend_from_slice(&s.to_le_bytes());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn output_length_matches_ratio_over_stream() {
        for (from, to) in [(8000u32, 16000u32), (16000, 8000), (48000, 16000), (16000, 24000)] {
            let mut r = Resampler::new(from, to);
            let mut out = Vec::new();
            let frame = (from / 50) as usize;
            for _ in 0..50 {
                r.process(&vec![1000; frame], &mut out);
            }
            let expected = to as usize;
            assert!((out.len() as i64 - expected as i64).abs() <= 1, "{from}->{to}: {}", out.len());
            assert!(out[out.len() / 2] == 1000);
        }
    }

    #[test]
    fn preserves_low_frequency_tone() {
        let input: Vec<i16> = (0..8000).map(|i| (10000.0 * (i as f64 * 2.0 * std::f64::consts::PI * 300.0 / 8000.0).sin()) as i16).collect();
        let mut r = Resampler::new(8000, 16000);
        let mut out = Vec::new();
        r.process(&input, &mut out);
        let rms = |v: &[i16]| (v.iter().map(|&s| (s as f64).powi(2)).sum::<f64>() / v.len() as f64).sqrt();
        assert!((rms(&out) / rms(&input) - 1.0).abs() < 0.05);
    }
}
