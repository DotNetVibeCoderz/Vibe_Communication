//! DSP building blocks for the capture and playback chains.

/// A frame processor operating in place on mono 48 kHz float samples.
pub trait AudioProcessor: Send {
    fn process(&mut self, frame: &mut [f32]);
}

impl<F: FnMut(&mut [f32]) + Send> AudioProcessor for F {
    fn process(&mut self, frame: &mut [f32]) {
        self(frame)
    }
}

/// Root-mean-square level of a frame.
#[inline]
pub fn rms(frame: &[f32]) -> f32 {
    if frame.is_empty() {
        return 0.0;
    }
    (frame.iter().map(|s| s * s).sum::<f32>() / frame.len() as f32).sqrt()
}

/// Level in dBFS (−96 for silence).
#[inline]
pub fn to_dbfs(level: f32) -> f32 {
    if level <= 1e-5 { -96.0 } else { 20.0 * level.log10() }
}

/// Peak absolute sample value.
#[inline]
pub fn peak(frame: &[f32]) -> f32 {
    frame.iter().fold(0.0f32, |m, s| m.max(s.abs()))
}

/// Linear gain.
#[derive(Debug, Clone, Copy)]
pub struct Gain(pub f32);

impl AudioProcessor for Gain {
    fn process(&mut self, frame: &mut [f32]) {
        if (self.0 - 1.0).abs() > f32::EPSILON {
            for s in frame {
                *s *= self.0;
            }
        }
    }
}

/// First-order DC-blocking high-pass filter (removes microphone offset / rumble).
#[derive(Debug, Clone, Copy)]
pub struct DcBlocker {
    r: f32,
    x1: f32,
    y1: f32,
}

impl Default for DcBlocker {
    fn default() -> Self {
        Self { r: 0.995, x1: 0.0, y1: 0.0 }
    }
}

impl AudioProcessor for DcBlocker {
    fn process(&mut self, frame: &mut [f32]) {
        for s in frame {
            let y = *s - self.x1 + self.r * self.y1;
            self.x1 = *s;
            self.y1 = y;
            *s = y;
        }
    }
}

/// Noise gate with attack/release smoothing.
#[derive(Debug, Clone, Copy)]
pub struct NoiseGate {
    pub threshold_db: f32,
    gain: f32,
    attack: f32,
    release: f32,
}

impl NoiseGate {
    pub fn new(threshold_db: f32) -> Self {
        Self { threshold_db, gain: 0.0, attack: 0.5, release: 0.05 }
    }
}

impl AudioProcessor for NoiseGate {
    fn process(&mut self, frame: &mut [f32]) {
        let open = to_dbfs(rms(frame)) >= self.threshold_db;
        let target = if open { 1.0 } else { 0.0 };
        let coeff = if open { self.attack } else { self.release };
        let start = self.gain;
        let end = start + (target - start) * coeff;
        let n = frame.len().max(1) as f32;
        for (i, s) in frame.iter_mut().enumerate() {
            *s *= start + (end - start) * (i as f32 / n);
        }
        self.gain = end;
    }
}

/// Energy-based voice activity detector with hangover.
#[derive(Debug, Clone, Copy)]
pub struct VoiceActivityDetector {
    /// Frames above this level (dBFS) are considered speech.
    pub threshold_db: f32,
    /// Frames to keep transmitting after speech stops (avoids clipping word endings).
    pub hold_frames: u32,
    hold: u32,
    /// Last measured level in dBFS.
    pub level_db: f32,
}

impl VoiceActivityDetector {
    pub fn new(threshold_db: f32, hold_frames: u32) -> Self {
        Self { threshold_db, hold_frames, hold: 0, level_db: -96.0 }
    }

    /// Returns true if the frame should be transmitted.
    pub fn is_active(&mut self, frame: &[f32]) -> bool {
        self.level_db = to_dbfs(rms(frame));
        if self.level_db >= self.threshold_db {
            self.hold = self.hold_frames;
            true
        } else if self.hold > 0 {
            self.hold -= 1;
            true
        } else {
            false
        }
    }
}

impl Default for VoiceActivityDetector {
    fn default() -> Self {
        Self::new(-45.0, 25)
    }
}

/// Soft clipper to avoid harsh distortion when mixing many speakers.
#[inline]
pub fn soft_clip(frame: &mut [f32]) {
    for s in frame {
        let x = *s;
        if x.abs() > 0.8 {
            *s = x.signum() * (0.8 + 0.2 * ((x.abs() - 0.8) / 0.2).tanh());
        }
    }
}

/// Converts float samples to `i16` with saturation.
#[inline]
pub fn f32_to_i16(input: &[f32], out: &mut [i16]) {
    for (o, s) in out.iter_mut().zip(input) {
        *o = (s.clamp(-1.0, 1.0) * 32767.0) as i16;
    }
}

/// Converts `i16` samples to float.
#[inline]
pub fn i16_to_f32(input: &[i16], out: &mut [f32]) {
    for (o, s) in out.iter_mut().zip(input) {
        *o = *s as f32 / 32768.0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn vad_hangover() {
        let mut vad = VoiceActivityDetector::new(-30.0, 2);
        let loud = [0.5f32; 480];
        let quiet = [0.0f32; 480];
        assert!(vad.is_active(&loud));
        assert!(vad.is_active(&quiet));
        assert!(vad.is_active(&quiet));
        assert!(!vad.is_active(&quiet));
    }

    #[test]
    fn dc_blocker_removes_offset() {
        let mut f = DcBlocker::default();
        let mut buf = vec![0.3f32; 48_000];
        f.process(&mut buf);
        assert!(buf[47_999].abs() < 0.01);
    }

    #[test]
    fn soft_clip_bounds() {
        let mut buf = [2.0f32, -3.0, 0.5];
        soft_clip(&mut buf);
        assert!(buf[0] <= 1.0 && buf[1] >= -1.0);
        assert_eq!(buf[2], 0.5);
    }

    #[test]
    fn gate_closes() {
        let mut g = NoiseGate::new(-40.0);
        let mut quiet = [0.001f32; 480];
        for _ in 0..100 {
            quiet = [0.001f32; 480];
            g.process(&mut quiet);
        }
        assert!(peak(&quiet) < 0.0001);
    }
}
