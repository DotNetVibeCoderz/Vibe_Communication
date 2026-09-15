//! Adaptive jitter buffer.
//!
//! Packets arrive from the network with sequence numbers expressed in 10 ms frame units.
//! The buffer reorders them, waits until enough audio is buffered to absorb the measured
//! network jitter (RFC 3550 inter-arrival estimator), and then plays out one packet at a time.
//! Gaps are reported as [`Playout::Lost`] so the decoder can run packet loss concealment.
//!
//! The structure is single-threaded by design: it lives on the audio thread and is fed through
//! a lock-free SPSC queue (see [`crate::mixer`]).

use std::collections::VecDeque;

use bytes::Bytes;

/// Frame duration in microseconds.
const FRAME_US: i64 = 10_000;

/// A packet waiting for playout.
#[derive(Debug, Clone, PartialEq)]
pub struct BufferedPacket {
    pub sequence: u64,
    /// Number of 10 ms frames contained in the payload.
    pub frames: u64,
    pub payload: Bytes,
    pub position: Option<[f32; 3]>,
    pub is_terminator: bool,
    pub volume_adjustment: f32,
}

/// Result of a playout request.
#[derive(Debug, Clone, PartialEq)]
pub enum Playout {
    /// Next packet in sequence.
    Packet(BufferedPacket),
    /// Expected packet is missing: conceal one frame.
    Lost,
    /// Still buffering (or idle): output silence.
    Waiting,
    /// The stream ended (terminator played or long silence).
    Ended,
}

/// Tuning parameters.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct JitterConfig {
    /// Minimum playout delay in frames.
    pub min_delay_frames: u64,
    /// Maximum playout delay in frames.
    pub max_delay_frames: u64,
    /// Maximum consecutive concealed frames before the stream is considered ended.
    pub max_concealed_frames: u32,
    /// Maximum packets kept in the buffer.
    pub capacity: usize,
}

impl Default for JitterConfig {
    fn default() -> Self {
        Self { min_delay_frames: 2, max_delay_frames: 20, max_concealed_frames: 15, capacity: 64 }
    }
}

/// Jitter buffer statistics.
#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct JitterStats {
    pub received: u64,
    pub late: u64,
    pub lost: u64,
    pub duplicates: u64,
    pub overflow_drops: u64,
    /// Current jitter estimate in milliseconds.
    pub jitter_ms: f32,
    /// Current target delay in frames.
    pub target_delay_frames: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    Idle,
    Buffering,
    Playing,
}

/// Adaptive jitter buffer. See module documentation.
#[derive(Debug)]
pub struct JitterBuffer {
    config: JitterConfig,
    /// Sorted by sequence (ascending). Small, so a VecDeque with ordered insert beats a tree.
    packets: VecDeque<BufferedPacket>,
    state: State,
    next_sequence: u64,
    concealed_run: u32,
    last_arrival: Option<(i64, u64)>,
    jitter_us: f64,
    stats: JitterStats,
}

impl JitterBuffer {
    pub fn new(config: JitterConfig) -> Self {
        Self {
            packets: VecDeque::with_capacity(config.capacity),
            config,
            state: State::Idle,
            next_sequence: 0,
            concealed_run: 0,
            last_arrival: None,
            jitter_us: 0.0,
            stats: JitterStats { target_delay_frames: config.min_delay_frames, ..Default::default() },
        }
    }

    pub fn stats(&self) -> JitterStats {
        self.stats
    }

    /// Buffered audio in frames.
    pub fn buffered_frames(&self) -> u64 {
        match (self.packets.front(), self.packets.back()) {
            (Some(first), Some(last)) => {
                let start = if self.state == State::Playing { self.next_sequence.min(first.sequence) } else { first.sequence };
                (last.sequence + last.frames).saturating_sub(start)
            }
            _ => 0,
        }
    }

    pub fn is_active(&self) -> bool {
        self.state != State::Idle
    }

