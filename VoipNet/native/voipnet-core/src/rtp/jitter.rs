//! Adaptive jitter buffer.
//!
//! Packets are ordered by sequence number and played out once per packetization
//! interval. The target depth follows the RFC 3550 inter-arrival jitter estimate
//! (roughly 3× jitter + one frame), bounded by `min_depth`/`max_depth`. When the
//! buffer grows beyond target it drops frames to reduce latency; on underrun it
//! re-buffers.

use std::collections::VecDeque;
use std::time::Instant;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Playout {
    /// Next in-order frame.
    Frame { sequence: u16, timestamp: u32, marker: bool, payload_type: u8, payload: Vec<u8> },
    /// Expected frame is missing — conceal it.
    Lost,
    /// Still buffering or empty — play silence/comfort noise.
    Empty,
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct JitterStats {
    pub received: u64,
    pub lost: u64,
    pub late: u64,
    pub duplicates: u64,
    pub dropped_for_latency: u64,
    pub jitter_ms: f64,
    pub depth: usize,
    pub target_depth: usize,
}

struct Slot {
    ext_seq: i64,
    timestamp: u32,
    marker: bool,
    payload_type: u8,
    payload: Vec<u8>,
}

pub struct JitterBuffer {
    queue: VecDeque<Slot>,
    frame_ms: u32,
    clock_rate: u32,
    min_depth: usize,
    max_depth: usize,
    target: usize,
    next_seq: Option<i64>,
    highest_ext: Option<i64>,
    playing: bool,
    excess_frames: u32,
    jitter: f64,
    last_transit: Option<f64>,
    epoch: Instant,
    stats: JitterStats,
    pool: Vec<Vec<u8>>,
}

impl JitterBuffer {
    pub fn new(frame_ms: u32, clock_rate: u32, min_depth: usize, max_depth: usize) -> Self {
        let min_depth = min_depth.max(1);
        let max_depth = max_depth.max(min_depth + 1);
        Self {
            queue: VecDeque::with_capacity(max_depth + 4),
            frame_ms: frame_ms.max(1),
            clock_rate: clock_rate.max(1),
            min_depth,
            max_depth,
            target: min_depth,
            next_seq: None,
            highest_ext: None,
            playing: false,
            excess_frames: 0,
            jitter: 0.0,
            last_transit: None,
            epoch: Instant::now(),
            stats: JitterStats::default(),
            pool: Vec::new(),
        }
    }

    fn extend_seq(&self, seq: u16) -> i64 {
        match self.highest_ext {
            None => seq as i64,
            Some(h) => {
                let delta = seq.wrapping_sub(h as u16) as i16 as i64;
                h + delta
            }
        }
    }

    pub fn push(&mut self, sequence: u16, timestamp: u32, marker: bool, payload_type: u8, payload: &[u8]) {
        self.push_at(sequence, timestamp, marker, payload_type, payload, self.epoch.elapsed().as_secs_f64())
    }

    /// Push with an explicit arrival time in seconds (testable).
    pub fn push_at(&mut self, sequence: u16, timestamp: u32, marker: bool, payload_type: u8, payload: &[u8], arrival: f64) {
        self.stats.received += 1;
        let ext = self.extend_seq(sequence);

        // RFC 3550 A.8 interarrival jitter, in seconds.
        let transit = arrival - timestamp as f64 / self.clock_rate as f64;
        if let Some(prev) = self.last_transit {
            let d = (transit - prev).abs();
            if d < 1.0 {
                self.jitter += (d - self.jitter) / 16.0;
            }
        }
        self.last_transit = Some(transit);
        self.highest_ext = Some(self.highest_ext.map_or(ext, |h| h.max(ext)));

        if let Some(next) = self.next_seq {
            if ext < next {
                self.stats.late += 1;
                return;
            }
        }

        let pos = self.queue.iter().rposition(|s| s.ext_seq <= ext);
        if let Some(p) = pos {
            if self.queue[p].ext_seq == ext {
                self.stats.duplicates += 1;
                return;
            }
        }
        let mut buf = self.pool.pop().unwrap_or_default();
        buf.clear();
        buf.extend_from_slice(payload);
        let slot = Slot { ext_seq: ext, timestamp, marker, payload_type, payload: buf };
        match pos {
            Some(p) => self.queue.insert(p + 1, slot),
            None => self.queue.push_front(slot),
        }

        while self.queue.len() > self.max_depth * 2 {
            if let Some(s) = self.queue.pop_front() {
                self.stats.dropped_for_latency += 1;
                self.next_seq = Some(s.ext_seq + 1);
                self.recycle(s.payload);
            }
        }
        self.update_target();
    }

    fn update_target(&mut self) {
        let jitter_ms = self.jitter * 1000.0;
        let frames = ((jitter_ms * 3.0) / self.frame_ms as f64).ceil() as usize + 1;
        self.target = frames.clamp(self.min_depth, self.max_depth);
    }

    fn recycle(&mut self, buf: Vec<u8>) {
        if self.pool.len() < 32 {
            self.pool.push(buf);
        }
    }

    /// Returns a payload buffer obtained from `Playout::Frame` to the internal pool.
    pub fn give_back(&mut self, buf: Vec<u8>) {
        self.recycle(buf);
    }

    /// Called once per frame interval by the playout clock.
    pub fn pop(&mut self) -> Playout {
        if !self.playing {
            if self.queue.len() < self.target {
                return Playout::Empty;
            }
            self.playing = true;
            if self.next_seq.is_none() {
                self.next_seq = self.queue.front().map(|s| s.ext_seq);
            }
        }

        // Latency control: if we've stayed well above target for a while (~50 frames),
        // skip the oldest frame. Transient bursts are left alone.
        if self.queue.len() > self.target + 2 {
            self.excess_frames += 1;
            if self.excess_frames >= 50 {
                self.excess_frames = 0;
                if let Some(s) = self.queue.pop_front() {
                    self.stats.dropped_for_latency += 1;
                    self.next_seq = Some(s.ext_seq + 1);
                    self.recycle(s.payload);
                }
            }
        } else {
            self.excess_frames = 0;
        }

        let Some(next) = self.next_seq else { return Playout::Empty };
        match self.queue.front() {
            None => {
                // Underrun: go back to buffering.
                self.playing = false;
                Playout::Empty
            }
            Some(front) if front.ext_seq == next => {
                let s = self.queue.pop_front().expect("front exists");
                self.next_seq = Some(next + 1);
                Playout::Frame {
                    sequence: s.ext_seq as u16,
                    timestamp: s.timestamp,
                    marker: s.marker,
                    payload_type: s.payload_type,
                    payload: s.payload,
                }
            }
            Some(_) => {
                self.stats.lost += 1;
                self.next_seq = Some(next + 1);
                Playout::Lost
            }
        }
    }

    pub fn reset(&mut self) {
        while let Some(s) = self.queue.pop_front() {
            self.recycle(s.payload);
        }
        self.next_seq = None;
        self.highest_ext = None;
        self.playing = false;
        self.last_transit = None;
    }

    pub fn stats(&self) -> JitterStats {
        JitterStats { jitter_ms: self.jitter * 1000.0, depth: self.queue.len(), target_depth: self.target, ..self.stats }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame_seq(p: &Playout) -> Option<u16> {
        match p {
            Playout::Frame { sequence, .. } => Some(*sequence),
            _ => None,
        }
    }

    #[test]
    fn reorders_packets() {
        let mut jb = JitterBuffer::new(20, 8000, 3, 10);
        for (i, seq) in [2u16, 0, 1, 3].iter().enumerate() {
            jb.push_at(*seq, *seq as u32 * 160, false, 0, &[*seq as u8], i as f64 * 0.02);
        }
        let got: Vec<_> = (0..4).map(|_| frame_seq(&jb.pop())).collect();
        assert_eq!(got, vec![Some(0), Some(1), Some(2), Some(3)]);
    }

    #[test]
    fn reports_loss_and_handles_wraparound() {
        let mut jb = JitterBuffer::new(20, 8000, 1, 10);
        for (i, seq) in [65534u16, 65535, 1, 2].iter().enumerate() {
            jb.push_at(*seq, i as u32 * 160, false, 0, &[0], i as f64 * 0.02);
        }
        assert_eq!(frame_seq(&jb.pop()), Some(65534));
        assert_eq!(frame_seq(&jb.pop()), Some(65535));
        assert_eq!(jb.pop(), Playout::Lost); // seq 0 missing
        assert_eq!(frame_seq(&jb.pop()), Some(1));
        assert_eq!(jb.stats().lost, 1);
    }

    #[test]
    fn late_and_duplicate_packets_are_dropped() {
        let mut jb = JitterBuffer::new(20, 8000, 1, 10);
        jb.push_at(10, 0, false, 0, &[0], 0.0);
        jb.push_at(10, 0, false, 0, &[0], 0.0);
        assert_eq!(frame_seq(&jb.pop()), Some(10));
        jb.push_at(9, 0, false, 0, &[0], 0.01);
        let s = jb.stats();
        assert_eq!((s.duplicates, s.late), (1, 1));
    }

    #[test]
    fn target_depth_grows_with_jitter() {
        let mut jb = JitterBuffer::new(20, 8000, 2, 15);
        let mut arrival = 0.0;
        for seq in 0..200u16 {
            arrival += if seq % 2 == 0 { 0.005 } else { 0.035 };
            jb.push_at(seq, seq as u32 * 160, false, 0, &[0], arrival);
            let _ = jb.pop();
        }
        assert!(jb.stats().target_depth > 2, "{:?}", jb.stats());
    }
}
