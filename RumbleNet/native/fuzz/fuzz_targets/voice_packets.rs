#![no_main]
//! Decodes arbitrary datagrams in both UDP formats and from both directions.
use libfuzzer_sys::fuzz_target;
use rumble_protocol::voice::{self, VoiceFormat};

fuzz_target!(|data: &[u8]| {
    for format in [VoiceFormat::Legacy, VoiceFormat::Protobuf] {
        for from_server in [true, false] {
            if let Ok(packet) = voice::decode_udp(data, format, from_server) {
                // Re-encoding a decoded packet must also stay panic-free.
                if let voice::UdpPacket::Voice(v) = packet {
                    let mut out = Vec::new();
                    voice::encode_voice(&v, format, from_server, &mut out);
                }
            }
        }
    }
    let _ = voice::decode_server_info_response(data);
});