    fn target_delay(&self) -> u64 {
        // Three standard deviations of jitter, expressed in frames, plus one frame of slack.
        let frames = (3.0 * self.jitter_us / FRAME_US as f64).ceil() as u64 + 1;
        frames.clamp(self.config.min_delay_frames, self.config.max_delay_frames)
    }

    /// Inserts a packet that arrived at `arrival_us` (monotonic microseconds).
    pub fn insert(&mut self, packet: BufferedPacket, arrival_us: i64) {
        self.stats.received += 1;

        // RFC 3550 inter-arrival jitter estimate.
        if let Some((last_t, last_seq)) = self.last_arrival {
            let transit_delta = (arrival_us - last_t) - (packet.sequence as i64 - last_seq as i64) * FRAME_US;
            self.jitter_us += (transit_delta.unsigned_abs() as f64 - self.jitter_us) / 16.0;
            self.stats.jitter_ms = (self.jitter_us / 1000.0) as f32;
        }
        if self.last_arrival.is_none_or(|(_, s)| packet.sequence >= s) {
            self.last_arrival = Some((arrival_us, packet.sequence));
        }

        if self.state == State::Playing && packet.sequence < self.next_sequence {
            self.stats.late += 1;
            return;
        }

        // Ordered insert (common case: append at the back).
        let idx = self.packets.iter().rposition(|p| p.sequence <= packet.sequence).map_or(0, |i| i + 1);
        if idx > 0 && self.packets[idx - 1].sequence == packet.sequence {
            self.stats.duplicates += 1;
            return;
        }
        self.packets.insert(idx, packet);

        while self.packets.len() > self.config.capacity {
            self.packets.pop_front();
            self.stats.overflow_drops += 1;
        }

        if self.state == State::Idle {
            self.state = State::Buffering;
        }
    }

    /// Requests the next unit of playout (called every time the decoder needs more audio).
    pub fn pop(&mut self) -> Playout {
        match self.state {
            State::Idle => Playout::Waiting,
            State::Buffering => {
                let target = self.target_delay();
                self.stats.target_delay_frames = target;
                let has_terminator = self.packets.back().is_some_and(|p| p.is_terminator);
                if self.buffered_frames() >= target || (has_terminator && !self.packets.is_empty()) {
                    self.state = State::Playing;
                    self.next_sequence = self.packets.front().map_or(0, |p| p.sequence);
                    self.concealed_run = 0;
                    self.pop()
                } else {
                    Playout::Waiting
                }
            }
            State::Playing => {
                // Catch up if the buffer grew far beyond the target (e.g. after a network stall).
                let target = self.target_delay();
                while self.buffered_frames() > target + self.config.max_delay_frames && self.packets.len() > 1 {
                    let dropped = self.packets.pop_front().unwrap();
                    self.stats.overflow_drops += 1;
                    self.next_sequence = dropped.sequence + dropped.frames;
                }

                match self.packets.front() {
                    Some(p) if p.sequence <= self.next_sequence => {
                        let p = self.packets.pop_front().unwrap();
                        self.next_sequence = p.sequence + p.frames;
                        self.concealed_run = 0;
                        if p.is_terminator && self.packets.is_empty() {
                            self.reset_to_idle();
                        }
                        Playout::Packet(p)
                    }
                    Some(p) => {
                        // Gap before the next available packet.
                        self.concealed_run += 1;
                        self.stats.lost += 1;
                        self.next_sequence += 1;
                        if self.concealed_run > self.config.max_concealed_frames {
                            self.next_sequence = p.sequence;
                            self.concealed_run = 0;
                        }
                        Playout::Lost
                    }
                    None => {
                        self.concealed_run += 1;
                        if self.concealed_run > self.config.max_concealed_frames {
                            self.reset_to_idle();
                            Playout::Ended
                        } else {
                            self.next_sequence += 1;
                            Playout::Lost
                        }
                    }
                }
            }
        }
    }

