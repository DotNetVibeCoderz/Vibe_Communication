//! Throughput of the hot media paths: one iteration is one 20 ms frame, so a result of 2 µs means
//! roughly 10 000 concurrent streams per core for that step.

use criterion::{criterion_group, criterion_main, Criterion};
use std::hint::black_box;

use voipnet_core::codec::{create_audio_codec, CodecKind};
use voipnet_core::media::conference::{Conference, CONFERENCE_RATE};
use voipnet_core::srtp::SrtpContext;

fn tone(rate: u32, ms: usize) -> Vec<i16> {
    (0..rate as usize * ms / 1000)
        .map(|i| (8000.0 * (i as f64 * 2.0 * std::f64::consts::PI * 440.0 / rate as f64).sin()) as i16)
        .collect()
}

fn codecs(c: &mut Criterion) {
    let mut group = c.benchmark_group("codec 20 ms frame");
    for kind in [CodecKind::G722, CodecKind::Pcmu, CodecKind::Opus] {
        let map = kind.rtpmap();
        let Some(mut encoder) = create_audio_codec(&map) else { continue };
        let Some(mut decoder) = create_audio_codec(&map) else { continue };
        let frame = tone(encoder.sample_rate(), 20);
        let mut encoded = Vec::with_capacity(2048);
        let mut decoded = Vec::with_capacity(4096);
        group.bench_function(format!("{} encode", map.encoding), |b| {
            b.iter(|| {
                encoded.clear();
                encoder.encode(black_box(&frame), &mut encoded);
            })
        });
        encoded.clear();
        encoder.encode(&frame, &mut encoded);
        group.bench_function(format!("{} decode", map.encoding), |b| {
            b.iter(|| {
                decoded.clear();
                decoder.decode(black_box(&encoded), &mut decoded);
            })
        });
    }
    group.finish();
}

fn conference(c: &mut Criterion) {
    let samples = CONFERENCE_RATE as usize * 20 / 1000;
    let frame = tone(CONFERENCE_RATE, 20);
    let mut group = c.benchmark_group("conference mix-minus 20 ms");
    for participants in [3u64, 10, 50] {
        let bridge = Conference::new();
        for id in 0..participants {
            bridge.join(id);
            bridge.contribute(id, &frame);
        }
        let mut out = Vec::with_capacity(samples);
        group.bench_function(format!("{participants} participants"), |b| {
            b.iter(|| {
                out.clear();
                bridge.mix_for(black_box(0), samples, &mut out);
            })
        });
    }
    group.finish();
}

fn srtp(c: &mut Criterion) {
    let payload = tone(16000, 20);
    let mut packet = vec![0x80, 9, 0, 1, 0, 0, 0, 160, 0xCA, 0xFE, 0xBA, 0xBE];
    packet.extend(payload.iter().flat_map(|s| s.to_be_bytes()));
    let (params, mut protect) = SrtpContext::generate();
    let mut unprotect = SrtpContext::from_sdes(&params).unwrap();
    let mut group = c.benchmark_group("srtp 20 ms packet");
    group.bench_function("protect", |b| {
        b.iter(|| {
            let mut p = packet.clone();
            protect.protect_rtp(black_box(&mut p)).unwrap();
        })
    });
    let mut sealed = packet.clone();
    protect.protect_rtp(&mut sealed).unwrap();
    group.bench_function("unprotect", |b| {
        b.iter(|| {
            let mut p = sealed.clone();
            // A replayed packet is rejected, so the counter moves on each iteration.
            let _ = unprotect.unprotect_rtp(black_box(&mut p));
        })
    });
    group.finish();
}

criterion_group!(benches, codecs, conference, srtp);
criterion_main!(benches);
