//! Echo cancellation, noise suppression and gain control on the WebRTC audio processing pipeline
//! (the pure-Rust `sonora` port of AEC3, the noise suppressor and AGC2).
//!
//! The engine sees both sides of the loop: audio it plays out to the application is the far end
//! (render), and audio the application sends is the near end (capture). Echo cancellation therefore
//! only helps when the application really plays the inbound audio through a speaker and feeds a
//! microphone back — with headsets there is no echo to remove, and AEC3 detects that itself.

use sonora::config::{Config, EchoCanceller, GainController2, NoiseSuppression, NoiseSuppressionLevel};
use sonora::{AudioProcessing, StreamConfig};

/// Rates the processor runs at; anything else is passed through untouched.
const SUPPORTED_RATES: [u32; 4] = [8000, 16000, 32000, 48000];

pub struct AudioEnhancer {
    apm: AudioProcessing,
    /// Samples in one 10 ms frame, the unit the pipeline works in.
    frame: usize,
    scratch: Vec<i16>,
    /// Device round trip in milliseconds, re-applied before every capture frame because the pipeline
    /// treats it as per-frame stream state.
    delay_ms: Option<i32>,
}

impl AudioEnhancer {
    /// Builds a processor for `rate`, or `None` when nothing is enabled or the rate is unsupported.
    pub fn new(rate: u32, echo_cancellation: bool, noise_suppression: bool, auto_gain: bool) -> Option<Self> {
        if !(echo_cancellation || noise_suppression || auto_gain) || !SUPPORTED_RATES.contains(&rate) {
            return None;
        }
        let config = Config {
            echo_canceller: echo_cancellation.then(EchoCanceller::default),
            noise_suppression: noise_suppression.then(|| NoiseSuppression {
                level: NoiseSuppressionLevel::Moderate,
                ..Default::default()
            }),
            gain_controller2: auto_gain.then(GainController2::default),
            ..Default::default()
        };
        let stream = StreamConfig::new(rate, 1);
        let apm = AudioProcessing::builder().config(config).capture_config(stream).render_config(stream).build();
        let frame = rate as usize / 100;
        Some(Self { apm, frame, scratch: vec![0; frame], delay_ms: None })
    }

    /// Feeds the far-end signal (what the user hears) so the echo canceller knows what to remove.
    pub fn render(&mut self, pcm: &[i16]) {
        for chunk in pcm.chunks_exact(self.frame) {
            let _ = self.apm.process_render_i16(chunk, &mut self.scratch);
        }
    }

    /// How long it takes for audio handed to `render` to come back through the microphone: the sum of
    /// the playback and capture buffers. AEC3 estimates the delay itself, but starting from the device's
    /// real latency makes it converge sooner and hold on to the alignment.
    pub fn set_delay_ms(&mut self, delay_ms: u32) {
        self.delay_ms = (delay_ms > 0).then_some(delay_ms.min(5000) as i32);
    }

    /// Cleans the near-end signal (what the user says) in place.
    pub fn capture(&mut self, pcm: &mut [i16]) {
        if let Some(delay) = self.delay_ms {
            let _ = self.apm.set_stream_delay_ms(delay);
        }
        for chunk in pcm.chunks_exact_mut(self.frame) {
            if self.apm.process_capture_i16(chunk, &mut self.scratch).is_ok() {
                chunk.copy_from_slice(&self.scratch);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rms(pcm: &[i16]) -> f64 {
        (pcm.iter().map(|&s| (s as f64).powi(2)).sum::<f64>() / pcm.len().max(1) as f64).sqrt()
    }

    #[test]
    fn disabled_or_unsupported_rates_have_no_processor() {
        assert!(AudioEnhancer::new(48000, false, false, false).is_none());
        assert!(AudioEnhancer::new(44100, true, true, false).is_none());
        assert!(AudioEnhancer::new(48000, false, true, false).is_some());
    }

    #[test]
    fn noise_suppression_lowers_a_hiss() {
        let rate = 16000;
        let mut enhancer = AudioEnhancer::new(rate, false, true, false).unwrap();
        let mut seed = 12345u32;
        let mut noise: Vec<i16> = (0..rate as usize * 2)
            .map(|_| {
                seed = seed.wrapping_mul(1_103_515_245).wrapping_add(12345);
                ((seed >> 16) as i16) / 12
            })
            .collect();
        let before = rms(&noise);
        enhancer.capture(&mut noise);
        // Judge the tail: the suppressor needs a moment to learn the noise floor.
        let after = rms(&noise[rate as usize..]);
        assert!(after < before * 0.8, "noise {before} -> {after}");
    }

    #[test]
    fn echo_canceller_removes_what_was_played() {
        let rate = 16000;
        let mut enhancer = AudioEnhancer::new(rate, true, false, false).unwrap();
        // The microphone below lags the loudspeaker by two 10 ms frames, which is what a device would
        // report; telling the canceller about it is where `set_delay_ms` comes in.
        enhancer.set_delay_ms(20);
        let frame = rate as usize / 100;
        // Broadband noise stands in for far-end speech: AEC3 needs a rich signal to model the path.
        let mut seed = 987u32;
        let far: Vec<i16> = (0..frame * 400)
            .map(|_| {
                seed = seed.wrapping_mul(1_103_515_245).wrapping_add(12345);
                ((seed >> 16) as i16) / 6
            })
            .collect();
        let mut residual = Vec::new();
        for (i, chunk) in far.chunks_exact(frame).enumerate() {
            enhancer.render(chunk);
            // The microphone hears only the loudspeaker, two frames later and a little quieter.
            let mut mic: Vec<i16> = match i.checked_sub(2) {
                Some(j) => far[j * frame..(j + 1) * frame].iter().map(|s| s / 2).collect(),
                None => vec![0; frame],
            };
            enhancer.capture(&mut mic);
            if i > 300 {
                residual.extend_from_slice(&mic);
            }
        }
        let echo = rms(&far) / 2.0;
        let left = rms(&residual);
        assert!(left < echo * 0.5, "echo {echo} -> {left}");
    }
}