    fn reset_to_idle(&mut self) {
        self.state = State::Idle;
        self.concealed_run = 0;
        self.last_arrival = None;
    }

    /// Clears all buffered audio.
    pub fn clear(&mut self) {
        self.packets.clear();
        self.reset_to_idle();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pkt(seq: u64, term: bool) -> BufferedPacket {
        BufferedPacket {
            sequence: seq,
            frames: 1,
            payload: Bytes::from(vec![seq as u8]),
            position: None,
            is_terminator: term,
            volume_adjustment: 0.0,
        }
    }

    fn drain(jb: &mut JitterBuffer, n: usize) -> Vec<Playout> {
        (0..n).map(|_| jb.pop()).collect()
    }

    #[test]
    fn reorders_and_plays_in_sequence() {
        let mut jb = JitterBuffer::new(JitterConfig::default());
        for (i, seq) in [100u64, 102, 101, 103].into_iter().enumerate() {
            jb.insert(pkt(seq, false), i as i64 * FRAME_US);
        }
        let seqs: Vec<u64> = drain(&mut jb, 4)
            .into_iter()
            .filter_map(|p| if let Playout::Packet(p) = p { Some(p.sequence) } else { None })
            .collect();
        assert_eq!(seqs, vec![100, 101, 102, 103]);
    }

    #[test]
    fn conceals_gaps() {
        let mut jb = JitterBuffer::new(JitterConfig::default());
        for (i, seq) in [1u64, 2, 4, 5].into_iter().enumerate() {
            jb.insert(pkt(seq, false), i as i64 * FRAME_US);
        }
        let out = drain(&mut jb, 5);
        assert!(matches!(out[0], Playout::Packet(ref p) if p.sequence == 1));
        assert!(matches!(out[1], Playout::Packet(ref p) if p.sequence == 2));
        assert_eq!(out[2], Playout::Lost);
        assert!(matches!(out[3], Playout::Packet(ref p) if p.sequence == 4));
        assert_eq!(jb.stats().lost, 1);
    }

    #[test]
    fn drops_late_and_duplicate() {
        let mut jb = JitterBuffer::new(JitterConfig { min_delay_frames: 1, ..Default::default() });
        jb.insert(pkt(10, false), 0);
        jb.insert(pkt(11, false), FRAME_US);
        jb.insert(pkt(11, false), FRAME_US);
        assert_eq!(jb.stats().duplicates, 1);
        let _ = drain(&mut jb, 2);
        jb.insert(pkt(10, false), 3 * FRAME_US);
        assert_eq!(jb.stats().late, 1);
    }

    #[test]
    fn terminator_ends_stream() {
        let mut jb = JitterBuffer::new(JitterConfig::default());
        jb.insert(pkt(1, false), 0);
        jb.insert(pkt(2, true), FRAME_US);
        let out = drain(&mut jb, 3);
        assert!(matches!(out[1], Playout::Packet(ref p) if p.is_terminator));
        assert_eq!(out[2], Playout::Waiting);
        assert!(!jb.is_active());
    }

    #[test]
    fn adapts_delay_to_jitter() {
        let mut jb = JitterBuffer::new(JitterConfig::default());
        // Packets arrive in bursts of 5 every 50 ms → high jitter.
        for i in 0..200u64 {
            let arrival = ((i / 5) * 5) as i64 * FRAME_US;
            jb.insert(pkt(i, false), arrival);
            if jb.buffered_frames() > 30 {
                let _ = jb.pop();
            }
        }
        assert!(jb.stats().jitter_ms > 5.0, "jitter {}", jb.stats().jitter_ms);
        assert!(jb.target_delay() > JitterConfig::default().min_delay_frames);
    }

    #[test]
    fn long_silence_ends_stream() {
        let mut jb = JitterBuffer::new(JitterConfig::default());
        jb.insert(pkt(1, false), 0);
        jb.insert(pkt(2, false), FRAME_US);
        let out = drain(&mut jb, 40);
        assert!(out.contains(&Playout::Ended));
    }
}
