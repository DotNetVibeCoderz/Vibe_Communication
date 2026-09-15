use std::hint::black_box;

use bytes::{Bytes, BytesMut};
use criterion::{Criterion, Throughput, criterion_group, criterion_main};
use rumble_protocol::control::{ControlMessage, FrameDecoder};
use rumble_protocol::crypt::CryptState;
use rumble_protocol::proto;
use rumble_protocol::varint;
use rumble_protocol::voice::{self, VoiceFormat, VoicePacket};

fn crypt(c: &mut Criterion) {
    let mut group = c.benchmark_group("ocb2_aes128");
    for size in [60usize, 120, 480] {
        let mut a = CryptState::new();
        let mut b = CryptState::new();
        a.set_key(&[7; 16], &[1; 16], &[2; 16]);
        b.set_key(&[7; 16], &[2; 16], &[1; 16]);
        let plain = vec![0x5Au8; size];
        let mut enc = vec![0u8; size + 4];
        let mut dec = vec![0u8; size];
        group.throughput(Throughput::Bytes(size as u64));
        group.bench_function(format!("encrypt_decrypt_{size}b"), |bench| {
            bench.iter(|| {
                let n = a.encrypt(black_box(&plain), &mut enc).unwrap();
                b.decrypt(&enc[..n], &mut dec).unwrap()
            })
        });
    }
    group.finish();
}

fn varints(c: &mut Criterion) {
    let values: Vec<u64> = (0..1024u64).map(|i| i.wrapping_mul(0x9E37_79B9_7F4A_7C15) >> (i % 64)).collect();
    let mut buf = [0u8; varint::MAX_VARINT_LEN];
    c.bench_function("varint_roundtrip_1024", |bench| {
        bench.iter(|| {
            let mut acc = 0u64;
            for v in &values {
                let n = varint::encode(*v, &mut buf);
                acc ^= varint::decode(&buf[..n]).unwrap().0;
            }
            black_box(acc)
        })
    });
}

fn voice_packets(c: &mut Criterion) {
    let packet = VoicePacket {
        session: Some(17),
        position: Some([1.0, 2.0, 3.0]),
        ..VoicePacket::opus(0, 123_456, Bytes::from(vec![0xAB; 96]), false)
    };
    let mut out = Vec::with_capacity(256);
    for format in [VoiceFormat::Legacy, VoiceFormat::Protobuf] {
        c.bench_function(&format!("voice_encode_decode_{format:?}"), |bench| {
            bench.iter(|| {
                voice::encode_voice(black_box(&packet), format, true, &mut out);
                black_box(voice::decode_udp(&out, format, true).unwrap())
            })
        });
    }
}

fn control_frames(c: &mut Criterion) {
    let mut wire = BytesMut::new();
    for i in 0..100u32 {
        ControlMessage::UserState(proto::UserState {
            session: Some(i),
            name: Some(format!("user{i}")),
            channel_id: Some(i % 7),
            self_mute: Some(i % 2 == 0),
            ..Default::default()
        })
        .encode(&mut wire);
    }
    let wire = wire.freeze();
    c.bench_function("control_decode_100_userstates", |bench| {
        bench.iter(|| {
            let mut decoder = FrameDecoder::new();
            decoder.extend(&wire);
            let mut count = 0;
            while decoder.next_message().unwrap().is_some() {
                count += 1;
            }
            black_box(count)
        })
    });
}

criterion_group!(benches, crypt, varints, voice_packets, control_frames);
criterion_main!(benches);
