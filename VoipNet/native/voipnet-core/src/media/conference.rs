//! Conference bridge: per-participant mix-minus audio at a fixed internal rate, and video forwarded
//! from one participant to the rest.
//!
//! Video is forwarded, never mixed: mixing would mean decoding and re-encoding every stream, which
//! needs codecs this engine does not carry. Each participant therefore sees one other participant at a
//! time — the one who is speaking, or the one who has been pinned — which is what a speaker-focus
//! layout looks like anyway. A grid needs one stream per participant and is not offered.

use std::collections::HashMap;
use std::time::{Duration, Instant};

use parking_lot::Mutex;

pub const CONFERENCE_RATE: u32 = 16000;

/// How long the active speaker keeps the floor after they stop being the loudest, so the picture does
/// not flick between two people talking over each other.
const SPEAKER_HOLD: Duration = Duration::from_millis(1500);
/// How much louder somebody has to be to take the floor from the current speaker.
const SPEAKER_MARGIN: f64 = 1.5;
/// Below this the room counts as quiet, so silence never takes the floor from whoever last spoke.
const SPEAKER_FLOOR: f64 = 300.0;

/// Who each participant sees.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ConferenceLayout {
    /// Everyone sees whoever is speaking.
    #[default]
    SpeakerFocus,
    /// Everyone sees the same participant, whatever the room sounds like.
    Pinned(u64),
}

struct Participant {
    frame: Vec<i16>,
    generation: u64,
    /// Loudness of the last frame, for picking the speaker.
    energy: f64,
    /// When this participant was last above the floor.
    last_loud: Option<Instant>,
}

/// What one participant is currently watching.
struct VideoView {
    source: u64,
    /// True until a keyframe from the new source arrives: a decoder cannot start mid-picture.
    needs_keyframe: bool,
}

#[derive(Default)]
pub struct Conference {
    participants: Mutex<HashMap<u64, Participant>>,
    /// Mixing accumulator, kept between frames so the hot path allocates nothing.
    accumulator: Mutex<Vec<i32>>,
    layout: Mutex<ConferenceLayout>,
    /// Per viewer: whose video they are being sent.
    views: Mutex<HashMap<u64, VideoView>>,
    /// The speaker holding the floor, and since when.
    speaker: Mutex<Option<(u64, Instant)>>,
}

impl Conference {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn join(&self, id: u64) {
        self.participants
            .lock()
            .entry(id)
            .or_insert(Participant { frame: Vec::new(), generation: 0, energy: 0.0, last_loud: None });
    }

    pub fn leave(&self, id: u64) {
        self.participants.lock().remove(&id);
        self.views.lock().remove(&id);
        // Anyone watching them needs a new source next frame.
        self.views.lock().retain(|_, view| view.source != id);
        let mut speaker = self.speaker.lock();
        if speaker.is_some_and(|(current, _)| current == id) {
            *speaker = None;
        }
    }

    /// Sets who everyone sees.
    pub fn set_layout(&self, layout: ConferenceLayout) {
        *self.layout.lock() = layout;
        // The picture changes, so every viewer waits for a keyframe from the new source.
        self.views.lock().clear();
    }

    pub fn layout(&self) -> ConferenceLayout {
        *self.layout.lock()
    }

    /// Who is speaking now, or the last person to speak while the room is quiet.
    pub fn active_speaker(&self) -> Option<u64> {
        let parts = self.participants.lock();
        let loudest = parts
            .iter()
            .filter(|(_, p)| p.energy >= SPEAKER_FLOOR)
            .max_by(|a, b| a.1.energy.total_cmp(&b.1.energy))
            .map(|(id, p)| (*id, p.energy));

        let mut speaker = self.speaker.lock();
        match (*speaker, loudest) {
            (Some((current, since)), Some((id, energy))) if current != id => {
                // Taking the floor needs either a clear margin or a pause from the current speaker.
                let current_energy = parts.get(&current).map_or(0.0, |p| p.energy);
                if energy > current_energy * SPEAKER_MARGIN || since.elapsed() > SPEAKER_HOLD {
                    *speaker = Some((id, Instant::now()));
                }
            }
            (Some((current, _)), _) if parts.contains_key(&current) => {}
            (_, Some((id, _))) => *speaker = Some((id, Instant::now())),
            (_, None) => {}
        }

        speaker.map(|(id, _)| id)
    }

