//! Mumble variable-length integer encoding, compatible with Mumble's `PacketDataStream`.
//!
//! | Prefix bits   | Meaning                                         |
//! |---------------|-------------------------------------------------|
//! | `0xxxxxxx`    | 7-bit positive number                           |
//! | `10xxxxxx` +1 | 14-bit positive number                          |
//! | `110xxxxx` +2 | 21-bit positive number                          |
//! | `1110xxxx` +3 | 28-bit positive number                          |
//! | `111100__` +4 | 32-bit positive number                          |
//! | `111101__` +8 | 64-bit number                                   |
//! | `111110__`    | negative recursive varint                       |
//! | `111111xx`    | byte-inverted negative two-bit number (~xx)     |

use crate::{ProtocolError, Result};

/// Maximum number of bytes a single varint can occupy.
pub const MAX_VARINT_LEN: usize = 10;

/// Encodes `value` into `out`, returning the number of bytes written.
///
/// `out` must be at least [`MAX_VARINT_LEN`] bytes long.
#[inline]
pub fn encode(value: u64, out: &mut [u8]) -> usize {
    let mut i = value;
    let mut pos = 0;

    if (i & 0x8000_0000_0000_0000) != 0 && (!i) < 0x1_0000_0000 {
        i = !i;
        if i <= 0x3 {
            out[0] = 0xFC | i as u8;
            return 1;
        }
        out[0] = 0xF8;
        pos = 1;
    }

    let out = &mut out[pos..];
    let written = if i < 0x80 {
        out[0] = i as u8;
        1
    } else if i < 0x4000 {
        out[0] = ((i >> 8) as u8) | 0x80;
        out[1] = i as u8;
        2
    } else if i < 0x20_0000 {
        out[0] = ((i >> 16) as u8) | 0xC0;
        out[1] = (i >> 8) as u8;
        out[2] = i as u8;
        3
    } else if i < 0x1000_0000 {
        out[0] = ((i >> 24) as u8) | 0xE0;
        out[1] = (i >> 16) as u8;
        out[2] = (i >> 8) as u8;
        out[3] = i as u8;
        4
    } else if i < 0x1_0000_0000 {
        out[0] = 0xF0;
        out[1..5].copy_from_slice(&(i as u32).to_be_bytes());
        5
    } else {
        out[0] = 0xF4;
        out[1..9].copy_from_slice(&i.to_be_bytes());
        9
    };
    pos + written
}

/// Appends the encoding of `value` to `buf`.
#[inline]
pub fn write(buf: &mut Vec<u8>, value: u64) {
    let mut tmp = [0u8; MAX_VARINT_LEN];
    let n = encode(value, &mut tmp);
    buf.extend_from_slice(&tmp[..n]);
}

/// Decodes a varint from the start of `data`, returning `(value, bytes_consumed)`.
#[inline]
pub fn decode(data: &[u8]) -> Result<(u64, usize)> {
    let first = *data.first().ok_or(ProtocolError::Truncated)? as u64;
    let need = |n: usize| -> Result<&[u8]> { data.get(1..1 + n).ok_or(ProtocolError::Truncated) };

    if first & 0x80 == 0 {
        return Ok((first & 0x7F, 1));
    }
    if first & 0xC0 == 0x80 {
        let b = need(1)?;
        return Ok(((first & 0x3F) << 8 | b[0] as u64, 2));
    }
    if first & 0xF0 == 0xF0 {
        return match first & 0xFC {
            0xF0 => {
                let b = need(4)?;
                Ok((u32::from_be_bytes([b[0], b[1], b[2], b[3]]) as u64, 5))
            }
            0xF4 => {
                let b = need(8)?;
                let mut arr = [0u8; 8];
                arr.copy_from_slice(b);
                Ok((u64::from_be_bytes(arr), 9))
            }
            0xF8 => {
                let (inner, len) = decode(&data[1..])?;
                Ok((!inner, 1 + len))
            }
            0xFC => Ok((!(first & 0x03), 1)),
            _ => Err(ProtocolError::InvalidVarint),
        };
    }
    if first & 0xF0 == 0xE0 {
        let b = need(3)?;
        return Ok(((first & 0x0F) << 24 | (b[0] as u64) << 16 | (b[1] as u64) << 8 | b[2] as u64, 4));
    }
    if first & 0xE0 == 0xC0 {
        let b = need(2)?;
        return Ok(((first & 0x1F) << 16 | (b[0] as u64) << 8 | b[1] as u64, 3));
    }
    Err(ProtocolError::InvalidVarint)
}

/// A cursor that reads consecutive varints and raw bytes from a slice.
#[derive(Debug)]
pub struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    pub fn new(data: &'a [u8]) -> Self {
        Self { data, pos: 0 }
    }

    #[inline]
    pub fn varint(&mut self) -> Result<u64> {
        let (v, n) = decode(&self.data[self.pos..])?;
        self.pos += n;
        Ok(v)
    }

    #[inline]
    pub fn bytes(&mut self, len: usize) -> Result<&'a [u8]> {
        let end = self.pos.checked_add(len).ok_or(ProtocolError::Truncated)?;
        let slice = self.data.get(self.pos..end).ok_or(ProtocolError::Truncated)?;
        self.pos = end;
        Ok(slice)
    }

    #[inline]
    pub fn remaining(&self) -> &'a [u8] {
        &self.data[self.pos..]
    }

    #[inline]
    pub fn is_empty(&self) -> bool {
        self.pos >= self.data.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn roundtrip(v: u64) {
        let mut buf = [0u8; MAX_VARINT_LEN];
        let n = encode(v, &mut buf);
        let (d, m) = decode(&buf[..n]).unwrap();
        assert_eq!(v, d, "value {v:#x}");
        assert_eq!(n, m);
    }

    #[test]
    fn roundtrips_boundaries() {
        for v in [
            0u64, 1, 0x7F, 0x80, 0x3FFF, 0x4000, 0x1F_FFFF, 0x20_0000, 0x0FFF_FFFF, 0x1000_0000,
            0xFFFF_FFFF, 0x1_0000_0000, u64::MAX / 3,
        ] {
            roundtrip(v);
        }
        for neg in [-1i64, -2, -3, -4, -5, -1000, -(1 << 31)] {
            roundtrip(neg as u64);
        }
    }

    #[test]
    fn known_encodings() {
        let enc = |v: u64| {
            let mut buf = [0u8; MAX_VARINT_LEN];
            let n = encode(v, &mut buf);
            buf[..n].to_vec()
        };
        assert_eq!(enc(0x7F), [0x7F]);
        assert_eq!(enc(0x80), [0x80, 0x80]);
        assert_eq!(enc(-1i64 as u64), [0xFC]);
        assert_eq!(enc(-5i64 as u64), [0xF8, 0x04]);
    }

    #[test]
    fn truncated_is_error() {
        assert!(decode(&[0x80]).is_err());
        assert!(decode(&[]).is_err());
        assert!(decode(&[0xF0, 1, 2]).is_err());
    }
}
