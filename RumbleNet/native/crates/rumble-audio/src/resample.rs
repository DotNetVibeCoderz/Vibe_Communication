//! Streaming resamplers bridging arbitrary device rates and Mumble's 48 kHz.
//!
//! Uses 4-point cubic Hermite interpolation: cheap, allocation-free and considerably cleaner
//! than linear interpolation for voice. When rates match, samples pass straight through.

/// Push-style resampler for interleaved audio (capture path: device rate → 48 kHz).
#[derive(Debug, Clone)]
pub struct Resampler {
    channels: usize,
    step: f64,
    phase: f64,
    history: Vec<f32>,
    passthrough: bool,
}

#[inline(always)]
fn hermite(y0: f32, y1: f32, y2: f32, y3: f32, t: f32) -> f32 {
    let c0 = y1;
    let c1 = 0.5 * (y2 - y0);
    let c2 = y0 - 2.5 * y1 + 2.0 * y2 - 0.5 * y3;
    let c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
    ((c3 * t + c2) * t + c1) * t + c0
}

impl Resampler {
    pub fn new(input_rate: u32, output_rate: u32, channels: usize) -> Self {
        Self {
            channels,
            step: input_rate as f64 / output_rate as f64,
            phase: 0.0,
            // Four frames of history per channel.
            history: vec![0.0; 4 * channels],
            passthrough: input_rate == output_rate,
        }
    }

    pub fn is_passthrough(&self) -> bool {
        self.passthrough
    }

    /// Resamples `input` (interleaved) and appends to `out`.
    pub fn process(&mut self, input: &[f32], out: &mut Vec<f32>) {
        if self.passthrough {
            out.extend_from_slice(input);
            return;
        }
        let ch = self.channels;
        for frame in input.chunks_exact(ch) {
            // Shift history and push the new frame.
            self.history.copy_within(ch.., 0);
            self.history[3 * ch..].copy_from_slice(frame);
            // Emit all output samples that fall between history[1] and history[2].
            while self.phase < 1.0 {
                let t = self.phase as f32;
                for c in 0..ch {
                    let h = &self.history;
                    out.push(hermite(h[c], h[ch + c], h[2 * ch + c], h[3 * ch + c], t));
                }
                self.phase += self.step;
            }
            self.phase -= 1.0;
        }
    }
}

/// Pull-style resampler (playback path: 48 kHz source → device rate).
///
/// The source callback fills fixed-size blocks at the source rate.
#[derive(Debug, Clone)]
pub struct PullResampler {
    channels: usize,
    step: f64,
    phase: f64,
    block: Vec<f32>,
    block_pos: usize,
    history: Vec<f32>,
    passthrough: bool,
}

impl PullResampler {
    /// `block_frames` is the number of frames the source produces per call.
    pub fn new(source_rate: u32, output_rate: u32, channels: usize, block_frames: usize) -> Self {
        Self {
            channels,
            step: source_rate as f64 / output_rate as f64,
            phase: 0.0,
            block: vec![0.0; block_frames * channels],
            block_pos: block_frames * channels,
            history: vec![0.0; 4 * channels],
            passthrough: source_rate == output_rate,
        }
    }

    #[inline]
    fn next_source_frame<F: FnMut(&mut [f32])>(&mut self, source: &mut F) {
        if self.block_pos >= self.block.len() {
            source(&mut self.block);
            self.block_pos = 0;
        }
        let ch = self.channels;
        self.history.copy_within(ch.., 0);
        self.history[3 * ch..].copy_from_slice(&self.block[self.block_pos..self.block_pos + ch]);
        self.block_pos += ch;
    }

    /// Fills `out` (interleaved, output rate) pulling from `source` as needed.
    pub fn fill<F: FnMut(&mut [f32])>(&mut self, out: &mut [f32], mut source: F) {
        let ch = self.channels;
        if self.passthrough {
            let mut written = 0;
            while written < out.len() {
                if self.block_pos >= self.block.len() {
                    source(&mut self.block);
                    self.block_pos = 0;
                }
                let n = (self.block.len() - self.block_pos).min(out.len() - written);
                out[written..written + n].copy_from_slice(&self.block[self.block_pos..self.block_pos + n]);
                written += n;
                self.block_pos += n;
            }
            return;
        }
        for frame in out.chunks_exact_mut(ch) {
            while self.phase >= 1.0 {
                self.next_source_frame(&mut source);
                self.phase -= 1.0;
            }
            let t = self.phase as f32;
            let h = &self.history;
            for (c, o) in frame.iter_mut().enumerate() {
                *o = hermite(h[c], h[ch + c], h[2 * ch + c], h[3 * ch + c], t);
            }
            self.phase += self.step;
        }
    }
}

/// Downmixes interleaved audio to mono.
#[inline]
pub fn downmix_to_mono(input: &[f32], channels: usize, out: &mut Vec<f32>) {
    if channels == 1 {
        out.extend_from_slice(input);
        return;
    }
    let scale = 1.0 / channels as f32;
    out.extend(input.chunks_exact(channels).map(|f| f.iter().sum::<f32>() * scale));
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn push_ratio_is_correct() {
        let mut r = Resampler::new(44_100, 48_000, 1);
        let input = vec![0.25f32; 44_100];
        let mut out = Vec::new();
        r.process(&input, &mut out);
        assert!((out.len() as i64 - 48_000).abs() <= 2, "len {}", out.len());
        assert!((out[out.len() / 2] - 0.25).abs() < 1e-4);
    }

    #[test]
    fn pull_ratio_is_correct() {
        let mut r = PullResampler::new(48_000, 44_100, 2, 480);
        let mut pulled = 0usize;
        let mut out = vec![0f32; 44_100 * 2];
        r.fill(&mut out, |block| {
            pulled += block.len() / 2;
            block.fill(0.5);
        });
        assert!((pulled as i64 - 48_000).abs() <= 480, "pulled {pulled}");
        assert!((out[40_000] - 0.5).abs() < 1e-4);
    }

    #[test]
    fn passthrough() {
        let mut r = PullResampler::new(48_000, 48_000, 1, 480);
        let mut out = vec![0f32; 1000];
        let mut counter = 0.0;
        r.fill(&mut out, |b| {
            for s in b.iter_mut() {
                *s = counter;
                counter += 1.0;
            }
        });
        assert_eq!(out[999], 999.0);
    }
}
