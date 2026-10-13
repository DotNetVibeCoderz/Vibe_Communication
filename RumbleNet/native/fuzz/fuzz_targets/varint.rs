#![no_main]
//! Mumble varints: decoding arbitrary bytes must not panic, and whatever decodes must
//! re-encode to the same value.
use libfuzzer_sys::fuzz_target;
use rumble_protocol::varint;

fuzz_target!(|data: &[u8]| {
    let mut rest = data;
    while let Ok((value, used)) = varint::decode(rest) {
        let mut buf = [0u8; varint::MAX_VARINT_LEN];
        let written = varint::encode(value, &mut buf);
        let (round, _) = varint::decode(&buf[..written]).expect("re-encoded varint must decode");
        assert_eq!(value, round, "varint round-trip changed the value");
        if used == 0 || used > rest.len() {
            break;
        }
        rest = &rest[used..];
    }
});
