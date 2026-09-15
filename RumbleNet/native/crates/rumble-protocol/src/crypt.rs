//! OCB2-AES128 crypt state for Mumble UDP datagrams.
//!
//! Byte-for-byte compatible with Mumble's `CryptStateOCB2`, including the
//! countermeasures against the XEX* attack described in section 9 of
//! <https://eprint.iacr.org/2019/311>.
//!
//! Encrypted datagram layout: `iv_byte:u8 | tag[0..3] | ciphertext`.

use aes::Aes128;
use aes::cipher::{Array, BlockCipherDecrypt, BlockCipherEncrypt, KeyInit};

/// AES block size in bytes.
pub const BLOCK_SIZE: usize = 16;
/// Bytes added to a plaintext by [`CryptState::encrypt`].
pub const OVERHEAD: usize = 4;

type Block = [u8; BLOCK_SIZE];

/// Packet statistics maintained by the crypt state (reported to the server in pings).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CryptStats {
    pub good: u32,
    pub late: u32,
    pub lost: u32,
    pub resync: u32,
}

/// Mumble OCB2-AES128 crypt state.
pub struct CryptState {
    cipher: Option<Aes128>,
    raw_key: Block,
    encrypt_iv: Block,
    decrypt_iv: Block,
    decrypt_history: [u8; 256],
    /// Statistics for packets received from the remote side.
    pub local: CryptStats,
    /// Statistics reported by the remote side.
    pub remote: CryptStats,
}

impl Default for CryptState {
    fn default() -> Self {
        Self::new()
    }
}

impl std::fmt::Debug for CryptState {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CryptState")
            .field("valid", &self.is_valid())
            .field("local", &self.local)
            .field("remote", &self.remote)
            .finish_non_exhaustive()
    }
}

#[inline(always)]
fn xor(dst: &mut Block, a: &Block, b: &Block) {
    for i in 0..BLOCK_SIZE {
        dst[i] = a[i] ^ b[i];
    }
}

/// Multiplication by two in GF(2^128) (big-endian, polynomial 0x87).
#[inline(always)]
fn s2(block: &mut Block) {
    let v = u128::from_be_bytes(*block);
    let carry = (v >> 127) as u8;
    let r = (v << 1) ^ (carry as u128 * 0x87);
    *block = r.to_be_bytes();
}

/// `block ^= 2 * block`.
#[inline(always)]
fn s3(block: &mut Block) {
    let v = u128::from_be_bytes(*block);
    let carry = (v >> 127) as u8;
    let r = v ^ ((v << 1) ^ (carry as u128 * 0x87));
    *block = r.to_be_bytes();
}

impl CryptState {
    pub fn new() -> Self {
        Self {
            cipher: None,
            raw_key: [0; BLOCK_SIZE],
            encrypt_iv: [0; BLOCK_SIZE],
            decrypt_iv: [0; BLOCK_SIZE],
            decrypt_history: [0; 256],
            local: CryptStats::default(),
            remote: CryptStats::default(),
        }
    }

    /// True once a key has been set.
    pub fn is_valid(&self) -> bool {
        self.cipher.is_some()
    }

    /// Generates a random key and IVs (server side).
    pub fn generate_key(&mut self) {
        let mut key = [0u8; BLOCK_SIZE];
        let mut eiv = [0u8; BLOCK_SIZE];
        let mut div = [0u8; BLOCK_SIZE];
        rand::fill(&mut key);
        rand::fill(&mut eiv);
        rand::fill(&mut div);
        self.set_key(&key, &eiv, &div);
    }

    /// Sets key, encrypt IV and decrypt IV. Returns false if lengths are invalid.
    pub fn set_key(&mut self, key: &[u8], encrypt_iv: &[u8], decrypt_iv: &[u8]) -> bool {
        if key.len() != BLOCK_SIZE || encrypt_iv.len() != BLOCK_SIZE || decrypt_iv.len() != BLOCK_SIZE {
            return false;
        }
        self.raw_key.copy_from_slice(key);
        self.encrypt_iv.copy_from_slice(encrypt_iv);
        self.decrypt_iv.copy_from_slice(decrypt_iv);
        self.decrypt_history = [0; 256];
        let key_arr = Array::from(self.raw_key);
        self.cipher = Some(Aes128::new(&key_arr));
        true
    }

