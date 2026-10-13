#![no_main]
//! Attacker-controlled UDP: arbitrary ciphertext must be rejected without panicking, and a
//! genuine encrypt/decrypt round trip must return the original plaintext.
use libfuzzer_sys::fuzz_target;
use rumble_protocol::crypt::CryptState;

fuzz_target!(|data: &[u8]| {
    let mut receiver = CryptState::new();
    receiver.set_key(&[0x42; 16], &[1; 16], &[2; 16]);
    let mut plain = vec![0u8; data.len().max(1)];
    let _ = receiver.decrypt(data, &mut plain);

    // The honest path, with the same bytes as plaintext.
    if data.len() <= 1024 {
        let mut sender = CryptState::new();
        let mut peer = CryptState::new();
        sender.set_key(&[0x42; 16], &[3; 16], &[4; 16]);
        peer.set_key(&[0x42; 16], &[4; 16], &[3; 16]);
        if let Some(encrypted) = sender.encrypt_to_vec(data) {
            let decrypted = peer.decrypt_to_vec(&encrypted).expect("honest packet must decrypt");
            assert_eq!(decrypted.len(), data.len(), "round trip changed the length");

            // Digital silence makes the sender flip one bit (the XEX* countermeasure), so the
            // payload may differ in exactly that bit and nowhere else.
            let changed: Vec<u8> = decrypted.iter().zip(data).map(|(a, b)| a ^ b).collect();
            let flipped: usize = changed.iter().map(|b| b.count_ones() as usize).sum();
            assert!(flipped <= 1, "round trip changed {flipped} bits");
            assert!(changed.iter().all(|b| *b == 0 || *b == 1), "unexpected bit changed");
        }
    }
});
