//! SRTP/SRTCP with AES_CM_128_HMAC_SHA1_80 (RFC 3711) and SDES keying (RFC 4568).

use aes::cipher::{KeyIvInit, StreamCipher};
use base64::Engine;
use hmac::{Hmac, Mac};
use sha1::Sha1;

type Aes128Ctr = ctr::Ctr128BE<aes::Aes128>;
type HmacSha1 = Hmac<Sha1>;

pub const SUITE_AES_CM_128_HMAC_SHA1_80: &str = "AES_CM_128_HMAC_SHA1_80";
pub const MASTER_KEY_LEN: usize = 16;
pub const MASTER_SALT_LEN: usize = 14;
const AUTH_TAG_LEN: usize = 10;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SrtpError {
    TooShort,
    AuthFailed,
    Replay,
    BadKey,
}

#[derive(Clone)]
struct SessionKeys {
    enc: [u8; 16],
    auth: [u8; 20],
    salt: [u8; 14],
}

fn derive(master_key: &[u8; 16], master_salt: &[u8; 14], label: u8, out: &mut [u8]) {
    let mut iv = [0u8; 16];
    iv[..14].copy_from_slice(master_salt);
    iv[7] ^= label; // key_id = label || r (r = 0 since kdr = 0), right-aligned in the 112-bit salt
    out.fill(0);
    let mut c = Aes128Ctr::new(master_key.into(), &iv.into());
    c.apply_keystream(out);
}

impl SessionKeys {
    fn new(master_key: &[u8; 16], master_salt: &[u8; 14], rtcp: bool) -> Self {
        let base = if rtcp { 3 } else { 0 };
        let mut k = Self { enc: [0; 16], auth: [0; 20], salt: [0; 14] };
        derive(master_key, master_salt, base, &mut k.enc);
        derive(master_key, master_salt, base + 1, &mut k.auth);
        derive(master_key, master_salt, base + 2, &mut k.salt);
        k
    }

    fn iv(&self, ssrc: u32, index: u64) -> [u8; 16] {
        let mut iv = [0u8; 16];
        iv[..14].copy_from_slice(&self.salt);
        for (i, b) in ssrc.to_be_bytes().iter().enumerate() {
            iv[4 + i] ^= b;
        }
        let idx = index.to_be_bytes(); // 48-bit index occupies bytes 8..14
        for i in 0..6 {
            iv[8 + i] ^= idx[2 + i];
        }
        iv
    }

    fn tag(&self, parts: &[&[u8]]) -> [u8; AUTH_TAG_LEN] {
        let mut mac = <HmacSha1 as Mac>::new_from_slice(&self.auth).expect("hmac accepts any key length");
        for p in parts {
            mac.update(p);
        }
        let full = mac.finalize().into_bytes();
        let mut t = [0u8; AUTH_TAG_LEN];
        t.copy_from_slice(&full[..AUTH_TAG_LEN]);
        t
    }
}

/// One direction of an SRTP crypto context.
#[derive(Clone)]
pub struct SrtpContext {
    rtp: SessionKeys,
    rtcp: SessionKeys,
    roc: u32,
    last_seq: Option<u16>,
    srtcp_index: u32,
    replay_top: Option<u64>,
    replay_window: u64,
}

impl SrtpContext {
    pub fn new(master_key: &[u8; 16], master_salt: &[u8; 14]) -> Self {
        Self {
            rtp: SessionKeys::new(master_key, master_salt, false),
            rtcp: SessionKeys::new(master_key, master_salt, true),
            roc: 0,
            last_seq: None,
            srtcp_index: 0,
            replay_top: None,
            replay_window: 0,
        }
    }

    /// Builds a context from an SDES `inline:` key-params string.
    pub fn from_sdes(key_params: &str) -> Result<Self, SrtpError> {
        let b64 = key_params.strip_prefix("inline:").ok_or(SrtpError::BadKey)?;
        let b64 = b64.split('|').next().unwrap_or(b64);
        let raw = base64::engine::general_purpose::STANDARD.decode(b64).map_err(|_| SrtpError::BadKey)?;
        if raw.len() != MASTER_KEY_LEN + MASTER_SALT_LEN {
            return Err(SrtpError::BadKey);
        }
        let mut key = [0u8; 16];
        let mut salt = [0u8; 14];
        key.copy_from_slice(&raw[..16]);
        salt.copy_from_slice(&raw[16..]);
        Ok(Self::new(&key, &salt))
    }

