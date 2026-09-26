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
    /// When they were switched to this source, and how many keyframes have gone since.
    switched: Instant,
    keyframes: u8,
    /// When the source was last asked for a keyframe on this viewer's behalf.
    asked: Option<Instant>,
}

impl VideoView {
    fn new(source: u64, now: Instant) -> Self {
        Self { source, needs_keyframe: true, switched: now, keyframes: 0, asked: None }
    }

    fn switch(&mut self, source: u64, now: Instant) {
        self.source = source;
        self.needs_keyframe = true;
        self.switched = now;
        self.keyframes = 0;
        self.asked = None;
    }

    /// Whether to ask the source for a keyframe on this viewer's behalf, at most now and then.
    ///
    /// A keyframe is a large thing to ask for. Asking several times a second while one is already on
    /// its way is how a room talks itself into a storm: every request is answered with a picture big
    /// enough to lose packets, which loses the answer, which asks again.
    fn should_ask(&mut self, now: Instant) -> bool {
        if self.asked.is_some_and(|at| now.duration_since(at) < ASK_INTERVAL) {
            return false;
        }

        self.asked = Some(now);
        true
    }
}

/// How long a freshly switched viewer is given more than one keyframe, and how many it gets.
///
/// The first one can be lost on the way — a transport that has only just finished its handshake, a
/// packet dropped, a decoder not yet listening — and a viewer that misses it has no way to say so
/// until its own decoder complains, which some browsers are slow to do. A couple more cost almost
/// nothing and turn a minute of black screen into a second of it.
const SETTLING: Duration = Duration::from_secs(4);
const SETTLING_KEYFRAMES: u8 = 3;

/// How long a viewer is held back waiting for a picture it can start on.
///
/// After that the stream goes out anyway. A decoder that cannot start yet is no worse off for
/// receiving frames — it drops them and asks the sender for a keyframe of its own, which the room
/// passes on — and a viewer held back indefinitely is a black screen that nothing recovers from.
const HOLD: Duration = Duration::from_secs(2);