    /// Who should be sent a video frame from `from`, and whether `from` should be asked for a keyframe.
    ///
    /// Only the participant the layout has chosen is forwarded, and a viewer that has just been switched
    /// to a new source is held back until a keyframe arrives, so a decoder never starts mid-picture.
    pub fn video_targets(&self, from: u64, keyframe: bool) -> (Vec<u64>, bool) {
        let chosen = match self.layout() {
            ConferenceLayout::Pinned(id) => Some(id),
            ConferenceLayout::SpeakerFocus => self.active_speaker(),
        };
        if chosen != Some(from) {
            return (Vec::new(), false);
        }

        let viewers: Vec<u64> = self.participants.lock().keys().copied().filter(|id| *id != from).collect();
        let mut views = self.views.lock();
        let mut targets = Vec::with_capacity(viewers.len());
        let mut wants_keyframe = false;
        for viewer in viewers {
            let view = views.entry(viewer).or_insert(VideoView { source: from, needs_keyframe: true });
            if view.source != from {
                view.source = from;
                view.needs_keyframe = true;
            }

            if view.needs_keyframe && !keyframe {
                wants_keyframe = true;
                continue;
            }

            view.needs_keyframe = false;
            targets.push(viewer);
        }

        (targets, wants_keyframe)
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
            // Loudness decides who the room looks at, so it is measured where the audio already is.
            let sum: f64 = frame.iter().map(|s| f64::from(*s) * f64::from(*s)).sum();
            p.energy = if frame.is_empty() { 0.0 } else { (sum / frame.len() as f64).sqrt() };
            if p.energy >= SPEAKER_FLOOR {
                p.last_loud = Some(Instant::now());
            }
        }
    }

    /// Mixes every other participant's latest frame into `out` (mix-minus for `id`).
    pub fn mix_for(&self, id: u64, samples: usize, out: &mut Vec<i16>) {
        let parts = self.participants.lock();
        let start = out.len();
        out.resize(start + samples, 0);
        let mut acc = self.accumulator.lock();
        acc.clear();
        acc.resize(samples, 0);
        for (pid, p) in parts.iter() {
            if *pid == id || p.generation == 0 {
                continue;
            }
            for (a, s) in acc.iter_mut().zip(p.frame.iter()) {
                *a += i32::from(*s);
            }
        }
        for (o, a) in out[start..].iter_mut().zip(acc.iter()) {
            *o = (*a).clamp(-32768, 32767) as i16;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_loudest_participant_takes_the_floor_and_keeps_it_briefly() {
        let c = Conference::new();
        for id in 1..=3 {
            c.join(id);
        }

        c.contribute(1, &[5000; 160]);
        c.contribute(2, &[100; 160]);
        assert_eq!(c.active_speaker(), Some(1));

        // A little louder is not enough to take the floor from someone still talking.
        c.contribute(2, &[5200; 160]);
        assert_eq!(c.active_speaker(), Some(1));

        // Clearly louder is.
        c.contribute(2, &[20000; 160]);
        assert_eq!(c.active_speaker(), Some(2));

        // Silence does not hand the floor to nobody: the last speaker stays on screen.
        c.contribute(1, &[0; 160]);
        c.contribute(2, &[0; 160]);
        c.contribute(3, &[0; 160]);
        assert_eq!(c.active_speaker(), Some(2));
    }

    #[test]
    fn video_is_forwarded_from_the_speaker_once_a_keyframe_arrives() {
        let c = Conference::new();
        for id in 1..=3 {
            c.join(id);
        }

        c.contribute(1, &[9000; 160]);
        c.contribute(2, &[100; 160]);
        c.contribute(3, &[100; 160]);

        // Nobody watches the quiet participants.
        assert_eq!(c.video_targets(2, true), (vec![], false));

        // The speaker's first frame is a delta: the viewers cannot start on it, so a keyframe is asked for.
        let (targets, wants_keyframe) = c.video_targets(1, false);
        assert!(targets.is_empty());
        assert!(wants_keyframe);

        // The keyframe reaches both viewers, and the frames after it follow.
        let (mut targets, wants_keyframe) = c.video_targets(1, true);
        targets.sort_unstable();
        assert_eq!(targets, vec![2, 3]);
        assert!(!wants_keyframe);
        let (targets, _) = c.video_targets(1, false);
        assert_eq!(targets.len(), 2);
    }

    #[test]
    fn pinning_overrides_who_is_talking() {
        let c = Conference::new();
        for id in 1..=2 {
            c.join(id);
        }

        c.contribute(1, &[9000; 160]);
        c.set_layout(ConferenceLayout::Pinned(2));
        assert_eq!(c.layout(), ConferenceLayout::Pinned(2));

        assert_eq!(c.video_targets(1, true), (vec![], false));
        assert_eq!(c.video_targets(2, true), (vec![1], false));
    }

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