    /// Updates only the decrypt IV (resync from remote).
    pub fn set_decrypt_iv(&mut self, iv: &[u8]) -> bool {
        if iv.len() != BLOCK_SIZE {
            return false;
        }
        self.decrypt_iv.copy_from_slice(iv);
        self.local.resync = self.local.resync.wrapping_add(1);
        true
    }

    pub fn raw_key(&self) -> &[u8; BLOCK_SIZE] {
        &self.raw_key
    }

    pub fn encrypt_iv(&self) -> &[u8; BLOCK_SIZE] {
        &self.encrypt_iv
    }

    pub fn decrypt_iv(&self) -> &[u8; BLOCK_SIZE] {
        &self.decrypt_iv
    }

    #[inline(always)]
    fn aes_encrypt(cipher: &Aes128, src: &Block) -> Block {
        let mut b = Array::from(*src);
        cipher.encrypt_block(&mut b);
        b.into()
    }

    #[inline(always)]
    fn aes_decrypt(cipher: &Aes128, src: &Block) -> Block {
        let mut b = Array::from(*src);
        cipher.decrypt_block(&mut b);
        b.into()
    }

    /// Encrypts `plain` into `out` (which must hold `plain.len() + OVERHEAD` bytes).
    /// Returns the number of bytes written, or `None` if no key is set / `out` is too small.
    pub fn encrypt(&mut self, plain: &[u8], out: &mut [u8]) -> Option<usize> {
        let total = plain.len() + OVERHEAD;
        if out.len() < total {
            return None;
        }
        let cipher = self.cipher.as_ref()?;

        for i in 0..BLOCK_SIZE {
            self.encrypt_iv[i] = self.encrypt_iv[i].wrapping_add(1);
            if self.encrypt_iv[i] != 0 {
                break;
            }
        }

        let mut tag = [0u8; BLOCK_SIZE];
        ocb_encrypt(cipher, plain, &mut out[OVERHEAD..total], &self.encrypt_iv, &mut tag, true);
        out[0] = self.encrypt_iv[0];
        out[1..4].copy_from_slice(&tag[..3]);
        Some(total)
    }

    /// Convenience wrapper that allocates the output buffer.
    pub fn encrypt_to_vec(&mut self, plain: &[u8]) -> Option<Vec<u8>> {
        let mut out = vec![0u8; plain.len() + OVERHEAD];
        self.encrypt(plain, &mut out).map(|_| out)
    }

