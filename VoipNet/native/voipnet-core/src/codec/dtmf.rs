//! DTMF: RFC 4733 (formerly RFC 2833) telephone-events, in-band tone generation and
//! Goertzel-based in-band detection.

const LOW: [f32; 4] = [697.0, 770.0, 852.0, 941.0];
const HIGH: [f32; 4] = [1209.0, 1336.0, 1477.0, 1633.0];
const KEYPAD: [[char; 4]; 4] = [['1', '2', '3', 'A'], ['4', '5', '6', 'B'], ['7', '8', '9', 'C'], ['*', '0', '#', 'D']];

/// Maps a DTMF digit to its RFC 4733 event code.
pub fn digit_to_event(d: char) -> Option<u8> {
    Some(match d.to_ascii_uppercase() {
        c @ '0'..='9' => c as u8 - b'0',
        '*' => 10,
        '#' => 11,
        c @ 'A'..='D' => 12 + (c as u8 - b'A'),
        _ => return None,
    })
}

pub fn event_to_digit(e: u8) -> Option<char> {
    Some(match e {
        0..=9 => (b'0' + e) as char,
        10 => '*',
        11 => '#',
        12..=15 => (b'A' + e - 12) as char,
        16 => '!', // flash
        _ => return None,
    })
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TelephoneEvent {
    pub event: u8,
    pub end: bool,
    pub volume: u8,
    pub duration: u16,
}

impl TelephoneEvent {
    pub fn encode(&self) -> [u8; 4] {
        let d = self.duration.to_be_bytes();
        [self.event, (u8::from(self.end) << 7) | (self.volume & 0x3F), d[0], d[1]]
    }

    pub fn decode(p: &[u8]) -> Option<Self> {
        (p.len() >= 4).then(|| Self {
            event: p[0],
            end: p[1] & 0x80 != 0,
            volume: p[1] & 0x3F,
            duration: u16::from_be_bytes([p[2], p[3]]),
        })
    }
}

fn digit_freqs(d: char) -> Option<(f32, f32)> {
    let d = d.to_ascii_uppercase();
    for (r, row) in KEYPAD.iter().enumerate() {
        if let Some(c) = row.iter().position(|&k| k == d) {
            return Some((LOW[r], HIGH[c]));
        }
    }
    None
}

/// Generates an in-band dual tone for `digit` into `out`.
pub fn generate_tone(digit: char, sample_rate: u32, duration_ms: u32, amplitude: i16, out: &mut Vec<i16>) -> bool {
    let Some((f1, f2)) = digit_freqs(digit) else { return false };
    let n = (sample_rate * duration_ms / 1000) as usize;
    let amp = amplitude as f32 / 2.0;
    let w1 = 2.0 * std::f32::consts::PI * f1 / sample_rate as f32;
    let w2 = 2.0 * std::f32::consts::PI * f2 / sample_rate as f32;
    out.reserve(n);
    out.extend((0..n).map(|i| (amp * ((w1 * i as f32).sin() + (w2 * i as f32).sin())) as i16));
    true
}

/// Streaming in-band DTMF detector. Feed PCM frames; returns newly detected digits
/// (a digit is reported once per key press).
pub struct DtmfDetector {
    sample_rate: u32,
    block: Vec<f32>,
    block_len: usize,
    coeffs: [f32; 8],
    last: Option<char>,
    stable: u32,
}

impl DtmfDetector {
    pub fn new(sample_rate: u32) -> Self {
        let block_len = (sample_rate as usize * 256) / 10_000; // ~25.6 ms, 205 @ 8 kHz
        let mut coeffs = [0.0; 8];
        for (i, f) in LOW.iter().chain(HIGH.iter()).enumerate() {
            coeffs[i] = 2.0 * (2.0 * std::f32::consts::PI * f / sample_rate as f32).cos();
        }
        Self { sample_rate, block: Vec::with_capacity(block_len), block_len, coeffs, last: None, stable: 0 }
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    pub fn process(&mut self, pcm: &[i16], detected: &mut Vec<char>) {
        for &s in pcm {
            self.block.push(s as f32);
            if self.block.len() == self.block_len {
                let digit = self.analyze();
                self.block.clear();
                match digit {
                    Some(d) if Some(d) == self.last => {
                        self.stable += 1;
                        if self.stable == 2 {
                            detected.push(d);
                        }
                    }
                    Some(d) => {
                        self.last = Some(d);
                        self.stable = 1;
                    }
                    None => {
                        self.last = None;
                        self.stable = 0;
                    }
                }
            }
        }
    }

    fn analyze(&self) -> Option<char> {
        let mut power = [0f32; 8];
        let mut energy = 0f32;
        for s in &self.block {
            energy += s * s;
        }
        if energy / (self.block_len as f32) < 1.0e4 {
            return None;
        }
        for (k, c) in self.coeffs.iter().enumerate() {
            let (mut q1, mut q2) = (0f32, 0f32);
            for &x in &self.block {
                let q0 = c * q1 - q2 + x;
                q2 = q1;
                q1 = q0;
            }
            power[k] = q1 * q1 + q2 * q2 - c * q1 * q2;
        }
        let (r, rp) = max_index(&power[..4]);
        let (c, cp) = max_index(&power[4..]);
        // Both tones must dominate their group and carry a significant share of block energy.
        let group_ok = |p: &[f32], best: usize, bp: f32| p.iter().enumerate().all(|(i, &v)| i == best || v * 6.0 < bp);
        let total = energy * self.block_len as f32 / 2.0;
        if !group_ok(&power[..4], r, rp) || !group_ok(&power[4..], c, cp) || (rp + cp) < total * 0.5 {
            return None;
        }
        // Twist check: tones within 8 dB of each other.
        let twist = rp.max(cp) / rp.min(cp).max(1.0);
        (twist < 6.3).then(|| KEYPAD[r][c])
    }
}

fn max_index(v: &[f32]) -> (usize, f32) {
    v.iter().copied().enumerate().fold((0, f32::MIN), |acc, (i, x)| if x > acc.1 { (i, x) } else { acc })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn event_mapping_roundtrip() {
        for d in "0123456789*#ABCD".chars() {
            assert_eq!(event_to_digit(digit_to_event(d).unwrap()), Some(d));
        }
        let e = TelephoneEvent { event: 5, end: true, volume: 10, duration: 1600 };
        assert_eq!(TelephoneEvent::decode(&e.encode()), Some(e));
    }

    #[test]
    fn detects_generated_tones() {
        for rate in [8000u32, 16000] {
            let mut det = DtmfDetector::new(rate);
            let mut pcm = Vec::new();
            for d in "159#".chars() {
                generate_tone(d, rate, 100, 12000, &mut pcm);
                pcm.extend(std::iter::repeat(0).take(rate as usize / 20));
            }
            let mut found = Vec::new();
            for chunk in pcm.chunks(rate as usize / 50) {
                det.process(chunk, &mut found);
            }
            assert_eq!(found.into_iter().collect::<String>(), "159#", "rate {rate}");
        }
    }

    #[test]
    fn silence_and_speech_like_noise_do_not_trigger() {
        let mut det = DtmfDetector::new(8000);
        let noise: Vec<i16> = (0..8000).map(|i| ((i * 7919 % 2001) as i16 - 1000) * 8).collect();
        let mut found = Vec::new();
        det.process(&noise, &mut found);
        det.process(&[0; 4000], &mut found);
        assert!(found.is_empty());
    }
}
