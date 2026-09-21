//! Burst and gap statistics for RTCP XR VoIP metrics (RFC 3611 §4.7.2).
//!
//! Losses are not spread evenly: a call with 5% loss sounds very different when the misses come one
//! at a time than when they arrive in clumps. The RFC splits the stream into *bursts* (dense loss)
//! and *gaps* (clean stretches), separated by at least `G_MIN` good packets in a row.

/// Consecutive good packets that end a burst (RFC 3611 recommends 16).
pub const G_MIN: u32 = 16;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct BurstGapMetrics {
    /// Lost or discarded packets during bursts, as a fraction of 256.
    pub burst_density: u8,
    /// Lost or discarded packets during gaps, as a fraction of 256.
    pub gap_density: u8,
    /// Mean burst length in packets.
    pub burst_packets: u32,
    /// Mean gap length in packets.
    pub gap_packets: u32,
}

/// Classifies each played-out packet as part of a burst or a gap.
#[derive(Debug, Default)]
pub struct BurstGapTracker {
    /// Good packets seen since the last loss; they belong to a burst until they reach `G_MIN`.
    pending_good: u32,
    in_burst: bool,
    burst_packets: u64,
    burst_losses: u64,
    bursts: u64,
    gap_packets: u64,
    gap_losses: u64,
    gaps: u64,
}

impl BurstGapTracker {
    /// Records one packet that should have played: lost (or discarded) or received.
    pub fn packet(&mut self, lost: bool) {
        if lost {
            if !self.in_burst {
                self.in_burst = true;
                self.bursts += 1;
            }
            // Good packets since the last loss were too few to end the burst, so they count as burst.
            self.burst_packets += self.pending_good as u64 + 1;
            self.burst_losses += 1;
            self.pending_good = 0;
            return;
        }

        self.pending_good += 1;
        if self.in_burst && self.pending_good >= G_MIN {
            // The burst ended G_MIN packets ago; this run starts a gap.
            self.in_burst = false;
            self.gaps += 1;
            self.gap_packets += self.pending_good as u64;
            self.pending_good = 0;
        } else if !self.in_burst {
            if self.gaps == 0 {
                self.gaps = 1;
            }
            self.gap_packets += 1;
            self.pending_good = 0;
        }
    }

    /// Counts a packet that arrived but was thrown away (late, or trimmed for latency).
    pub fn discarded(&mut self) {
        self.packet(true);
    }

    pub fn metrics(&self) -> BurstGapMetrics {
        let density = |lost: u64, total: u64| if total == 0 { 0 } else { ((lost * 256 / total).min(255)) as u8 };
        BurstGapMetrics {
            burst_density: density(self.burst_losses, self.burst_packets),
            gap_density: density(self.gap_losses, self.gap_packets),
            burst_packets: (self.burst_packets.checked_div(self.bursts).unwrap_or(0)) as u32,
            gap_packets: (self.gap_packets.checked_div(self.gaps).unwrap_or(0)) as u32,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(pattern: &str) -> BurstGapMetrics {
        let mut tracker = BurstGapTracker::default();
        for c in pattern.chars() {
            tracker.packet(c == 'x');
        }
        tracker.metrics()
    }

    /// A loss followed by `good` received packets, repeated.
    fn losses_every(good: usize, times: usize) -> String {
        let mut s = String::new();
        for _ in 0..times {
            s.push('x');
            s.push_str(&"-".repeat(good));
        }
        s
    }

    #[test]
    fn a_clean_stream_is_all_gap() {
        let m = feed(&"-".repeat(200));
        assert_eq!((m.burst_density, m.gap_density, m.burst_packets), (0, 0, 0));
        assert_eq!(m.gap_packets, 200);
    }

    #[test]
    fn isolated_losses_far_apart_stay_out_of_bursts() {
        // One loss every 40 packets: each burst is a single packet, the rest are gaps.
        let m = feed(&losses_every(40, 5));
        assert!(m.burst_packets <= 2, "burst of {} packets", m.burst_packets);
        assert!(m.burst_density > 100, "density {}", m.burst_density);
        assert!(m.gap_packets > 30, "gap of {} packets", m.gap_packets);
    }

    #[test]
    fn clumped_losses_form_one_long_burst() {
        // Losses separated by fewer than G_MIN good packets belong to the same burst.
        let mut pattern = losses_every(4, 5);
        pattern.push_str(&"-".repeat(50));
        let m = feed(&pattern);
        assert!(m.burst_packets >= 20, "burst of {} packets", m.burst_packets);
        assert!(m.burst_density >= 40, "burst density {}", m.burst_density);
        assert_eq!(m.gap_density, 0);
    }
}