    /// Decrypts `source` into `out` (which must hold `source.len() - OVERHEAD` bytes).
    /// Returns the plaintext length on success; updates packet statistics.
    pub fn decrypt(&mut self, source: &[u8], out: &mut [u8]) -> Option<usize> {
        if source.len() < OVERHEAD {
            return None;
        }
        let plain_len = source.len() - OVERHEAD;
        if out.len() < plain_len {
            return None;
        }
        let cipher = self.cipher.as_ref()?;

        let save_iv = self.decrypt_iv;
        let iv_byte = source[0];
        let mut restore = false;
        let mut lost: i32 = 0;
        let mut late: i32 = 0;

        if self.decrypt_iv[0].wrapping_add(1) == iv_byte {
            // In order as expected.
            if iv_byte > self.decrypt_iv[0] {
                self.decrypt_iv[0] = iv_byte;
            } else if iv_byte < self.decrypt_iv[0] {
                self.decrypt_iv[0] = iv_byte;
                for i in 1..BLOCK_SIZE {
                    self.decrypt_iv[i] = self.decrypt_iv[i].wrapping_add(1);
                    if self.decrypt_iv[i] != 0 {
                        break;
                    }
                }
            } else {
                return None;
            }
        } else {
            // Out of order or a repeat.
            let cur = self.decrypt_iv[0] as i32;
            let ivb = iv_byte as i32;
            let mut diff = ivb - cur;
            if diff > 128 {
                diff -= 256;
            } else if diff < -128 {
                diff += 256;
            }

            if ivb < cur && diff > -30 && diff < 0 {
                // Late packet, no wraparound.
                late = 1;
                lost = -1;
                self.decrypt_iv[0] = iv_byte;
                restore = true;
            } else if ivb > cur && diff > -30 && diff < 0 {
                // Late packet from the previous round (wraparound).
                late = 1;
                lost = -1;
                self.decrypt_iv[0] = iv_byte;
                for i in 1..BLOCK_SIZE {
                    let before = self.decrypt_iv[i];
                    self.decrypt_iv[i] = before.wrapping_sub(1);
                    if before != 0 {
                        break;
                    }
                }
                restore = true;
            } else if ivb > cur && diff > 0 {
                // Lost a few packets.
                lost = ivb - cur - 1;
                self.decrypt_iv[0] = iv_byte;
            } else if ivb < cur && diff > 0 {
                // Lost a few packets and wrapped around.
                lost = 256 - cur + ivb - 1;
                self.decrypt_iv[0] = iv_byte;
                for i in 1..BLOCK_SIZE {
                    self.decrypt_iv[i] = self.decrypt_iv[i].wrapping_add(1);
                    if self.decrypt_iv[i] != 0 {
                        break;
                    }
                }
            } else {
                return None;
            }

            if self.decrypt_history[self.decrypt_iv[0] as usize] == self.decrypt_iv[1] {
                // Replay.
                self.decrypt_iv = save_iv;
                return None;
            }
        }

        let mut tag = [0u8; BLOCK_SIZE];
        let ok = ocb_decrypt(cipher, &source[OVERHEAD..], &mut out[..plain_len], &self.decrypt_iv, &mut tag);

        if !ok || tag[..3] != source[1..4] {
            self.decrypt_iv = save_iv;
            return None;
        }

        self.decrypt_history[self.decrypt_iv[0] as usize] = self.decrypt_iv[1];
        if restore {
            self.decrypt_iv = save_iv;
        }

        self.local.good = self.local.good.wrapping_add(1);
        // Guard against wrapping below zero, exactly like upstream.
        if late > 0 || self.local.late as i64 + late as i64 >= 0 {
            self.local.late = (self.local.late as i64 + late as i64) as u32;
        }
        if lost > 0 || self.local.lost as i64 + lost as i64 >= 0 {
            self.local.lost = (self.local.lost as i64 + lost as i64) as u32;
        }
        Some(plain_len)
    }

    /// Convenience wrapper that allocates the output buffer.
    pub fn decrypt_to_vec(&mut self, source: &[u8]) -> Option<Vec<u8>> {
        if source.len() < OVERHEAD {
            return None;
        }
        let mut out = vec![0u8; source.len() - OVERHEAD];
        self.decrypt(source, &mut out).map(|_| out)
    }
}

/// OCB2 encryption. `modify_plain_on_xex_star_attack` mirrors upstream (always true in production).
fn ocb_encrypt(
    cipher: &Aes128,
    plain: &[u8],
    encrypted: &mut [u8],
    nonce: &Block,
    tag: &mut Block,
    modify_plain_on_xex_star_attack: bool,
) -> bool {
    let mut success = true;
    let mut delta = CryptState::aes_encrypt(cipher, nonce);
    let mut checksum = [0u8; BLOCK_SIZE];
    let mut tmp = [0u8; BLOCK_SIZE];

    let mut len = plain.len();
    let mut off = 0;

    while len > BLOCK_SIZE {
        let block: &Block = plain[off..off + BLOCK_SIZE].try_into().unwrap();
        let mut flip_a_bit = false;
        if len - BLOCK_SIZE <= BLOCK_SIZE {
            let sum = block[..BLOCK_SIZE - 1].iter().fold(0u8, |acc, b| acc | b);
            if sum == 0 {
                if modify_plain_on_xex_star_attack {
                    flip_a_bit = true;
                } else {
                    success = false;
                }
            }
        }

        s2(&mut delta);
        xor(&mut tmp, &delta, block);
        if flip_a_bit {
            tmp[0] ^= 1;
        }
        tmp = CryptState::aes_encrypt(cipher, &tmp);
        let mut enc = [0u8; BLOCK_SIZE];
        xor(&mut enc, &delta, &tmp);
        encrypted[off..off + BLOCK_SIZE].copy_from_slice(&enc);
        let c = checksum;
        xor(&mut checksum, &c, block);
        if flip_a_bit {
            checksum[0] ^= 1;
        }

        len -= BLOCK_SIZE;
        off += BLOCK_SIZE;
    }

    s2(&mut delta);
    tmp = [0u8; BLOCK_SIZE];
    tmp[8..].copy_from_slice(&((len as u64) * 8).to_be_bytes());
    let t = tmp;
    xor(&mut tmp, &t, &delta);
    let pad = CryptState::aes_encrypt(cipher, &tmp);
    tmp[..len].copy_from_slice(&plain[off..off + len]);
    tmp[len..].copy_from_slice(&pad[len..]);
    let c = checksum;
    xor(&mut checksum, &c, &tmp);
    let t = tmp;
    xor(&mut tmp, &pad, &t);
    encrypted[off..off + len].copy_from_slice(&tmp[..len]);

    s3(&mut delta);
    xor(&mut tmp, &delta, &checksum);
    *tag = CryptState::aes_encrypt(cipher, &tmp);

    success
}

