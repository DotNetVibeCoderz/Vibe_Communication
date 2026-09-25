//! Video RTP payload formats: H.264 (RFC 6184) and VP8 (RFC 7741).
//!
//! Encoded frames are far larger than a datagram, so each frame is split across packets and put back
//! together on the other side. A frame is complete when the packet with the marker bit arrives and no
//! sequence number is missing; anything else is dropped, because half a frame shown is worse than a
//! frame skipped — the decoder would show garbage until the next keyframe.

/// Largest RTP payload that fits a typical path without IP fragmentation.
pub const MAX_PAYLOAD: usize = 1200;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VideoFormat {
    H264,
    Vp8,
}

/// A frame ready for the decoder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VideoFrame {
    /// H.264 access unit in Annex B form (start codes), or a VP8 frame.
    pub data: Vec<u8>,
    pub timestamp: u32,
    /// The frame can be decoded without any earlier frame.
    pub keyframe: bool,
}

/// Splits encoded frames into RTP payloads.
pub struct VideoPacketizer {
    format: VideoFormat,
    max_payload: usize,
    vp8_picture_id: u16,
}

impl VideoPacketizer {
    pub fn new(format: VideoFormat) -> Self {
        Self { format, max_payload: MAX_PAYLOAD, vp8_picture_id: 0 }
    }

    pub fn with_max_payload(mut self, bytes: usize) -> Self {
        self.max_payload = bytes.clamp(64, 1500);
        self
    }

    /// Packetizes one frame. The last payload is the one that carries the marker bit.
    pub fn packetize(&mut self, frame: &[u8]) -> Vec<Vec<u8>> {
        match self.format {
            VideoFormat::H264 => self.packetize_h264(frame),
            VideoFormat::Vp8 => self.packetize_vp8(frame),
        }
    }

    fn packetize_h264(&self, access_unit: &[u8]) -> Vec<Vec<u8>> {
        let mut out = Vec::new();
        for nal in annex_b_units(access_unit) {
            if nal.is_empty() {
                continue;
            }
            if nal.len() <= self.max_payload {
                out.push(nal.to_vec());
                continue;
            }
            // FU-A: the NAL header is replaced by an indicator and a per-fragment header.
            let indicator = (nal[0] & 0xE0) | 28;
            let nal_type = nal[0] & 0x1F;
            let chunk = self.max_payload - 2;
            let body = &nal[1..];
            let count = body.len().div_ceil(chunk);
            for (i, part) in body.chunks(chunk).enumerate() {
                let mut header = nal_type;
                if i == 0 {
                    header |= 0x80; // start
                } else if i == count - 1 {
                    header |= 0x40; // end
                }
                let mut packet = Vec::with_capacity(part.len() + 2);
                packet.push(indicator);
                packet.push(header);
                packet.extend_from_slice(part);
                out.push(packet);
            }
        }
        out
    }

    fn packetize_vp8(&mut self, frame: &[u8]) -> Vec<Vec<u8>> {
        self.vp8_picture_id = (self.vp8_picture_id + 1) & 0x7FFF;
        let picture_id = self.vp8_picture_id;
        let chunk = self.max_payload - 4;
        frame
            .chunks(chunk.max(1))
            .enumerate()
            .map(|(i, part)| {
                // Descriptor: X=1 (extended), S on the first packet, then the 15-bit picture id.
                let mut packet = Vec::with_capacity(part.len() + 4);
                packet.push(0x80 | if i == 0 { 0x10 } else { 0 });
                packet.push(0x80); // I: picture id present
                packet.push(0x80 | (picture_id >> 8) as u8);
                packet.push(picture_id as u8);
                packet.extend_from_slice(part);
                packet
            })
            .collect()
    }
}

/// Reassembles RTP payloads into frames.
pub struct VideoDepacketizer {
    format: VideoFormat,
    buffer: Vec<u8>,
    timestamp: u32,
    expected_sequence: Option<u16>,
    started: bool,
    damaged: bool,
    keyframe: bool,
    /// True when the last frame was delivered whole, so a gap after it belongs to the next frame.
    ended_cleanly: bool,
    /// Frames dropped because packets were missing; a caller may use this to ask for a keyframe.
    pub incomplete_frames: u64,
}

