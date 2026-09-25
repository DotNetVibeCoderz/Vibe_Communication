//! RTP (RFC 3550 §5) and minimal RTCP packet handling.

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct RtpHeader {
    pub marker: bool,
    pub payload_type: u8,
    pub sequence: u16,
    pub timestamp: u32,
    pub ssrc: u32,
}

pub const RTP_HEADER_LEN: usize = 12;

/// Zero-copy view of a received RTP packet.
#[derive(Debug, Clone, Copy)]
pub struct RtpPacketRef<'a> {
    pub header: RtpHeader,
    pub payload: &'a [u8],
}

impl<'a> RtpPacketRef<'a> {
    pub fn parse(data: &'a [u8]) -> Option<Self> {
        if data.len() < RTP_HEADER_LEN || data[0] >> 6 != 2 {
            return None;
        }
        let padding = data[0] & 0x20 != 0;
        let extension = data[0] & 0x10 != 0;
        let csrc_count = (data[0] & 0x0F) as usize;
        let mut offset = RTP_HEADER_LEN + csrc_count * 4;
        if extension {
            if data.len() < offset + 4 {
                return None;
            }
            let words = u16::from_be_bytes([data[offset + 2], data[offset + 3]]) as usize;
            offset += 4 + words * 4;
        }
        let mut end = data.len();
        if padding {
            let pad = *data.last()? as usize;
            end = end.checked_sub(pad)?;
        }
        if offset > end {
            return None;
        }
        Some(Self {
            header: RtpHeader {
                marker: data[1] & 0x80 != 0,
                payload_type: data[1] & 0x7F,
                sequence: u16::from_be_bytes([data[2], data[3]]),
                timestamp: u32::from_be_bytes([data[4], data[5], data[6], data[7]]),
                ssrc: u32::from_be_bytes([data[8], data[9], data[10], data[11]]),
            },
            payload: &data[offset..end],
        })
    }
}

impl RtpHeader {
    pub fn write(&self, payload: &[u8], out: &mut Vec<u8>) {
        out.reserve(RTP_HEADER_LEN + payload.len());
        out.push(0x80);
        out.push((u8::from(self.marker) << 7) | (self.payload_type & 0x7F));
        out.extend_from_slice(&self.sequence.to_be_bytes());
        out.extend_from_slice(&self.timestamp.to_be_bytes());
        out.extend_from_slice(&self.ssrc.to_be_bytes());
        out.extend_from_slice(payload);
    }
}

/// RTP vs RTCP demultiplexing on a shared port (RFC 5761 §4), plus STUN/DTLS (RFC 7983).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PacketClass {
    Stun,
    Dtls,
    Rtp,
    Rtcp,
    /// Key agreement on the media path (RFC 6189): an RTP-shaped header with the ZRTP magic.
    Zrtp,
    Unknown,
}

pub fn classify(data: &[u8]) -> PacketClass {
    // ZRTP reuses the RTP header shape with version zero and its own magic word.
    if data.len() >= 12 && data[0] == 0x10 && data[4..8] == *b"ZRTP" {
        return PacketClass::Zrtp;
    }

    match data.first() {
        Some(0..=3) => PacketClass::Stun,
        Some(20..=63) => PacketClass::Dtls,
        Some(128..=191) if data.len() >= 2 => {
            if (192..=223).contains(&data[1]) {
                PacketClass::Rtcp
            } else {
                PacketClass::Rtp
            }
        }
        _ => PacketClass::Unknown,
    }
}

/// Builds an RTCP Sender Report followed by an SDES CNAME chunk (compound packet).
/// One RTCP reception report block (RFC 3550 §6.4.1).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ReportBlock {
    /// Source this block reports on.
    pub ssrc: u32,
    /// Loss since the previous report, as a fraction of 256.
    pub fraction_lost: u8,
    /// Cumulative packets lost (24-bit signed on the wire).
    pub cumulative_lost: i32,
    pub highest_seq: u32,
    /// Interarrival jitter in RTP timestamp units.
    pub jitter: u32,
    /// Middle 32 bits of the NTP timestamp of the last sender report received.
    pub last_sr: u32,
    /// Delay since that sender report, in units of 1/65536 s.
    pub delay_since_last_sr: u32,
}

