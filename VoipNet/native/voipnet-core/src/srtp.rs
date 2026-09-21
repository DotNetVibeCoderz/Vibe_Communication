//! SRTP/SRTCP (RFC 3711) with AES_CM_128_HMAC_SHA1_80 and AEAD_AES_128_GCM / AEAD_AES_256_GCM
//! (RFC 7714). Keys come from SDES (RFC 4568) or DTLS-SRTP (RFC 5764).

use aes::cipher::{KeyIvInit, StreamCipher};
use base64::Engine;
use hmac::{Hmac, Mac};
use ring::aead::{self, Aad, LessSafeKey, Nonce, UnboundKey};
use sha1::Sha1;

type Aes128Ctr = ctr::Ctr128BE<aes::Aes128>;
type Aes256Ctr = ctr::Ctr128BE<aes::Aes256>;
type HmacSha1 = Hmac<Sha1>;

pub const SUITE_AES_CM_128_HMAC_SHA1_80: &str = "AES_CM_128_HMAC_SHA1_80";
pub const MASTER_KEY_LEN: usize = 16;
pub const MASTER_SALT_LEN: usize = 14;
const AUTH_TAG_LEN: usize = 10;
const GCM_TAG_LEN: usize = 16;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SrtpError {
    TooShort,
    AuthFailed,
    Replay,
    BadKey,
}

/// Protection profile of a crypto context.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SrtpProfile {
    AesCm128HmacSha1_80,
    AeadAes128Gcm,
    AeadAes256Gcm,
}

impl SrtpProfile {
    pub fn key_len(self) -> usize {
        match self {
            Self::AesCm128HmacSha1_80 | Self::AeadAes128Gcm => 16,
            Self::AeadAes256Gcm => 32,
        }
    }

    pub fn salt_len(self) -> usize {
        match self {
            Self::AesCm128HmacSha1_80 => 14,
            _ => 12,
        }
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::AesCm128HmacSha1_80 => "AES_CM_128_HMAC_SHA1_80",
            Self::AeadAes128Gcm => "AEAD_AES_128_GCM",
            Self::AeadAes256Gcm => "AEAD_AES_256_GCM",
        }
    }
}

/// RFC 3711 §4.3 key derivation (AES-128-CM PRF, or AES-256-CM PRF from RFC 6188) with kdr = 0.
/// Shorter master salts (96-bit for GCM) are zero-padded to 112 bits, as libsrtp does.
fn derive(master_key: &[u8], master_salt: &[u8], label: u8, out: &mut [u8]) {
    let mut iv = [0u8; 16];
    iv[..master_salt.len()].copy_from_slice(master_salt);
    iv[7] ^= label; // key_id = label || r (r = 0), right-aligned in the 112-bit salt
    out.fill(0);
    if master_key.len() == 32 {
        Aes256Ctr::new(master_key.into(), &iv.into()).apply_keystream(out);
    } else {
        Aes128Ctr::new(master_key.into(), &iv.into()).apply_keystream(out);
    }
}

enum SessionKeys {
    Cm { enc: [u8; 16], auth: [u8; 20], salt: [u8; 14] },
    Gcm { key: LessSafeKey, salt: [u8; 12] },
}

impl SessionKeys {
    fn derive(profile: SrtpProfile, master_key: &[u8], master_salt: &[u8], rtcp: bool) -> Self {
        let base = if rtcp { 3 } else { 0 };
        match profile {
            SrtpProfile::AesCm128HmacSha1_80 => {
                let (mut enc, mut auth, mut salt) = ([0u8; 16], [0u8; 20], [0u8; 14]);
                derive(master_key, master_salt, base, &mut enc);
                derive(master_key, master_salt, base + 1, &mut auth);
                derive(master_key, master_salt, base + 2, &mut salt);
                Self::Cm { enc, auth, salt }
            }
            _ => {
                let mut enc = [0u8; 32];
                let enc = &mut enc[..profile.key_len()];
                let mut salt = [0u8; 12];
                derive(master_key, master_salt, base, enc);
                derive(master_key, master_salt, base + 2, &mut salt);
                Self::gcm(profile, enc, salt)
            }
        }
    }