/// The shortest gap between asking a source for a keyframe on one viewer's behalf.
const ASK_INTERVAL: Duration = Duration::from_millis(1500);

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
        let loudest = |floor: f64| {
            parts
                .iter()
                .filter(|(_, p)| p.energy > floor)
                .max_by(|a, b| a.1.energy.total_cmp(&b.1.energy))
                .map(|(id, p)| (*id, p.energy))
        };
        // Above the floor is a person talking. Below it, somebody quiet is still better than an empty
        // screen — a softly spoken participant, or a browser whose echo canceller has turned them
        // down, should still be who the room looks at. Only silence leaves the picture where it was.
        let loudest = loudest(SPEAKER_FLOOR).or_else(|| loudest(0.0));

        let mut speaker = self.speaker.lock();
        match (*speaker, loudest) {
            (Some((current, _)), Some((id, energy))) if current != id => {
                // Taking the floor needs a clear margin, or a pause from whoever holds it. Two people
                // talking at the same level must not pass the picture back and forth: every change
                // costs the viewers a keyframe, and the room would spend its time recovering.
                let current_energy = parts.get(&current).map_or(0.0, |p| p.energy);
                let paused = parts.get(&current).and_then(|p| p.last_loud).is_none_or(|at| at.elapsed() > SPEAKER_HOLD);
                if energy > current_energy * SPEAKER_MARGIN || paused {
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
    pub fn video_targets(&self, from: u64, keyframe: bool, now: Instant) -> (Vec<u64>, bool) {
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
            let view = views.entry(viewer).or_insert_with(|| VideoView::new(from, now));
            if view.source != from {
                view.switch(from, now);
            }

            if view.needs_keyframe && !keyframe {
                let holding = now.duration_since(view.switched) < HOLD;
                wants_keyframe |= view.should_ask(now);
                if holding {
                    continue;
                }

                // Held long enough: send it anyway rather than leave them looking at nothing.
                targets.push(viewer);
                continue;
            }

            if keyframe {
                view.needs_keyframe = false;
                view.keyframes = view.keyframes.saturating_add(1);
                // Still settling: ask for another in case this one does not arrive.
                if view.keyframes < SETTLING_KEYFRAMES && now.duration_since(view.switched) < SETTLING {
                    wants_keyframe |= view.should_ask(now);
                }
            }

            targets.push(viewer);
        }

        (targets, wants_keyframe)
    }

    /// Who a viewer is being shown, and marks them as needing a picture to start on again.
    ///
    /// This is what a viewer's own keyframe request means in a room: the browser cannot decode what it
    /// is being sent, so the participant it is watching has to be asked, and nothing more is forwarded
    /// until that arrives — a delta frame a decoder cannot use is worse than nothing.
    pub fn source_for_restart(&self, viewer: u64) -> Option<u64> {
        let mut views = self.views.lock();
        let view = views.get_mut(&viewer)?;
        view.needs_keyframe = true;
        view.keyframes = 0;
        view.switched = Instant::now();
        view.asked = None;
        Some(view.source)
    }

    /// Everybody in the room, by call.
    pub fn participants(&self) -> Vec<u64> {
        self.participants.lock().keys().copied().collect()
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
    fn a_quiet_room_still_shows_somebody() {
        // Below the floor nobody counts as "talking", but an empty screen helps no one: the loudest
        // of the quiet participants is shown until somebody speaks up.
        let c = Conference::new();
        c.join(1);
        c.join(2);

        c.contribute(1, &[40; 160]);
        c.contribute(2, &[10; 160]);
        assert_eq!(c.active_speaker(), Some(1));

        // Somebody actually talking takes it from them.
        c.contribute(2, &[9000; 160]);
        assert_eq!(c.active_speaker(), Some(2));
    }

    #[test]
    fn an_equally_loud_participant_does_not_take_the_floor() {
        let c = Conference::new();
        c.join(1);
        c.join(2);

        c.contribute(1, &[6000; 160]);
        assert_eq!(c.active_speaker(), Some(1));

        // Two people talking at the same level: the floor stays where it is, because every change
        // costs the viewers a keyframe.
        for _ in 0..5 {
            c.contribute(1, &[6000; 160]);
            c.contribute(2, &[6000; 160]);
            assert_eq!(c.active_speaker(), Some(1));
        }
    }

    #[test]
    fn a_freshly_switched_viewer_is_offered_more_than_one_keyframe() {
        let mut now = Instant::now();
        // The first keyframe after a switch can be lost — a transport that has only just finished
        // its handshake, a dropped packet — and the viewer has no way to say so until its own
        // decoder complains. For a few seconds the room keeps asking.
        let c = Conference::new();
        for id in [1, 2] {
            c.join(id);
        }

        c.set_layout(ConferenceLayout::Pinned(1));

        // The first frame is a delta: held back, and the source is asked.
        let (targets, wants) = c.video_targets(1, false, now);
        assert!(targets.is_empty() && wants, "the viewer waits for a picture to start on");

        // The keyframe arrives and is forwarded. Another is asked for while the viewer settles,
        // though not before the room has waited a moment for the last one to arrive.
        now += Duration::from_millis(100);
        let (targets, wants) = c.video_targets(1, true, now);
        assert_eq!(targets, vec![2]);
        assert!(!wants, "the request a moment ago is still on its way");

        now += Duration::from_secs(2);
        let (targets, wants) = c.video_targets(1, true, now);
        assert_eq!(targets, vec![2]);
        assert!(wants, "a second keyframe is asked for while the viewer is settling");

        // Once it has had its few, the room lets the stream be.
        now += Duration::from_secs(2);
        let (_, wants) = c.video_targets(1, true, now);
        assert!(!wants, "three keyframes are enough to start on");
        now += Duration::from_secs(2);
        let (targets, wants) = c.video_targets(1, false, now);
        assert_eq!(targets, vec![2]);
        assert!(!wants);
    }

    #[test]
    fn a_viewer_asking_for_a_keyframe_names_who_it_is_watching() {
        let mut now = Instant::now();
        let c = Conference::new();
        for id in [1, 2, 3] {
            c.join(id);
        }

        c.set_layout(ConferenceLayout::Pinned(1));
        let (targets, _) = c.video_targets(1, true, now);
        assert_eq!(targets.len(), 2, "both viewers are being sent the pinned participant");

        // A browser that cannot decode what it is being sent asks for a keyframe. The room turns that
        // into a request to whoever it is watching, and holds the stream until the keyframe arrives.
        assert_eq!(c.source_for_restart(2), Some(1));
        let (targets, wants_keyframe) = c.video_targets(1, false, now);
        assert!(wants_keyframe, "the source is asked");
        assert_eq!(targets, vec![3], "the viewer that asked is held back, the other carries on");

        let (targets, _) = c.video_targets(1, true, now);
        assert_eq!(targets.len(), 2, "the keyframe goes to both, and the stream is whole again");

        assert_eq!(c.source_for_restart(9), None, "somebody who is watching nothing names nobody");
    }

    #[test]
    fn video_is_forwarded_from_the_speaker_once_a_keyframe_arrives() {
        let mut now = Instant::now();
        let c = Conference::new();
        for id in 1..=3 {
            c.join(id);
        }

        c.contribute(1, &[9000; 160]);
        c.contribute(2, &[100; 160]);
        c.contribute(3, &[100; 160]);

        // Nobody watches the quiet participants.
        assert_eq!(c.video_targets(2, true, now), (vec![], false));

        // The speaker's first frame is a delta: the viewers cannot start on it, so a keyframe is asked for.
        let (targets, wants_keyframe) = c.video_targets(1, false, now);
        assert!(targets.is_empty());
        assert!(wants_keyframe);

        // The keyframe reaches both viewers, and the frames after it follow. Another keyframe is
        // asked for while they settle, in case this one did not arrive at one of them.
        now += Duration::from_secs(2);
        let (mut targets, wants_keyframe) = c.video_targets(1, true, now);
        targets.sort_unstable();
        assert_eq!(targets, vec![2, 3]);
        assert!(wants_keyframe);
        let (targets, _) = c.video_targets(1, false, now);
        assert_eq!(targets.len(), 2);
    }

    #[test]
    fn pinning_overrides_who_is_talking() {
        let now = Instant::now();
        let c = Conference::new();
        for id in 1..=2 {
            c.join(id);
        }

        c.contribute(1, &[9000; 160]);
        c.set_layout(ConferenceLayout::Pinned(2));
        assert_eq!(c.layout(), ConferenceLayout::Pinned(2));

        assert_eq!(c.video_targets(1, true, now), (vec![], false), "nobody is watching the one who is talking");
        // The viewer is sent the pinned participant, and asked after again while they settle.
        assert_eq!(c.video_targets(2, true, now), (vec![1], true));
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