impl ReportBlock {
    fn write(&self, out: &mut Vec<u8>) {
        out.extend_from_slice(&self.ssrc.to_be_bytes());
        out.push(self.fraction_lost);
        let lost = self.cumulative_lost.clamp(-0x7F_FFFF, 0x7F_FFFF) & 0xFF_FFFF;
        out.extend_from_slice(&lost.to_be_bytes()[1..]);
        out.extend_from_slice(&self.highest_seq.to_be_bytes());
        out.extend_from_slice(&self.jitter.to_be_bytes());
        out.extend_from_slice(&self.last_sr.to_be_bytes());
        out.extend_from_slice(&self.delay_since_last_sr.to_be_bytes());
    }

    fn parse(b: &[u8]) -> Self {
        let word = |i: usize| u32::from_be_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]);
        // Sign-extend the 24-bit cumulative loss.
        let raw = ((b[5] as i32) << 16) | ((b[6] as i32) << 8) | b[7] as i32;
        Self {
            ssrc: word(0),
            fraction_lost: b[4],
            cumulative_lost: if raw & 0x80_0000 != 0 { raw - 0x100_0000 } else { raw },
            highest_seq: word(8),
            jitter: word(12),
            last_sr: word(16),
            delay_since_last_sr: word(20),
        }
    }
}

/// VoIP metrics of an RTCP extended report (RFC 3611 §4.7). Only the fields the engine fills or
/// reads are modelled; the rest are sent as "unavailable" per the RFC.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct VoipMetrics {
    /// Source the metrics describe.
    pub ssrc: u32,
    /// Lost packets as a fraction of 256.
    pub loss_rate: u8,
    /// Packets discarded by the jitter buffer, as a fraction of 256.
    pub discard_rate: u8,
    /// Loss density inside bursts and gaps, as fractions of 256 (RFC 3611 §4.7.2).
    pub burst_density: u8,
    pub gap_density: u8,
    /// Mean burst and gap length in milliseconds.
    pub burst_duration_ms: u16,
    pub gap_duration_ms: u16,
    /// Round-trip delay in milliseconds.
    pub round_trip_ms: u16,
    /// Playout delay added by this end (jitter buffer plus packetization), in milliseconds.
    pub end_system_delay_ms: u16,
    /// ITU-T G.107 R factor, 0–100.
    pub r_factor: u8,
    /// Listening quality MOS, ×10 (so 43 means 4.3).
    pub mos_lq: u8,
    /// Conversational quality MOS, ×10.
    pub mos_cq: u8,
    /// Nominal and maximum jitter buffer delay in milliseconds.
    pub jb_nominal_ms: u16,
    pub jb_maximum_ms: u16,
}

/// Builds an extended report (RFC 3611) carrying one VoIP metrics block.
pub fn build_xr_voip_metrics(ssrc: u32, metrics: VoipMetrics) -> Vec<u8> {
    let mut out = vec![0x80, 207, 0, 10];
    out.extend_from_slice(&ssrc.to_be_bytes());
    // Block type 7, reserved byte, block length 8 words.
    out.extend_from_slice(&[7, 0, 0, 8]);
    out.extend_from_slice(&metrics.ssrc.to_be_bytes());
    out.push(metrics.loss_rate);
    out.push(metrics.discard_rate);
    out.push(metrics.burst_density);
    out.push(metrics.gap_density);
    out.extend_from_slice(&metrics.burst_duration_ms.to_be_bytes());
    out.extend_from_slice(&metrics.gap_duration_ms.to_be_bytes());
    out.extend_from_slice(&metrics.round_trip_ms.to_be_bytes());
    out.extend_from_slice(&metrics.end_system_delay_ms.to_be_bytes());
    out.push(127); // signal level: unavailable
    out.push(127); // noise level: unavailable
    out.push(127); // residual echo return loss: unavailable
    out.push(16); // Gmin, the RFC 3611 default
    out.push(metrics.r_factor);
    out.push(127); // external R factor: unavailable
    out.push(metrics.mos_lq);
    out.push(metrics.mos_cq);
    out.push(0); // RX config: adaptive jitter buffer flags left unset
    out.push(0); // reserved
    out.extend_from_slice(&metrics.jb_nominal_ms.to_be_bytes());
    out.extend_from_slice(&metrics.jb_maximum_ms.to_be_bytes());
    out.extend_from_slice(&metrics.jb_maximum_ms.to_be_bytes()); // absolute maximum
    out
}

