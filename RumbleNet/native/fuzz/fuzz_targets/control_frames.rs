#![no_main]
//! Feeds arbitrary bytes into the TCP control-channel decoder, in chunks, the way a hostile
//! server could. The decoder must never panic, and must not over-allocate on huge length fields.
use libfuzzer_sys::fuzz_target;
use rumble_protocol::control::FrameDecoder;

fuzz_target!(|data: &[u8]| {
    let mut decoder = FrameDecoder::new();
    for chunk in data.chunks(7.max(data.len() / 8 + 1)) {
        decoder.extend(chunk);
        loop {
            match decoder.next_message() {
                Ok(Some(_)) => continue,
                Ok(None) => break,
                Err(_) => return,
            }
        }
    }
});