    fn gcm(profile: SrtpProfile, key: &[u8], salt: [u8; 12]) -> Self {
        let alg = if profile == SrtpProfile::AeadAes256Gcm { &aead::AES_256_GCM } else { &aead::AES_128_GCM };
        let key = LessSafeKey::new(UnboundKey::new(alg, key).expect("key length matches the profile"));
        Self::Gcm { key, salt }
    }

    fn cm_iv(salt: &[u8; 14], ssrc: u32, index: u64) -> [u8; 16] {
        let mut iv = [0u8; 16];
        iv[..14].copy_from_slice(salt);
        for (i, b) in ssrc.to_be_bytes().iter().enumerate() {
            iv[4 + i] ^= b;
        }
        let idx = index.to_be_bytes(); // 48-bit index occupies bytes 8..14
        for i in 0..6 {
            iv[8 + i] ^= idx[2 + i];
        }
        iv
    }

    /// RFC 7714 §8.1 / §9.1: 00 00 || SSRC || 32-bit counter || 16-bit counter, XOR salt.
    fn gcm_nonce(salt: &[u8; 12], ssrc: u32, high: u32, low: u16) -> Nonce {
        let mut iv = [0u8; 12];
        iv[2..6].copy_from_slice(&ssrc.to_be_bytes());
        iv[6..10].copy_from_slice(&high.to_be_bytes());
        iv[10..12].copy_from_slice(&low.to_be_bytes());
        for (b, s) in iv.iter_mut().zip(salt) {
            *b ^= s;
        }
        Nonce::assume_unique_for_key(iv)
    }