    /// Generates a random master key/salt and returns (`inline:...` key-params, context).
    pub fn generate() -> (String, Self) {
        use rand::RngCore;
        let mut raw = [0u8; 30];
        rand::thread_rng().fill_bytes(&mut raw);
        let params = format!("inline:{}", base64::engine::general_purpose::STANDARD.encode(raw));
        let ctx = Self::from_sdes(&params).expect("generated key is valid");
        (params, ctx)
    }

    /// Encrypts an RTP packet in place and appends the authentication tag.
    pub fn protect_rtp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        let header_len = rtp_header_len(packet).ok_or(SrtpError::TooShort)?;
        let seq = u16::from_be_bytes([packet[2], packet[3]]);
        let ssrc = u32::from_be_bytes([packet[8], packet[9], packet[10], packet[11]]);
        if let Some(last) = self.last_seq {
            if seq < last && last.wrapping_sub(seq) > 0x8000 {
                self.roc = self.roc.wrapping_add(1);
            }
        }
        self.last_seq = Some(seq);
        let index = ((self.roc as u64) << 16) | seq as u64;
        let iv = self.rtp.iv(ssrc, index);
        let mut c = Aes128Ctr::new(&self.rtp.enc.into(), &iv.into());
        c.apply_keystream(&mut packet[header_len..]);
        let tag = self.rtp.tag(&[packet, &self.roc.to_be_bytes()]);
        packet.extend_from_slice(&tag);
        Ok(())
    }

    /// Verifies and decrypts an SRTP packet in place (tag removed on success).
    pub fn unprotect_rtp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        if packet.len() < 12 + AUTH_TAG_LEN {
            return Err(SrtpError::TooShort);
        }
        let body_len = packet.len() - AUTH_TAG_LEN;
        let header_len = rtp_header_len(&packet[..body_len]).ok_or(SrtpError::TooShort)?;
        let seq = u16::from_be_bytes([packet[2], packet[3]]);
        let ssrc = u32::from_be_bytes([packet[8], packet[9], packet[10], packet[11]]);

        // RFC 3711 §3.3.1 ROC estimation.
        let roc_guess = match self.last_seq {
            None => self.roc,
            Some(s_l) => {
                if s_l < 0x8000 {
                    if seq as i32 - s_l as i32 > 0x8000 {
                        self.roc.wrapping_sub(1)
                    } else {
                        self.roc
                    }
                } else if (s_l as i32 - 0x8000) > seq as i32 {
                    self.roc.wrapping_add(1)
                } else {
                    self.roc
                }
            }
        };
        let index = ((roc_guess as u64) << 16) | seq as u64;
        if self.is_replay(index) {
            return Err(SrtpError::Replay);
        }

        let expected = self.rtp.tag(&[&packet[..body_len], &roc_guess.to_be_bytes()]);
        if !constant_time_eq(&expected, &packet[body_len..]) {
            return Err(SrtpError::AuthFailed);
        }
        packet.truncate(body_len);
        let iv = self.rtp.iv(ssrc, index);
        let mut c = Aes128Ctr::new(&self.rtp.enc.into(), &iv.into());
        c.apply_keystream(&mut packet[header_len..]);

        self.accept_index(index);
        if roc_guess == self.roc.wrapping_add(1) || self.last_seq.is_none() {
            self.roc = roc_guess;
            self.last_seq = Some(seq);
        } else if roc_guess == self.roc && self.last_seq.is_some_and(|l| seq > l) {
            self.last_seq = Some(seq);
        }
        Ok(())
    }

    fn is_replay(&self, index: u64) -> bool {
        match self.replay_top {
            None => false,
            Some(top) if index > top => false,
            Some(top) => {
                let delta = top - index;
                delta >= 64 || (self.replay_window >> delta) & 1 == 1
            }
        }
    }

    fn accept_index(&mut self, index: u64) {
        match self.replay_top {
            None => {
                self.replay_top = Some(index);
                self.replay_window = 1;
            }
            Some(top) if index > top => {
                let shift = index - top;
                self.replay_window = if shift >= 64 { 1 } else { (self.replay_window << shift) | 1 };
                self.replay_top = Some(index);
            }
            Some(top) => self.replay_window |= 1 << (top - index),
        }
    }

    pub fn protect_rtcp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        if packet.len() < 8 {
            return Err(SrtpError::TooShort);
        }
        let ssrc = u32::from_be_bytes([packet[4], packet[5], packet[6], packet[7]]);
        self.srtcp_index = (self.srtcp_index + 1) & 0x7FFF_FFFF;
        let iv = self.rtcp.iv(ssrc, self.srtcp_index as u64);
        let mut c = Aes128Ctr::new(&self.rtcp.enc.into(), &iv.into());
        c.apply_keystream(&mut packet[8..]);
        packet.extend_from_slice(&(0x8000_0000 | self.srtcp_index).to_be_bytes());
        let tag = self.rtcp.tag(&[packet]);
        packet.extend_from_slice(&tag);
        Ok(())
    }

    pub fn unprotect_rtcp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        if packet.len() < 8 + 4 + AUTH_TAG_LEN {
            return Err(SrtpError::TooShort);
        }
        let tag_start = packet.len() - AUTH_TAG_LEN;
        let expected = self.rtcp.tag(&[&packet[..tag_start]]);
        if !constant_time_eq(&expected, &packet[tag_start..]) {
            return Err(SrtpError::AuthFailed);
        }
        let idx_start = tag_start - 4;
        let e_index = u32::from_be_bytes([packet[idx_start], packet[idx_start + 1], packet[idx_start + 2], packet[idx_start + 3]]);
        packet.truncate(idx_start);
        if e_index & 0x8000_0000 != 0 {
            let ssrc = u32::from_be_bytes([packet[4], packet[5], packet[6], packet[7]]);
            let iv = self.rtcp.iv(ssrc, (e_index & 0x7FFF_FFFF) as u64);
            let mut c = Aes128Ctr::new(&self.rtcp.enc.into(), &iv.into());
            c.apply_keystream(&mut packet[8..]);
        }
        Ok(())
    }
}