fn parse_voip_metrics(b: &[u8]) -> Option<VoipMetrics> {
    if b.len() < 36 || b[0] != 7 {
        return None;
    }
    let word = |i: usize| u16::from_be_bytes([b[i], b[i + 1]]);
    Some(VoipMetrics {
        ssrc: u32::from_be_bytes([b[4], b[5], b[6], b[7]]),
        loss_rate: b[8],
        discard_rate: b[9],
        burst_density: b[10],
        gap_density: b[11],
        burst_duration_ms: word(12),
        gap_duration_ms: word(14),
        round_trip_ms: word(16),
        end_system_delay_ms: word(18),
        r_factor: b[24],
        mos_lq: b[26],
        mos_cq: b[27],
        jb_nominal_ms: word(30),
        jb_maximum_ms: word(32),
    })
}

/// An RTCP packet the engine cares about.
#[derive(Debug, Clone, PartialEq)]
pub enum RtcpPacket {
    /// A sender report: its NTP and RTP timestamps are the same instant on two clocks, which is
    /// what lets a receiver line audio up with video (RFC 3550 6.4.1).
    SenderReport { ssrc: u32, ntp: u64, rtp_ts: u32, reports: Vec<ReportBlock> },
    ReceiverReport { ssrc: u32, reports: Vec<ReportBlock> },
    ExtendedReport { ssrc: u32, metrics: VoipMetrics },
    Bye { ssrc: u32 },
    /// The peer asks for a keyframe: Picture Loss Indication (RFC 4585) or Full Intra Request (RFC 5104).
    KeyframeRequest { ssrc: u32, full: bool },
    /// The peer says how much it can receive (REMB), in bits per second.
    ReceiverEstimate { ssrc: u32, bitrate: u64 },
}

/// Parses a compound RTCP packet, ignoring types the engine does not use.
pub fn parse_rtcp(data: &[u8]) -> Vec<RtcpPacket> {
    let mut out = Vec::new();
    let mut rest = data;
    while rest.len() >= 8 {
        let count = (rest[0] & 0x1F) as usize;
        let length = 4 + 4 * u16::from_be_bytes([rest[2], rest[3]]) as usize;
        if length > rest.len() || rest[0] >> 6 != 2 {
            break;
        }
        let (packet, tail) = rest.split_at(length);
        let ssrc = u32::from_be_bytes([packet[4], packet[5], packet[6], packet[7]]);
        let blocks = |start: usize| -> Vec<ReportBlock> {
            (0..count)
                .map(|i| start + i * 24)
                .take_while(|&o| o + 24 <= packet.len())
                .map(|o| ReportBlock::parse(&packet[o..o + 24]))
                .collect()
        };
        match packet[1] {
            200 if packet.len() >= 28 => out.push(RtcpPacket::SenderReport {
                ssrc,
                ntp: u64::from_be_bytes(packet[8..16].try_into().expect("8 bytes")),
                rtp_ts: u32::from_be_bytes(packet[16..20].try_into().expect("4 bytes")),
                reports: blocks(28),
            }),
            201 => out.push(RtcpPacket::ReceiverReport { ssrc, reports: blocks(8) }),
            207 => {
                // Extended report: walk its blocks and keep the VoIP metrics one.
                let mut at = 8;
                while at + 4 <= packet.len() {
                    let block_len = 4 + 4 * u16::from_be_bytes([packet[at + 2], packet[at + 3]]) as usize;
                    if let Some(metrics) = packet.get(at..at + block_len).and_then(parse_voip_metrics) {
                        out.push(RtcpPacket::ExtendedReport { ssrc, metrics });
                    }
                    at += block_len.max(4);
                }
            }
            203 => out.push(RtcpPacket::Bye { ssrc }),
            // Payload-specific feedback (RFC 4585 6.3): FMT 1 is PLI, FMT 4 is FIR (RFC 5104 4.3.1).
            206 if matches!(count, 1 | 4) => out.push(RtcpPacket::KeyframeRequest { ssrc, full: count == 4 }),
            // FMT 15 with the identifier "REMB" carries the receiver's estimate of what it can take.
            206 if count == 15 && packet.len() >= 20 && &packet[12..16] == b"REMB" => {
                let exponent = u32::from(packet[17] >> 2);
                let mantissa = u32::from_be_bytes([0, packet[17] & 0x03, packet[18], packet[19]]);
                out.push(RtcpPacket::ReceiverEstimate { ssrc, bitrate: u64::from(mantissa) << exponent });
            }
            _ => {}
        }
        rest = tail;
    }
    out
}