/// OCB2 decryption. Returns false if the XEX* attack pattern is detected.
fn ocb_decrypt(cipher: &Aes128, encrypted: &[u8], plain: &mut [u8], nonce: &Block, tag: &mut Block) -> bool {
    let mut success = true;
    let mut delta = CryptState::aes_encrypt(cipher, nonce);
    let mut checksum = [0u8; BLOCK_SIZE];
    let mut tmp = [0u8; BLOCK_SIZE];

    let mut len = encrypted.len();
    let mut off = 0;

    while len > BLOCK_SIZE {
        let block: &Block = encrypted[off..off + BLOCK_SIZE].try_into().unwrap();
        s2(&mut delta);
        xor(&mut tmp, &delta, block);
        tmp = CryptState::aes_decrypt(cipher, &tmp);
        let mut p = [0u8; BLOCK_SIZE];
        xor(&mut p, &delta, &tmp);
        plain[off..off + BLOCK_SIZE].copy_from_slice(&p);
        let c = checksum;
        xor(&mut checksum, &c, &p);
        len -= BLOCK_SIZE;
        off += BLOCK_SIZE;
    }

    s2(&mut delta);
    tmp = [0u8; BLOCK_SIZE];
    tmp[8..].copy_from_slice(&((len as u64) * 8).to_be_bytes());
    let t = tmp;
    xor(&mut tmp, &t, &delta);
    let pad = CryptState::aes_encrypt(cipher, &tmp);
    tmp = [0u8; BLOCK_SIZE];
    tmp[..len].copy_from_slice(&encrypted[off..off + len]);
    let t = tmp;
    xor(&mut tmp, &t, &pad);
    let c = checksum;
    xor(&mut checksum, &c, &tmp);
    plain[off..off + len].copy_from_slice(&tmp[..len]);

    // XEX* attack countermeasure: the last block must not equal `delta ^ len`.
    if tmp[..BLOCK_SIZE - 1] == delta[..BLOCK_SIZE - 1] {
        success = false;
    }

    s3(&mut delta);
    xor(&mut tmp, &delta, &checksum);
    *tag = CryptState::aes_encrypt(cipher, &tmp);

    success
}

#[cfg(test)]
mod tests {
    use super::*;

    fn seq(n: usize) -> Vec<u8> {
        (0..n as u8).collect()
    }