impl VideoDepacketizer {
    pub fn new(format: VideoFormat) -> Self {
        Self {
            format,
            buffer: Vec::with_capacity(64 * 1024),
            timestamp: 0,
            expected_sequence: None,
            started: false,
            damaged: false,
            keyframe: false,
            ended_cleanly: false,
            incomplete_frames: 0,
        }
    }

    /// Feeds one RTP packet. Returns a frame once the last packet of a complete frame arrives.
    pub fn push(&mut self, sequence: u16, timestamp: u32, marker: bool, payload: &[u8]) -> Option<VideoFrame> {
        if payload.is_empty() {
            return None;
        }
        let gap = self.expected_sequence.is_some_and(|expected| expected != sequence);
        self.expected_sequence = Some(sequence.wrapping_add(1));

        if self.started && timestamp != self.timestamp {
            // A new frame started before the previous one was finished.
            self.incomplete_frames += 1;
            self.discard();
        }
        if !self.started {
            self.started = true;
            self.timestamp = timestamp;
            self.keyframe = false;
            // A gap before a frame usually means the previous frame lost packets. But when that one
            // arrived whole, the missing packets are this frame's own head — and in H.264 a head that
            // is gone cannot be seen in the payload, because any whole NAL unit looks like the start
            // of a frame. Losing it would hand the decoder an access unit without its parameter sets.
            // VP8 says so in the payload itself, so it needs no help from the sequence number.
            let head_lost = gap && self.ended_cleanly && self.format == VideoFormat::H264;
            self.damaged = !self.starts_a_frame(payload) || head_lost;
        } else if gap {
            self.damaged = true;
        }

        match self.format {
            VideoFormat::H264 => self.push_h264(payload),
            VideoFormat::Vp8 => self.push_vp8(payload),
        }

        if !marker {
            return None;
        }
        let frame = (!self.damaged && !self.buffer.is_empty()).then(|| VideoFrame {
            data: self.buffer.clone(),
            timestamp: self.timestamp,
            keyframe: self.keyframe,
        });
        if frame.is_none() {
            self.incomplete_frames += 1;
        }

        let complete = frame.is_some();
        self.discard();
        self.ended_cleanly = complete;
        frame
    }

    /// True when this payload is the first of a frame (an FU-A start, a whole NAL, or a VP8 S bit).
    fn starts_a_frame(&self, payload: &[u8]) -> bool {
        match self.format {
            VideoFormat::H264 if payload[0] & 0x1F == 28 => payload.len() > 1 && payload[1] & 0x80 != 0,
            VideoFormat::H264 => true,
            VideoFormat::Vp8 => payload[0] & 0x10 != 0,
        }
    }

    fn discard(&mut self) {
        self.buffer.clear();
        self.started = false;
        self.damaged = false;
        self.keyframe = false;
        self.ended_cleanly = false;
    }

    fn push_h264(&mut self, payload: &[u8]) {
        const START_CODE: [u8; 4] = [0, 0, 0, 1];
        match payload[0] & 0x1F {
            // STAP-A: several whole NAL units in one packet.
            24 => {
                let mut at = 1;
                while at + 2 <= payload.len() {
                    let size = u16::from_be_bytes([payload[at], payload[at + 1]]) as usize;
                    at += 2;
                    let Some(nal) = payload.get(at..at + size) else { break };
                    self.note_h264_type(nal[0]);
                    self.buffer.extend_from_slice(&START_CODE);
                    self.buffer.extend_from_slice(nal);
                    at += size;
                }
            }
            // FU-A fragment.
            28 if payload.len() > 2 => {
                let header = payload[1];
                if header & 0x80 != 0 {
                    let nal_header = (payload[0] & 0xE0) | (header & 0x1F);
                    self.note_h264_type(nal_header);
                    self.buffer.extend_from_slice(&START_CODE);
                    self.buffer.push(nal_header);
                }
                self.buffer.extend_from_slice(&payload[2..]);
            }
            _ => {
                self.note_h264_type(payload[0]);
                self.buffer.extend_from_slice(&START_CODE);
                self.buffer.extend_from_slice(payload);
            }
        }
    }

