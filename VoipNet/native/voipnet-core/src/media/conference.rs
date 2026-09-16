//! Conference bridge with per-participant mix-minus at a fixed internal rate.

use std::collections::HashMap;

use parking_lot::Mutex;

pub const CONFERENCE_RATE: u32 = 16000;

struct Participant {
    frame: Vec<i16>,
    generation: u64,
}

#[derive(Default)]
pub struct Conference {
    participants: Mutex<HashMap<u64, Participant>>,
}

impl Conference {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn join(&self, id: u64) {
        self.participants.lock().entry(id).or_insert(Participant { frame: Vec::new(), generation: 0 });
    }

    pub fn leave(&self, id: u64) {
        self.participants.lock().remove(&id);
    }

    pub fn len(&self) -> usize {
        self.participants.lock().len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    /// Stores the latest frame (at `CONFERENCE_RATE`) heard from participant `id`.
    pub fn contribute(&self, id: u64, frame: &[i16]) {
        if let Some(p) = self.participants.lock().get_mut(&id) {
            p.frame.clear();
            p.frame.extend_from_slice(frame);
            p.generation += 1;
        }
    }

    /// Mixes every other participant's latest frame into `out` (mix-minus for `id`).
    pub fn mix_for(&self, id: u64, samples: usize, out: &mut Vec<i16>) {
        let parts = self.participants.lock();
        let start = out.len();
        out.resize(start + samples, 0);
        let mut acc = vec![0i32; samples];
        for (pid, p) in parts.iter() {
            if *pid == id || p.generation == 0 {
                continue;
            }
            for (a, s) in acc.iter_mut().zip(p.frame.iter()) {
                *a += *s as i32;
            }
        }
        for (o, a) in out[start..].iter_mut().zip(acc) {
            *o = a.clamp(-32768, 32767) as i16;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mix_minus_excludes_self_and_saturates() {
        let c = Conference::new();
        for id in 1..=3 {
            c.join(id);
        }
        c.contribute(1, &[100; 4]);
        c.contribute(2, &[200; 4]);
        c.contribute(3, &[30000; 4]);
        let mut out = Vec::new();
        c.mix_for(1, 4, &mut out);
        assert_eq!(out, vec![30200; 4].into_iter().map(|v: i32| v.min(32767) as i16).collect::<Vec<_>>());
        out.clear();
        c.mix_for(3, 4, &mut out);
        assert_eq!(out, vec![300; 4]);
    }
}