    /// Test vectors from the OCB2 draft (also used by Mumble's TestCrypt).
    #[test]
    fn ocb2_test_vectors() {
        let key: Block = seq(16).try_into().unwrap();
        let nonce: Block = key;
        let cipher = Aes128::new(&Array::from(key));

        let mut tag = [0u8; BLOCK_SIZE];
        let mut empty: [u8; 0] = [];
        assert!(ocb_encrypt(&cipher, &[], &mut empty, &nonce, &mut tag, true));
        assert_eq!(
            tag,
            [0xBF, 0x31, 0x08, 0x13, 0x07, 0x73, 0xAD, 0x5E, 0xC7, 0x0E, 0xC6, 0x9E, 0x78, 0x75, 0xA7, 0xB0]
        );

        let plain = seq(40);
        let mut crypted = [0u8; 40];
        assert!(ocb_encrypt(&cipher, &plain, &mut crypted, &nonce, &mut tag, true));
        assert_eq!(
            tag,
            [0x9D, 0xB0, 0xCD, 0xF8, 0x80, 0xF7, 0x3E, 0x3E, 0x10, 0xD4, 0xEB, 0x32, 0x17, 0x76, 0x66, 0x88]
        );
        assert_eq!(
            crypted,
            [
                0xF7, 0x5D, 0x6B, 0xC8, 0xB4, 0xDC, 0x8D, 0x66, 0xB8, 0x36, 0xA2, 0xB0, 0x8B, 0x32, 0xA6, 0x36,
                0x9F, 0x1C, 0xD3, 0xC5, 0x22, 0x8D, 0x79, 0xFD, 0x6C, 0x26, 0x7F, 0x5F, 0x6A, 0xA7, 0xB2, 0x31,
                0xC7, 0xDF, 0xB9, 0xD5, 0x99, 0x51, 0xAE, 0x9C
            ]
        );

        let mut decrypted = [0u8; 40];
        let mut tag2 = [0u8; BLOCK_SIZE];
        assert!(ocb_decrypt(&cipher, &crypted, &mut decrypted, &nonce, &mut tag2));
        assert_eq!(tag, tag2);
        assert_eq!(&decrypted[..], &plain[..]);
    }

    fn pair() -> (CryptState, CryptState) {
        let key = [7u8; 16];
        let iv_a = [1u8; 16];
        let iv_b = [9u8; 16];
        let mut client = CryptState::new();
        let mut server = CryptState::new();
        client.set_key(&key, &iv_a, &iv_b);
        server.set_key(&key, &iv_b, &iv_a);
        (client, server)
    }

    #[test]
    fn roundtrip_many_sizes() {
        let (mut client, mut server) = pair();
        for size in [0usize, 1, 15, 16, 17, 31, 32, 33, 100, 1000] {
            let data: Vec<u8> = (0..size).map(|i| (i * 31) as u8).collect();
            let enc = client.encrypt_to_vec(&data).unwrap();
            let dec = server.decrypt_to_vec(&enc).unwrap();
            assert_eq!(dec, data);
        }
        assert_eq!(server.local.good, 10);
    }

    #[test]
    fn detects_tamper_and_replay() {
        let (mut client, mut server) = pair();
        let enc = client.encrypt_to_vec(b"hello mumble").unwrap();
        let mut bad = enc.clone();
        bad[6] ^= 0x40;
        assert!(server.decrypt_to_vec(&bad).is_none());
        assert!(server.decrypt_to_vec(&enc).is_some());
        assert!(server.decrypt_to_vec(&enc).is_none(), "replay must be rejected");
    }

    #[test]
    fn handles_loss_and_reordering() {
        let (mut client, mut server) = pair();
        let packets: Vec<Vec<u8>> = (0..300).map(|i| client.encrypt_to_vec(&[i as u8; 20]).unwrap()).collect();
        // Deliver with gaps and swaps across the IV wraparound at 256.
        let mut order: Vec<usize> = (0..300).collect();
        order.swap(10, 11);
        order.swap(254, 256);
        order.retain(|i| i % 50 != 7);
        for i in order {
            assert!(server.decrypt_to_vec(&packets[i]).is_some(), "packet {i}");
        }
        assert!(server.local.late >= 2);
        assert!(server.local.lost >= 5);
    }

    #[test]
    fn xex_star_silence_blocks_roundtrip() {
        // Digital silence triggers the countermeasure path on encrypt; decrypt must still succeed.
        let (mut client, mut server) = pair();
        let mut data = vec![0u8; 32];
        data[31] = 5;
        let enc = client.encrypt_to_vec(&data).unwrap();
        assert!(server.decrypt_to_vec(&enc).is_some());
    }
}
