//! Mumble protocol version helpers.
//!
//! * v1 (legacy): `major << 16 | minor << 8 | patch` in a `u32` (each part capped at 255).
//! * v2 (>= 1.5): `major << 48 | minor << 32 | patch << 16` in a `u64`.

use std::fmt;

/// A semantic protocol version.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct Version {
    pub major: u16,
    pub minor: u16,
    pub patch: u16,
}

impl Version {
    /// Version advertised by Rumble.Net (protocol level of Mumble 1.5).
    pub const RUMBLE: Version = Version::new(1, 5, 735);
    /// First version using the protobuf UDP packet format.
    pub const PROTOBUF_UDP: Version = Version::new(1, 5, 0);

    pub const fn new(major: u16, minor: u16, patch: u16) -> Self {
        Self { major, minor, patch }
    }

    pub const fn to_v1(self) -> u32 {
        const fn clamp(v: u16) -> u32 {
            if v > 255 { 255 } else { v as u32 }
        }
        clamp(self.major) << 16 | clamp(self.minor) << 8 | clamp(self.patch)
    }

    pub const fn to_v2(self) -> u64 {
        (self.major as u64) << 48 | (self.minor as u64) << 32 | (self.patch as u64) << 16
    }

    pub const fn from_v1(v: u32) -> Self {
        Self::new(((v >> 16) & 0xFF) as u16, ((v >> 8) & 0xFF) as u16, (v & 0xFF) as u16)
    }

    pub const fn from_v2(v: u64) -> Self {
        Self::new((v >> 48) as u16, (v >> 32) as u16, (v >> 16) as u16)
    }

    /// Resolves the effective version from an optional v1/v2 pair, preferring v2.
    pub fn from_message(v1: Option<u32>, v2: Option<u64>) -> Self {
        match (v2, v1) {
            (Some(v2), _) if v2 != 0 => Self::from_v2(v2),
            (_, Some(v1)) => Self::from_v1(v1),
            _ => Self::default(),
        }
    }

    /// True if this version uses the protobuf-based UDP packet format.
    pub fn uses_protobuf_udp(self) -> bool {
        self >= Self::PROTOBUF_UDP
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn conversions() {
        let v = Version::new(1, 4, 287);
        assert_eq!(Version::from_v2(v.to_v2()), v);
        assert_eq!(Version::from_v1(Version::new(1, 2, 19).to_v1()), Version::new(1, 2, 19));
        assert!(Version::new(1, 5, 0).uses_protobuf_udp());
        assert!(!Version::new(1, 4, 287).uses_protobuf_udp());
        assert_eq!(Version::from_message(Some(0x010204), None), Version::new(1, 2, 4));
    }
}