pub fn build_sender_report(ssrc: u32, ntp: u64, rtp_ts: u32, packets: u32, octets: u32, cname: &str, report: Option<ReportBlock>) -> Vec<u8> {
    let mut out = Vec::with_capacity(96);
    let count = u8::from(report.is_some());
    out.extend_from_slice(&[0x80 | count, 200, 0, 6 + 6 * count as u8]);
    out.extend_from_slice(&ssrc.to_be_bytes());
    out.extend_from_slice(&ntp.to_be_bytes());
    out.extend_from_slice(&rtp_ts.to_be_bytes());
    out.extend_from_slice(&packets.to_be_bytes());
    out.extend_from_slice(&octets.to_be_bytes());
    if let Some(block) = report {
        block.write(&mut out);
    }

    let name = &cname.as_bytes()[..cname.len().min(255)];
    let chunk_len = 4 + 2 + name.len() + 1; // ssrc + type/len + name + END
    let padded = chunk_len.div_ceil(4) * 4;
    out.extend_from_slice(&[0x81, 202]);
    out.extend_from_slice(&((padded / 4) as u16).to_be_bytes());
    out.extend_from_slice(&ssrc.to_be_bytes());
    out.push(1);
    out.push(name.len() as u8);
    out.extend_from_slice(name);
    out.resize(out.len() + (padded - chunk_len) + 1, 0);
    out
}

/// A receiver-only report, sent when this side is not transmitting.
pub fn build_receiver_report(ssrc: u32, report: ReportBlock) -> Vec<u8> {
    let mut out = vec![0x81, 201, 0, 7];
    out.extend_from_slice(&ssrc.to_be_bytes());
    report.write(&mut out);
    out
}

/// Asks the peer for a keyframe. `full` sends a Full Intra Request (RFC 5104), which needs a sequence
/// number so repeats can be told apart; otherwise a Picture Loss Indication (RFC 4585).
pub fn build_keyframe_request(ssrc: u32, media_ssrc: u32, full: bool, sequence: u8) -> Vec<u8> {
    let mut out = vec![0x80 | if full { 4 } else { 1 }, 206, 0, if full { 4 } else { 2 }];
    out.extend_from_slice(&ssrc.to_be_bytes());
    out.extend_from_slice(&media_ssrc.to_be_bytes());
    if full {
        // FCI: the SSRC to refresh, the request sequence number, then three reserved bytes.
        out.extend_from_slice(&media_ssrc.to_be_bytes());
        out.extend_from_slice(&[sequence, 0, 0, 0]);
    }
    out
}

/// Tells the sender how much this side can receive (REMB, draft-alvestrand-rmcat-remb).
///
/// The bitrate travels as a 6-bit exponent and an 18-bit mantissa, so it is rounded up to the next
/// value that fits rather than reported as something smaller than the estimate.
pub fn build_receiver_estimate(ssrc: u32, media_ssrc: u32, bitrate: u64) -> Vec<u8> {
    let mut exponent = 0u32;
    let mut mantissa = bitrate;
    while mantissa >= 0x0003_FFFF {
        mantissa = mantissa.div_ceil(2);
        exponent += 1;
    }

    let mantissa = mantissa as u32;
    let mut out = vec![0x80 | 15, 206, 0, 5];
    out.extend_from_slice(&ssrc.to_be_bytes());
    out.extend_from_slice(&0u32.to_be_bytes()); // unused in REMB: the streams are named below
    out.extend_from_slice(b"REMB");
    out.push(1); // one SSRC follows
    out.push(((exponent as u8) << 2) | ((mantissa >> 16) & 0x03) as u8);
    out.extend_from_slice(&((mantissa & 0xFFFF) as u16).to_be_bytes());
    out.extend_from_slice(&media_ssrc.to_be_bytes());
    out
}