fn rtp_header_len(p: &[u8]) -> Option<usize> {
    if p.len() < 12 {
        return None;
    }
    let mut len = 12 + (p[0] & 0x0F) as usize * 4;
    if p[0] & 0x10 != 0 {
        if p.len() < len + 4 {
            return None;
        }
        len += 4 + u16::from_be_bytes([p[len + 2], p[len + 3]]) as usize * 4;
    }
    (len <= p.len()).then_some(len)
}

fn constant_time_eq(a: &[u8], b: &[u8]) -> bool {
    a.len() == b.len() && a.iter().zip(b).fold(0u8, |acc, (x, y)| acc | (x ^ y)) == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    #[test]
    fn rfc3711_key_derivation_vectors() {
        let key: [u8; 16] = hex("E1F97A0D3E018BE0D64FA32C06DE4139").try_into().unwrap();
        let salt: [u8; 14] = hex("0EC675AD498AFEEBB6960B3AABE6").try_into().unwrap();
        let k = SessionKeys::new(&key, &salt, false);
        assert_eq!(k.enc.to_vec(), hex("C61E7A93744F39EE10734AFE3FF7A087"));
        assert_eq!(k.salt.to_vec(), hex("30CBBC08863D8C85D49DB34A9AE1"));
        assert_eq!(k.auth[..20].to_vec(), hex("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA4"));
    }

    fn rtp(seq: u16) -> Vec<u8> {
        let mut p = vec![0x80, 0, 0, 0, 0, 0, 0, 160, 0xCA, 0xFE, 0xBA, 0xBE];
        p[2..4].copy_from_slice(&seq.to_be_bytes());
        p.extend((0..160).map(|i| i as u8));
        p
    }

    #[test]
    fn rtp_roundtrip_across_rollover_and_replay() {
        let (params, mut tx) = SrtpContext::generate();
        let mut rx = SrtpContext::from_sdes(&params).unwrap();
        for seq in [65533u16, 65534, 65535, 0, 1, 2] {
            let plain = rtp(seq);
            let mut p = plain.clone();
            tx.protect_rtp(&mut p).unwrap();
            assert_ne!(p[12..172], plain[12..]);
            let replay = p.clone();
            rx.unprotect_rtp(&mut p).unwrap();
            assert_eq!(p, plain);
            assert_eq!(rx.unprotect_rtp(&mut replay.clone()), Err(SrtpError::Replay));
        }
    }

    #[test]
    fn tampering_is_detected() {
        let (params, mut tx) = SrtpContext::generate();
        let mut rx = SrtpContext::from_sdes(&params).unwrap();
        let mut p = rtp(10);
        tx.protect_rtp(&mut p).unwrap();
        p[20] ^= 1;
        assert_eq!(rx.unprotect_rtp(&mut p), Err(SrtpError::AuthFailed));
    }

    #[test]
    fn rtcp_roundtrip() {
        let (params, mut tx) = SrtpContext::generate();
        let mut rx = SrtpContext::from_sdes(&params).unwrap();
        let plain = crate::rtp::packet::build_sender_report(7, 1, 2, 3, 4, "x@y");
        let mut p = plain.clone();
        tx.protect_rtcp(&mut p).unwrap();
        rx.unprotect_rtcp(&mut p).unwrap();
        assert_eq!(p, plain);
    }
}