    fn note_h264_type(&mut self, nal_header: u8) {
        // 5 is an IDR picture, 7 and 8 are the parameter sets that come with it.
        self.keyframe |= matches!(nal_header & 0x1F, 5 | 7 | 8);
    }

    fn push_vp8(&mut self, payload: &[u8]) {
        let mut at = 1;
        if payload[0] & 0x80 != 0 {
            // Extended descriptor: skip the optional picture id, TL0PICIDX and TID/KEYIDX bytes.
            let Some(&extension) = payload.get(at) else { return };
            at += 1;
            if extension & 0x80 != 0 {
                let long = payload.get(at).is_some_and(|b| b & 0x80 != 0);
                at += if long { 2 } else { 1 };
            }
            if extension & 0x40 != 0 {
                at += 1;
            }
            if extension & 0x30 != 0 {
                at += 1;
            }
        }
        let Some(body) = payload.get(at..) else { return };
        if self.buffer.is_empty() && !body.is_empty() {
            // The first byte of a VP8 frame says whether it is a keyframe.
            self.keyframe = body[0] & 1 == 0;
        }
        self.buffer.extend_from_slice(body);
    }
}

/// Splits an Annex B byte stream into NAL units, without their start codes.
fn annex_b_units(data: &[u8]) -> Vec<&[u8]> {
    let mut starts = Vec::new();
    let mut i = 0;
    while i + 3 <= data.len() {
        if data[i] == 0 && data[i + 1] == 0 {
            if data[i + 2] == 1 {
                starts.push((i, i + 3));
                i += 3;
                continue;
            }
            if i + 4 <= data.len() && data[i + 2] == 0 && data[i + 3] == 1 {
                starts.push((i, i + 4));
                i += 4;
                continue;
            }
        }
        i += 1;
    }
    if starts.is_empty() {
        // Not Annex B: treat the buffer as a single NAL unit.
        return vec![data];
    }
    starts
        .iter()
        .enumerate()
        .map(|(n, (_, body))| {
            let end = starts.get(n + 1).map_or(data.len(), |(next, _)| *next);
            &data[*body..end]
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn nal(kind: u8, len: usize) -> Vec<u8> {
        let mut unit = vec![0, 0, 0, 1, 0x60 | kind];
        unit.extend((0..len).map(|i| (i % 251) as u8 | 1));
        unit
    }

    fn deliver(packets: &[Vec<u8>], depacketizer: &mut VideoDepacketizer, timestamp: u32, first_sequence: u16) -> Option<VideoFrame> {
        let last = packets.len() - 1;
        let mut frame = None;
        for (i, packet) in packets.iter().enumerate() {
            let out = depacketizer.push(first_sequence.wrapping_add(i as u16), timestamp, i == last, packet);
            frame = frame.or(out);
        }
        frame
    }

    #[test]
    fn h264_small_frame_travels_in_one_packet() {
        let access_unit = nal(1, 100);
        let packets = VideoPacketizer::new(VideoFormat::H264).packetize(&access_unit);
        assert_eq!(packets.len(), 1);
        let mut depacketizer = VideoDepacketizer::new(VideoFormat::H264);
        let frame = deliver(&packets, &mut depacketizer, 9000, 1).expect("frame");
        assert_eq!(frame.data, access_unit);
        assert!(!frame.keyframe);
    }

    #[test]
    fn a_frame_that_lost_its_first_packet_is_dropped_rather_than_truncated() {
        // Parameter sets, then a picture: if the packet carrying them goes missing, what is left
        // still looks like the start of a frame, so only the sequence number gives it away.
        let mut access_unit = nal(7, 10);
        access_unit.extend(nal(5, 2000));
        let packets = VideoPacketizer::new(VideoFormat::H264).with_max_payload(500).packetize(&access_unit);
        let mut depacketizer = VideoDepacketizer::new(VideoFormat::H264);

        // One whole frame first, so the depacketizer knows the previous one ended on its marker.
        deliver(&packets, &mut depacketizer, 9000, 1).expect("the first frame arrives");
        let before = depacketizer.incomplete_frames;

        let sequence = 1 + packets.len() as u16;
        let last = packets.len() - 1;
        let mut delivered = None;
        for (i, packet) in packets.iter().enumerate().skip(1) {
            let out = depacketizer.push(sequence.wrapping_add(i as u16), 12_000, i == last, packet);
            delivered = delivered.or(out);
        }

        assert!(delivered.is_none(), "half a frame must not reach the decoder");
        assert_eq!(depacketizer.incomplete_frames, before + 1, "and it is counted, so a keyframe is asked for");
    }

    #[test]
    fn h264_keyframe_is_fragmented_and_reassembled() {
        // Parameter sets plus a large IDR picture: several NAL units, one of them fragmented.
        let mut access_unit = nal(7, 10);
        access_unit.extend(nal(8, 6));
        access_unit.extend(nal(5, 4000));
        let packets = VideoPacketizer::new(VideoFormat::H264).with_max_payload(500).packetize(&access_unit);
        assert!(packets.len() > 8, "{} packets", packets.len());
        let mut depacketizer = VideoDepacketizer::new(VideoFormat::H264);
        let frame = deliver(&packets, &mut depacketizer, 90_000, 40).expect("frame");
        assert_eq!(frame.data, access_unit);
        assert!(frame.keyframe);
    }

    #[test]
    fn a_missing_packet_drops_the_frame() {
        let access_unit = nal(5, 4000);
        let packets = VideoPacketizer::new(VideoFormat::H264).with_max_payload(400).packetize(&access_unit);
        let mut depacketizer = VideoDepacketizer::new(VideoFormat::H264);
        let last = packets.len() - 1;
        for (i, packet) in packets.iter().enumerate() {
            if i == 2 {
                continue; // lost in transit
            }
            assert!(depacketizer.push(100 + i as u16, 5000, i == last, packet).is_none());
        }
        assert_eq!(depacketizer.incomplete_frames, 1);

        // The next complete frame still arrives.
        let next = VideoPacketizer::new(VideoFormat::H264).packetize(&nal(1, 50));
        assert!(deliver(&next, &mut depacketizer, 8000, 200).is_some());
    }

    #[test]
    fn vp8_frames_survive_fragmentation() {
        let mut keyframe = vec![0x10, 0x02, 0x00]; // bit 0 clear: keyframe
        keyframe.extend((0..3000).map(|i| (i % 255) as u8));
        let packets = VideoPacketizer::new(VideoFormat::Vp8).with_max_payload(600).packetize(&keyframe);
        assert!(packets.len() >= 5);
        assert_eq!(packets[0][0] & 0x10, 0x10, "first packet starts a partition");
        let mut depacketizer = VideoDepacketizer::new(VideoFormat::Vp8);
        let frame = deliver(&packets, &mut depacketizer, 3000, 7).expect("frame");
        assert_eq!(frame.data, keyframe);
        assert!(frame.keyframe);

        let mut inter = vec![0x11, 0x02, 0x00]; // bit 0 set: depends on earlier frames
        inter.extend((0..100).map(|i| i as u8));
        let packets = VideoPacketizer::new(VideoFormat::Vp8).packetize(&inter);
        let frame = deliver(&packets, &mut depacketizer, 6000, 100).expect("frame");
        assert!(!frame.keyframe);
        assert_eq!(frame.data, inter);
    }

    #[test]
    fn stap_a_packets_from_a_browser_are_unpacked() {
        // Chrome sends parameter sets aggregated in one STAP-A packet.
        let (sps, pps) = (vec![0x67, 1, 2, 3], vec![0x68, 4, 5]);
        let mut packet = vec![0x78]; // STAP-A
        for unit in [&sps, &pps] {
            packet.extend((unit.len() as u16).to_be_bytes());
            packet.extend_from_slice(unit);
        }
        let mut depacketizer = VideoDepacketizer::new(VideoFormat::H264);
        let frame = depacketizer.push(1, 900, true, &packet).expect("frame");
        assert_eq!(frame.data, [&[0, 0, 0, 1][..], &sps, &[0, 0, 0, 1][..], &pps].concat());
        assert!(frame.keyframe);
    }
}