pub fn build_bye(ssrc: u32) -> Vec<u8> {
    let mut out = vec![0x81, 203, 0, 1];
    out.extend_from_slice(&ssrc.to_be_bytes());
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn receiver_estimates_round_trip() {
        for bitrate in [64_000u64, 300_000, 1_500_000, 12_000_000] {
            let packet = build_receiver_estimate(0x1111_2222, 0x3333_4444, bitrate);
            let Some(RtcpPacket::ReceiverEstimate { ssrc, bitrate: parsed }) = parse_rtcp(&packet).into_iter().next() else {
                panic!("expected a REMB for {bitrate}");
            };

            assert_eq!(ssrc, 0x1111_2222);
            // The wire format is lossy above 18 bits, so it may round up but never below the estimate.
            assert!(parsed >= bitrate, "{parsed} < {bitrate}");
            assert!(parsed <= bitrate + bitrate / 100, "{parsed} is more than a percent above {bitrate}");
        }
    }

    #[test]
    fn keyframe_requests_round_trip() {
        for (full, expected) in [(false, false), (true, true)] {
            let bytes = build_keyframe_request(1, 2, full, 7);
            assert_eq!(bytes.len() % 4, 0);
            let parsed = parse_rtcp(&bytes);
            assert_eq!(parsed, vec![RtcpPacket::KeyframeRequest { ssrc: 1, full: expected }]);
        }
    }

    #[test]
    fn roundtrip() {
        let h = RtpHeader { marker: true, payload_type: 0, sequence: 65535, timestamp: 0xDEADBEEF, ssrc: 42 };
        let mut buf = Vec::new();
        h.write(&[1, 2, 3], &mut buf);
        let p = RtpPacketRef::parse(&buf).unwrap();
        assert_eq!(p.header, h);
        assert_eq!(p.payload, &[1, 2, 3]);
        assert_eq!(classify(&buf), PacketClass::Rtp);
    }

    #[test]
    fn handles_csrc_extension_and_padding() {
        let mut buf = vec![0xB1, 8, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2];
        buf.extend_from_slice(&[0, 0, 0, 9]); // CSRC
        buf.extend_from_slice(&[0xBE, 0xEF, 0, 1, 1, 2, 3, 4]); // one-word extension
        buf.extend_from_slice(&[7, 7, 0, 2]); // payload + 2 padding bytes
        let p = RtpPacketRef::parse(&buf).unwrap();
        assert_eq!(p.payload, &[7, 7]);
    }


    #[test]
    fn report_blocks_survive_a_roundtrip() {
        let block = ReportBlock {
            ssrc: 0xDEAD_BEEF,
            fraction_lost: 64,
            cumulative_lost: -12,
            highest_seq: 70_000,
            jitter: 1234,
            last_sr: 0xAABB_CCDD,
            delay_since_last_sr: 65_536,
        };
        let sr = build_sender_report(7, 0x1122_3344_5566_7788, 900, 50, 8000, "voipnet", Some(block));
        assert_eq!(classify(&sr), PacketClass::Rtcp);
        let parsed = parse_rtcp(&sr);
        assert_eq!(parsed.len(), 1, "the SDES packet is ignored");
        match &parsed[0] {
            RtcpPacket::SenderReport { ssrc, ntp, rtp_ts, reports } => {
                assert_eq!((*ssrc, *ntp, *rtp_ts), (7, 0x1122_3344_5566_7788, 900));
                assert_eq!(reports, &[block]);
            }
            other => panic!("unexpected {other:?}"),
        }

        let rr = build_receiver_report(9, block);
        assert_eq!(parse_rtcp(&rr), vec![RtcpPacket::ReceiverReport { ssrc: 9, reports: vec![block] }]);
        assert_eq!(parse_rtcp(&build_bye(3)), vec![RtcpPacket::Bye { ssrc: 3 }]);
        assert!(parse_rtcp(&[0u8; 4]).is_empty());
    }


    #[test]
    fn voip_metrics_survive_a_roundtrip() {
        let metrics = VoipMetrics {
            ssrc: 0x1234_5678,
            loss_rate: 5,
            discard_rate: 2,
            burst_density: 120,
            gap_density: 1,
            burst_duration_ms: 80,
            gap_duration_ms: 4000,
            round_trip_ms: 42,
            end_system_delay_ms: 60,
            r_factor: 88,
            mos_lq: 42,
            mos_cq: 41,
            jb_nominal_ms: 40,
            jb_maximum_ms: 300,
        };
        let xr = build_xr_voip_metrics(9, metrics);
        assert_eq!(classify(&xr), PacketClass::Rtcp);
        assert_eq!(parse_rtcp(&xr), vec![RtcpPacket::ExtendedReport { ssrc: 9, metrics }]);
    }

    #[test]
    fn sender_report_is_rtcp() {
        let sr = build_sender_report(1, 2, 3, 4, 5, "voipnet@host", None);
        assert_eq!(sr.len() % 4, 0);
        assert_eq!(classify(&sr), PacketClass::Rtcp);
    }
}
