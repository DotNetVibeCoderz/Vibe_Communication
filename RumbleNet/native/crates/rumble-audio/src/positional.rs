//! 3D positional audio.
//!
//! Coordinates follow Mumble's convention: meters, left-handed, +X right, +Y up, +Z forward.
//! Gains for many speakers are computed in a single tight loop over contiguous slices, which the
//! compiler auto-vectorizes (SSE/NEON), so hundreds of speakers cost microseconds per frame.

/// Listener (local user) pose.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Listener {
    pub position: [f32; 3],
    pub forward: [f32; 3],
    pub up: [f32; 3],
}

impl Default for Listener {
    fn default() -> Self {
        Self { position: [0.0; 3], forward: [0.0, 0.0, 1.0], up: [0.0, 1.0, 0.0] }
    }
}

/// Distance model parameters.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PositionalSettings {
    /// Distance (m) under which volume is not attenuated.
    pub min_distance: f32,
    /// Distance (m) at which the minimum volume is reached.
    pub max_distance: f32,
    /// Volume at or beyond `max_distance` (0..=1).
    pub min_volume: f32,
    /// Extra attenuation for sources behind the listener (0 = none, 1 = full).
    pub rear_attenuation: f32,
}

impl Default for PositionalSettings {
    fn default() -> Self {
        Self { min_distance: 1.0, max_distance: 15.0, min_volume: 0.1, rear_attenuation: 0.25 }
    }
}

/// Per-channel gains for a stereo output.
#[derive(Debug, Clone, Copy, PartialEq, Default)]
pub struct StereoGain {
    pub left: f32,
    pub right: f32,
}

impl StereoGain {
    pub const CENTER: StereoGain = StereoGain { left: 1.0, right: 1.0 };
}

#[inline(always)]
fn cross(a: [f32; 3], b: [f32; 3]) -> [f32; 3] {
    [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]
}

#[inline(always)]
fn normalize(v: [f32; 3]) -> [f32; 3] {
    let len = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]).sqrt();
    if len > f32::EPSILON { [v[0] / len, v[1] / len, v[2] / len] } else { [0.0; 3] }
}

/// Computes stereo gains for every source position in `positions`, writing into `out`.
pub fn compute_gains(listener: &Listener, settings: &PositionalSettings, positions: &[[f32; 3]], out: &mut [StereoGain]) {
    let forward = normalize(listener.forward);
    // Left-handed coordinates: right = up × forward.
    let right = normalize(cross(listener.up, forward));
    let range = (settings.max_distance - settings.min_distance).max(f32::EPSILON);
    let [lx, ly, lz] = listener.position;

    for (pos, gain) in positions.iter().zip(out.iter_mut()) {
        let dx = pos[0] - lx;
        let dy = pos[1] - ly;
        let dz = pos[2] - lz;
        let dist = (dx * dx + dy * dy + dz * dz).sqrt();
        let inv = if dist > f32::EPSILON { 1.0 / dist } else { 0.0 };

        // Pan in [-1, 1] and facing in [-1, 1].
        let pan = (dx * right[0] + dy * right[1] + dz * right[2]) * inv;
        let facing = (dx * forward[0] + dy * forward[1] + dz * forward[2]) * inv;

        // Linear distance attenuation between min and max distance.
        let t = ((dist - settings.min_distance) / range).clamp(0.0, 1.0);
        let mut volume = 1.0 - t * (1.0 - settings.min_volume);
        // Sources behind the listener are slightly muffled.
        volume *= 1.0 - settings.rear_attenuation * (-facing).max(0.0);

        // Equal-power panning.
        let angle = (pan.clamp(-1.0, 1.0) + 1.0) * std::f32::consts::FRAC_PI_4;
        gain.left = angle.cos() * volume * std::f32::consts::SQRT_2;
        gain.right = angle.sin() * volume * std::f32::consts::SQRT_2;
    }
}

/// Convenience wrapper for a single source.
pub fn gain_for(listener: &Listener, settings: &PositionalSettings, position: [f32; 3]) -> StereoGain {
    let mut out = [StereoGain::default()];
    compute_gains(listener, settings, &[position], &mut out);
    out[0]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn panning_follows_position() {
        let l = Listener::default();
        let s = PositionalSettings::default();
        let right = gain_for(&l, &s, [1.0, 0.0, 0.0]);
        let left = gain_for(&l, &s, [-1.0, 0.0, 0.0]);
        let front = gain_for(&l, &s, [0.0, 0.0, 1.0]);
        assert!(right.right > right.left);
        assert!(left.left > left.right);
        assert!((front.left - front.right).abs() < 1e-4);
        assert!((front.left - 1.0).abs() < 1e-3, "center is unity gain");
    }

    #[test]
    fn attenuates_with_distance() {
        let l = Listener::default();
        let s = PositionalSettings::default();
        let near = gain_for(&l, &s, [0.0, 0.0, 2.0]);
        let far = gain_for(&l, &s, [0.0, 0.0, 30.0]);
        assert!(near.left > far.left);
        assert!((far.left - s.min_volume).abs() < 1e-3);
    }

    #[test]
    fn rotating_listener_changes_side() {
        let s = PositionalSettings::default();
        let l = Listener { forward: [1.0, 0.0, 0.0], ..Default::default() };
        // Facing +X, a source at +Z is on the left.
        let g = gain_for(&l, &s, [0.0, 0.0, 1.0]);
        assert!(g.left > g.right);
    }
}
