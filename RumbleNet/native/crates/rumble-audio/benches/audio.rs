use std::hint::black_box;

use bytes::Bytes;
use criterion::{BenchmarkId, Criterion, criterion_group, criterion_main};
use rumble_audio::FRAME_SIZE;
use rumble_audio::codec::{OpusApplication, OpusDecoder, OpusEncoder, StreamCodec};
use rumble_audio::jitter::{BufferedPacket, JitterBuffer, JitterConfig};
use rumble_audio::mixer::{self, IncomingVoice, MixerConfig};
use rumble_audio::positional::{Listener, PositionalSettings, StereoGain, compute_gains};

fn tone() -> Vec<f32> {
    (0..FRAME_SIZE).map(|i| (i as f32 * 0.0575).sin() * 0.4).collect()
}

fn opus(c: &mut Criterion) {
    let frame = tone();
    let mut encoder = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
    encoder.set_bitrate(48_000).unwrap();
    let mut packet = [0u8; 1500];
    c.bench_function("opus_encode_10ms", |b| b.iter(|| encoder.encode_float(black_box(&frame), &mut packet).unwrap()));

    let n = encoder.encode_float(&frame, &mut packet).unwrap();
    let mut decoder = OpusDecoder::new(1).unwrap();
    let mut pcm = vec![0f32; 5760];
    c.bench_function("opus_decode_10ms", |b| b.iter(|| decoder.decode_float(black_box(&packet[..n]), &mut pcm).unwrap()));
    c.bench_function("opus_conceal_10ms", |b| b.iter(|| decoder.conceal(&mut pcm[..FRAME_SIZE]).unwrap()));
}

fn jitter(c: &mut Criterion) {
    c.bench_function("jitter_insert_pop_1000", |b| {
        b.iter(|| {
            let mut jb = JitterBuffer::new(JitterConfig::default());
            let payload = Bytes::from_static(&[0u8; 64]);
            for seq in 0..1000u64 {
                // Swap neighbours to exercise reordering.
                let s = if seq % 10 == 3 { seq + 1 } else if seq % 10 == 4 { seq - 1 } else { seq };
                jb.insert(
                    BufferedPacket { sequence: s, frames: 1, payload: payload.clone(), position: None, is_terminator: false, volume_adjustment: 0.0 },
                    seq as i64 * 10_000,
                );
                black_box(jb.pop());
            }
        })
    });
}

fn mixing(c: &mut Criterion) {
    let frame = tone();
    let mut encoder = OpusEncoder::new(1, OpusApplication::Voip).unwrap();
    let mut buf = [0u8; 1500];
    let n = encoder.encode_float(&frame, &mut buf).unwrap();
    let payload = Bytes::copy_from_slice(&buf[..n]);

    let mut group = c.benchmark_group("mixer_10ms");
    for speakers in [1u32, 8, 32] {
        group.bench_with_input(BenchmarkId::from_parameter(speakers), &speakers, |b, &speakers| {
            let (mut router, mut mix) = mixer::channel(MixerConfig::default());
            router.set_positional_enabled(true);
            let mut out = vec![0f32; FRAME_SIZE * 2];
            let mut seq = 0u64;
            b.iter(|| {
                for s in 0..speakers {
                    router.route(
                        s + 1,
                        IncomingVoice {
                            codec: StreamCodec::Opus,
                            sequence: seq,
                            payload: payload.clone(),
                            position: Some([s as f32, 0.0, 3.0]),
                            is_terminator: false,
                            volume_adjustment: 0.0,
                            arrival_us: seq as i64 * 10_000,
                        },
                    );
                }
                seq += 1;
                black_box(mix.mix(&mut out, 2))
            })
        });
    }
    group.finish();
}

fn positional(c: &mut Criterion) {
    let positions: Vec<[f32; 3]> = (0..256).map(|i| [(i % 16) as f32 - 8.0, 0.0, (i / 16) as f32 - 8.0]).collect();
    let mut gains = vec![StereoGain::default(); positions.len()];
    let listener = Listener::default();
    let settings = PositionalSettings::default();
    c.bench_function("positional_gains_256_sources", |b| {
        b.iter(|| compute_gains(black_box(&listener), &settings, &positions, &mut gains))
    });
}

criterion_group!(benches, opus, jitter, mixing, positional);
criterion_main!(benches);