    fn tag(auth: &[u8; 20], parts: &[&[u8]]) -> [u8; AUTH_TAG_LEN] {
        let mut mac = <HmacSha1 as Mac>::new_from_slice(auth).expect("hmac accepts any key length");
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
pub struct SrtpContext {
    profile: SrtpProfile,
    rtp: SessionKeys,
    rtcp: SessionKeys,
    roc: u32,
    last_seq: Option<u16>,
    srtcp_index: u32,
    replay_top: Option<u64>,
    replay_window: u64,
}

impl SrtpContext {
    /// Builds a context from a master key and salt of the lengths required by `profile`.
    pub fn with_profile(profile: SrtpProfile, master_key: &[u8], master_salt: &[u8]) -> Result<Self, SrtpError> {
        if master_key.len() != profile.key_len() || master_salt.len() != profile.salt_len() {
            return Err(SrtpError::BadKey);
        }
        Ok(Self::from_keys(
            profile,
            SessionKeys::derive(profile, master_key, master_salt, false),
            SessionKeys::derive(profile, master_key, master_salt, true),
        ))
    }

    fn from_keys(profile: SrtpProfile, rtp: SessionKeys, rtcp: SessionKeys) -> Self {
        Self { profile, rtp, rtcp, roc: 0, last_seq: None, srtcp_index: 0, replay_top: None, replay_window: 0 }
    }

    pub fn new(master_key: &[u8; 16], master_salt: &[u8; 14]) -> Self {
        Self::with_profile(SrtpProfile::AesCm128HmacSha1_80, master_key, master_salt).expect("fixed-size key")
    }

    pub fn profile(&self) -> SrtpProfile {
        self.profile
    }

    /// Builds a context from an SDES `inline:` key-params string.
    pub fn from_sdes(key_params: &str) -> Result<Self, SrtpError> {
        let b64 = key_params.strip_prefix("inline:").ok_or(SrtpError::BadKey)?;
        let b64 = b64.split('|').next().unwrap_or(b64);
        let raw = base64::engine::general_purpose::STANDARD.decode(b64).map_err(|_| SrtpError::BadKey)?;
        if raw.len() != MASTER_KEY_LEN + MASTER_SALT_LEN {
            return Err(SrtpError::BadKey);
        }
        Self::with_profile(SrtpProfile::AesCm128HmacSha1_80, &raw[..16], &raw[16..])
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

    /// Splits DTLS-SRTP keying material (RFC 5764 §4.2: client key, server key, client salt,
    /// server salt) into (outbound, inbound) contexts for the local role.
    pub fn from_dtls(profile: SrtpProfile, material: &[u8], local_is_client: bool) -> Result<(Self, Self), SrtpError> {
        let (k, s) = (profile.key_len(), profile.salt_len());
        if material.len() < 2 * (k + s) {
            return Err(SrtpError::BadKey);
        }
        let client = Self::with_profile(profile, &material[..k], &material[2 * k..2 * k + s])?;
        let server = Self::with_profile(profile, &material[k..2 * k], &material[2 * k + s..2 * (k + s)])?;
        Ok(if local_is_client { (client, server) } else { (server, client) })
    }

    fn outbound_roc(&mut self, seq: u16) -> u32 {
        if let Some(last) = self.last_seq {
            if seq < last && last.wrapping_sub(seq) > 0x8000 {
                self.roc = self.roc.wrapping_add(1);
            }
        }
        self.last_seq = Some(seq);
        self.roc
    }

    /// Encrypts an RTP packet in place and appends the authentication tag.
    pub fn protect_rtp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        let header_len = rtp_header_len(packet).ok_or(SrtpError::TooShort)?;
        let seq = u16::from_be_bytes([packet[2], packet[3]]);
        let ssrc = u32::from_be_bytes([packet[8], packet[9], packet[10], packet[11]]);
        let roc = self.outbound_roc(seq);
        match &self.rtp {
            SessionKeys::Cm { enc, auth, salt } => {
                let index = ((roc as u64) << 16) | seq as u64;
                let iv = SessionKeys::cm_iv(salt, ssrc, index);
                Aes128Ctr::new(enc.into(), &iv.into()).apply_keystream(&mut packet[header_len..]);
                let tag = SessionKeys::tag(auth, &[packet, &roc.to_be_bytes()]);
                packet.extend_from_slice(&tag);
            }
            SessionKeys::Gcm { key, salt } => {
                let nonce = SessionKeys::gcm_nonce(salt, ssrc, roc, seq);
                let mut body = packet.split_off(header_len);
                key.seal_in_place_append_tag(nonce, Aad::from(&packet[..]), &mut body).map_err(|_| SrtpError::BadKey)?;
                packet.extend_from_slice(&body);
            }
        }
        Ok(())
    }

    /// Verifies and decrypts an SRTP packet in place (tag removed on success).
    pub fn unprotect_rtp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        let tag_len = if matches!(self.rtp, SessionKeys::Cm { .. }) { AUTH_TAG_LEN } else { GCM_TAG_LEN };
        if packet.len() < 12 + tag_len {
            return Err(SrtpError::TooShort);
        }
        let body_len = packet.len() - tag_len;
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

        match &self.rtp {
            SessionKeys::Cm { enc, auth, salt } => {
                let expected = SessionKeys::tag(auth, &[&packet[..body_len], &roc_guess.to_be_bytes()]);
                if !constant_time_eq(&expected, &packet[body_len..]) {
                    return Err(SrtpError::AuthFailed);
                }
                packet.truncate(body_len);
                let iv = SessionKeys::cm_iv(salt, ssrc, index);
                Aes128Ctr::new(enc.into(), &iv.into()).apply_keystream(&mut packet[header_len..]);
            }
            SessionKeys::Gcm { key, salt } => {
                let nonce = SessionKeys::gcm_nonce(salt, ssrc, roc_guess, seq);
                let (header, body) = packet.split_at_mut(header_len);
                key.open_in_place(nonce, Aad::from(&header[..]), body).map_err(|_| SrtpError::AuthFailed)?;
                packet.truncate(body_len);
            }
        }

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
        let e_index = (0x8000_0000 | self.srtcp_index).to_be_bytes();
        match &self.rtcp {
            SessionKeys::Cm { enc, auth, salt } => {
                let iv = SessionKeys::cm_iv(salt, ssrc, self.srtcp_index as u64);
                Aes128Ctr::new(enc.into(), &iv.into()).apply_keystream(&mut packet[8..]);
                packet.extend_from_slice(&e_index);
                let tag = SessionKeys::tag(auth, &[packet]);
                packet.extend_from_slice(&tag);
            }
            SessionKeys::Gcm { key, salt } => {
                // RFC 7714 §9: AAD = header || E+index; the E+index trailer follows the tag.
                let nonce = SessionKeys::gcm_nonce(salt, ssrc, self.srtcp_index >> 16, self.srtcp_index as u16);
                let mut aad = [0u8; 12];
                aad[..8].copy_from_slice(&packet[..8]);
                aad[8..].copy_from_slice(&e_index);
                let mut body = packet.split_off(8);
                key.seal_in_place_append_tag(nonce, Aad::from(aad), &mut body).map_err(|_| SrtpError::BadKey)?;
                packet.extend_from_slice(&body);
                packet.extend_from_slice(&e_index);
            }
        }
        Ok(())
    }

    pub fn unprotect_rtcp(&mut self, packet: &mut Vec<u8>) -> Result<(), SrtpError> {
        match &self.rtcp {
            SessionKeys::Cm { enc, auth, salt } => {
                if packet.len() < 8 + 4 + AUTH_TAG_LEN {
                    return Err(SrtpError::TooShort);
                }
                let tag_start = packet.len() - AUTH_TAG_LEN;
                let expected = SessionKeys::tag(auth, &[&packet[..tag_start]]);
                if !constant_time_eq(&expected, &packet[tag_start..]) {
                    return Err(SrtpError::AuthFailed);
                }
                let idx_start = tag_start - 4;
                let e_index = read_u32(&packet[idx_start..]);
                packet.truncate(idx_start);
                if e_index & 0x8000_0000 != 0 {
                    let ssrc = read_u32(&packet[4..]);
                    let iv = SessionKeys::cm_iv(salt, ssrc, (e_index & 0x7FFF_FFFF) as u64);
                    Aes128Ctr::new(enc.into(), &iv.into()).apply_keystream(&mut packet[8..]);
                }
            }
            SessionKeys::Gcm { key, salt } => {
                if packet.len() < 8 + GCM_TAG_LEN + 4 {
                    return Err(SrtpError::TooShort);
                }
                let idx_start = packet.len() - 4;
                let e_index = read_u32(&packet[idx_start..]);
                let index = e_index & 0x7FFF_FFFF;
                let ssrc = read_u32(&packet[4..]);
                let nonce = SessionKeys::gcm_nonce(salt, ssrc, index >> 16, index as u16);
                packet.truncate(idx_start);
                if e_index & 0x8000_0000 != 0 {
                    let mut aad = [0u8; 12];
                    aad[..8].copy_from_slice(&packet[..8]);
                    aad[8..].copy_from_slice(&e_index.to_be_bytes());
                    let (_, body) = packet.split_at_mut(8);
                    key.open_in_place(nonce, Aad::from(aad), body).map_err(|_| SrtpError::AuthFailed)?;
                } else {
                    // Authenticated but unencrypted: everything before the tag is associated data.
                    let tag_start = packet.len() - GCM_TAG_LEN;
                    let mut aad = packet[..tag_start].to_vec();
                    aad.extend_from_slice(&e_index.to_be_bytes());
                    let mut tag = packet[tag_start..].to_vec();
                    key.open_in_place(nonce, Aad::from(aad), &mut tag).map_err(|_| SrtpError::AuthFailed)?;
                }
                packet.truncate(packet.len() - GCM_TAG_LEN);
            }
        }
        Ok(())
    }
}

fn read_u32(b: &[u8]) -> u32 {
    u32::from_be_bytes([b[0], b[1], b[2], b[3]])
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
        let s: String = s.chars().filter(|c| !c.is_whitespace()).collect();
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    #[test]
    fn rfc3711_key_derivation_vectors() {
        let key = hex("E1F97A0D3E018BE0D64FA32C06DE4139");
        let salt = hex("0EC675AD498AFEEBB6960B3AABE6");
        let SessionKeys::Cm { enc, auth, salt } = SessionKeys::derive(SrtpProfile::AesCm128HmacSha1_80, &key, &salt, false) else {
            unreachable!()
        };
        assert_eq!(enc.to_vec(), hex("C61E7A93744F39EE10734AFE3FF7A087"));
        assert_eq!(salt.to_vec(), hex("30CBBC08863D8C85D49DB34A9AE1"));
        assert_eq!(auth.to_vec(), hex("CEBE321F6FF7716B6FD4AB49AF256A156D38BAA4"));
    }

    /// RFC 7714 §16.1.1 / §16.2.1 (session key and salt given directly).
    #[test]
    fn rfc7714_gcm_vectors() {
        let plain = hex("8040f17b 8041f8d3 5501a0b2 47616c6c 69612065 7374206f 6d6e6973 20646976 69736120 696e2070 61727465 73207472 6573");
        let salt: [u8; 12] = hex("517569642070726f2071756f").try_into().unwrap();
        let cases = [
            (
                SrtpProfile::AeadAes128Gcm,
                hex("000102030405060708090a0b0c0d0e0f"),
                hex("8040f17b 8041f8d3 5501a0b2 f24de3a3 fb34de6c acba861c 9d7e4bca be633bd5 0d294e6f 42a5f47a 51c7d19b 36de3adf 8833899d 7f27beb1 6a9152cf 765ee439 0cce"),
            ),
            (
                SrtpProfile::AeadAes256Gcm,
                hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
                hex("8040f17b 8041f8d3 5501a0b2 32b1de78 a822fe12 ef9f78fa 332e33aa b1801238 9a58e2f3 b50b2a02 76ffae0f 1ba63799 b87b7aa3 db36dfff d6b0f9bb 7878d7a7 6c13"),
            ),
        ];
        for (profile, key, expected) in cases {
            let ctx = || SrtpContext::from_keys(profile, SessionKeys::gcm(profile, &key, salt), SessionKeys::gcm(profile, &key, salt));
            let mut p = plain.clone();
            ctx().protect_rtp(&mut p).unwrap();
            assert_eq!(p, expected, "{profile:?}");
            ctx().unprotect_rtp(&mut p).unwrap();
            assert_eq!(p, plain);
        }
    }

    fn rtp(seq: u16) -> Vec<u8> {
        let mut p = vec![0x80, 0, 0, 0, 0, 0, 0, 160, 0xCA, 0xFE, 0xBA, 0xBE];
        p[2..4].copy_from_slice(&seq.to_be_bytes());
        p.extend((0..160).map(|i| i as u8));
        p
    }

    fn pairs() -> Vec<(SrtpContext, SrtpContext)> {
        let (params, tx) = SrtpContext::generate();
        let mut out = vec![(tx, SrtpContext::from_sdes(&params).unwrap())];
        for profile in [SrtpProfile::AeadAes128Gcm, SrtpProfile::AeadAes256Gcm] {
            let material: Vec<u8> = (0..2 * (profile.key_len() + profile.salt_len())).map(|i| (i * 7) as u8).collect();
            let (client_tx, _) = SrtpContext::from_dtls(profile, &material, true).unwrap();
            let (_, server_rx) = SrtpContext::from_dtls(profile, &material, false).unwrap();
            out.push((client_tx, server_rx));
        }
        out
    }

    #[test]
    fn rtp_roundtrip_across_rollover_and_replay() {
        for (mut tx, mut rx) in pairs() {
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
    }

    #[test]
    fn tampering_is_detected() {
        for (mut tx, mut rx) in pairs() {
            let mut p = rtp(10);
            tx.protect_rtp(&mut p).unwrap();
            p[20] ^= 1;
            assert_eq!(rx.unprotect_rtp(&mut p), Err(SrtpError::AuthFailed), "{:?}", tx.profile());
        }
    }

    #[test]
    fn rtcp_roundtrip() {
        for (mut tx, mut rx) in pairs() {
            let plain = crate::rtp::packet::build_sender_report(7, 1, 2, 3, 4, "x@y", None);
            let mut p = plain.clone();
            tx.protect_rtcp(&mut p).unwrap();
            rx.unprotect_rtcp(&mut p).unwrap();
            assert_eq!(p, plain, "{:?}", tx.profile());
        }
    }
}
