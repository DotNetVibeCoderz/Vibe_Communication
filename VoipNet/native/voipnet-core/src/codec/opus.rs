//! Opus (RFC 6716, RTP payload RFC 7587) on libopus.
//!
//! Voice is encoded mono at 48 kHz (the SDP still says `opus/48000/2`, as RFC 7587 requires) with
//! in-band FEC. Losses are concealed by libopus, and recovered from the FEC data carried in the next
//! packet when that packet has already arrived.

use opus::{Application, Bitrate, Channels, Decoder, Encoder};

use super::AudioCodec;

/// libopus sample rate used for both directions.
pub const OPUS_RATE: u32 = 48000;
/// Largest Opus packet we produce (RFC 6716 allows up to 1275 bytes per frame).
const MAX_PACKET: usize = 1500;

pub struct Opus {
    payload_type: u8,
    encoder: Encoder,
    decoder: Decoder,
    packet: Box<[u8; MAX_PACKET]>,
    pcm: Vec<i16>,
    bitrate: i32,
}

impl Opus {
    pub fn new(payload_type: u8) -> Option<Self> {
        let mut encoder = Encoder::new(OPUS_RATE, Channels::Mono, Application::Voip).ok()?;
        // 32 kbit/s wideband voice with forward error correction sized for ~10% loss.
        let _ = encoder.set_bitrate(Bitrate::Bits(32_000));
        let _ = encoder.set_inband_fec(true);
        let _ = encoder.set_packet_loss_perc(10);
        let _ = encoder.set_complexity(5);
        let decoder = Decoder::new(OPUS_RATE, Channels::Mono).ok()?;
        Some(Self {
            payload_type,
            encoder,
            decoder,
            packet: Box::new([0; MAX_PACKET]),
            pcm: vec![0; OPUS_RATE as usize * 120 / 1000],
            bitrate: 32_000,
        })
    }

    fn decode_into(&mut self, payload: &[u8], fec: bool, samples: usize, out: &mut Vec<i16>) -> bool {
        // With FEC or PLC the output length must equal the missing duration.
        let wanted = if payload.is_empty() || fec { samples.min(self.pcm.len()) } else { self.pcm.len() };
        match self.decoder.decode(payload, &mut self.pcm[..wanted], fec) {
            Ok(n) if n > 0 => {
                out.extend_from_slice(&self.pcm[..n]);
                true
            }
            _ => false,
        }
    }
}

impl AudioCodec for Opus {
    fn name(&self) -> &'static str {
        "opus"
    }

    fn payload_type(&self) -> u8 {
        self.payload_type
    }

    fn clock_rate(&self) -> u32 {
        OPUS_RATE
    }

    fn sample_rate(&self) -> u32 {
        OPUS_RATE
    }

    fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>) {
        if let Ok(n) = self.encoder.encode(pcm, &mut self.packet[..]) {
            out.extend_from_slice(&self.packet[..n]);
        }
    }

    fn decode(&mut self, payload: &[u8], out: &mut Vec<i16>) {
        let _ = self.decode_into(payload, false, 0, out);
    }

    fn conceal(&mut self, samples: usize, next_payload: Option<&[u8]>, out: &mut Vec<i16>) -> bool {
        match next_payload {
            Some(next) if self.decode_into(next, true, samples, out) => true,
            _ => self.decode_into(&[], false, samples, out),
        }
    }

    /// Trades bitrate for redundancy as the peer reports loss: FEC needs headroom, and a congested
    /// path recovers faster at a lower rate.
    fn set_network_quality(&mut self, loss_percent: f64, _round_trip_ms: f64) {
        let loss = loss_percent.clamp(0.0, 40.0);
        let bitrate = match loss {
            l if l >= 15.0 => 20_000,
            l if l >= 5.0 => 24_000,
            _ => 32_000,
        };
        if bitrate != self.bitrate {
            self.bitrate = bitrate;
            let _ = self.encoder.set_bitrate(Bitrate::Bits(bitrate));
        }
        let _ = self.encoder.set_packet_loss_perc(loss.round() as i32);
    }

    fn set_dtx(&mut self, enabled: bool) {
        let _ = self.encoder.set_dtx(enabled);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(ms: usize) -> Vec<i16> {
        (0..OPUS_RATE as usize * ms / 1000).map(|i| (9000.0 * (i as f64 * 2.0 * std::f64::consts::PI * 440.0 / OPUS_RATE as f64).sin()) as i16).collect()
    }

    fn correlation(a: &[i16], b: &[i16]) -> f64 {
        let dot: f64 = a.iter().zip(b).map(|(x, y)| *x as f64 * *y as f64).sum();
        let na: f64 = a.iter().map(|x| (*x as f64).powi(2)).sum::<f64>().sqrt();
        let nb: f64 = b.iter().map(|x| (*x as f64).powi(2)).sum::<f64>().sqrt();
        dot / (na * nb).max(1.0)
    }

    #[test]
    fn encodes_compactly_and_decodes_the_tone() {
        let mut tx = Opus::new(111).unwrap();
        let mut rx = Opus::new(111).unwrap();
        let input = tone(400);
        let (mut decoded, mut bytes) = (Vec::new(), 0);
        for frame in input.chunks_exact(960) {
            let mut packet = Vec::new();
            tx.encode(frame, &mut packet);
            bytes += packet.len();
            assert!(!packet.is_empty() && packet.len() < 200, "packet of {} bytes", packet.len());
            rx.decode(&packet, &mut decoded);
        }
        assert_eq!(decoded.len(), input.len());
        assert!(bytes < input.len() * 2 / 10, "{bytes} bytes for {} samples", input.len());
        // Opus adds a few ms of algorithmic delay; compare after aligning by the encoder lookahead.
        let lookahead = tx.encoder.get_lookahead().unwrap() as usize;
        let tail = 9600;
        let c = correlation(&input[tail - lookahead..input.len() - lookahead], &decoded[tail..]);
        assert!(c > 0.9, "correlation {c}");
    }

    #[test]
    fn recovers_a_lost_frame_from_fec_or_plc() {
        let mut tx = Opus::new(111).unwrap();
        let mut rx = Opus::new(111).unwrap();
        let packets: Vec<Vec<u8>> = tone(200)
            .chunks_exact(960)
            .map(|f| {
                let mut p = Vec::new();
                tx.encode(f, &mut p);
                p
            })
            .collect();
        let mut out = Vec::new();
        for (i, p) in packets.iter().enumerate() {
            if i == 5 {
                // Packet 5 is lost; packet 6 carries FEC for it.
                assert!(rx.conceal(960, Some(&packets[6]), &mut out));
                continue;
            }
            rx.decode(p, &mut out);
        }
        assert_eq!(out.len(), packets.len() * 960);
        let concealed = &out[5 * 960..6 * 960];
        assert!(concealed.iter().map(|s| s.unsigned_abs()).max().unwrap() > 1000, "concealment is silent");
        let mut plc = Vec::new();
        assert!(rx.conceal(960, None, &mut plc));
        assert_eq!(plc.len(), 960);
    }
}
