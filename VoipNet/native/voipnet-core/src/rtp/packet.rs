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
    Unknown,
}

pub fn classify(data: &[u8]) -> PacketClass {
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
pub fn build_sender_report(ssrc: u32, ntp: u64, rtp_ts: u32, packets: u32, octets: u32, cname: &str) -> Vec<u8> {
    let mut out = Vec::with_capacity(64);
    out.extend_from_slice(&[0x80, 200, 0, 6]);
    out.extend_from_slice(&ssrc.to_be_bytes());
    out.extend_from_slice(&ntp.to_be_bytes());
    out.extend_from_slice(&rtp_ts.to_be_bytes());
    out.extend_from_slice(&packets.to_be_bytes());
    out.extend_from_slice(&octets.to_be_bytes());

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

pub fn build_bye(ssrc: u32) -> Vec<u8> {
    let mut out = vec![0x81, 203, 0, 1];
    out.extend_from_slice(&ssrc.to_be_bytes());
    out
}

#[cfg(test)]
mod tests {
    use super::*;

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
    fn sender_report_is_rtcp() {
        let sr = build_sender_report(1, 2, 3, 4, 5, "voipnet@host");
        assert_eq!(sr.len() % 4, 0);
        assert_eq!(classify(&sr), PacketClass::Rtcp);
    }
}
