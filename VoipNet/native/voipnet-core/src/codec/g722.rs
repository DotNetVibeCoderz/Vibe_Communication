//! ITU-T G.722 wideband SB-ADPCM codec at 64 kbit/s.
//!
//! Fixed-point implementation following the ITU-T reference algorithm
//! (same structure as the public-domain CMU/spandsp implementation).

use super::AudioCodec;

const QMF_COEFFS: [i32; 12] = [3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11];
const Q6: [i32; 32] = [
    0, 35, 72, 110, 150, 190, 233, 276, 323, 370, 422, 473, 530, 587, 650, 714, 786, 858, 940, 1023, 1121, 1219, 1339,
    1458, 1612, 1765, 1980, 2195, 2557, 2919, 0, 0,
];
const ILN: [i32; 32] = [
    0, 63, 62, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 0,
];
const ILP: [i32; 32] = [
    0, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51, 50, 49, 48, 47, 46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33,
    32, 0,
];
const WL: [i32; 8] = [-60, -30, 58, 172, 334, 538, 1198, 3042];
const RL42: [i32; 16] = [0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3, 2, 1, 0];
const ILB: [i32; 32] = [
    2048, 2093, 2139, 2186, 2233, 2282, 2332, 2383, 2435, 2489, 2543, 2599, 2656, 2714, 2774, 2834, 2896, 2960, 3025, 3091,
    3158, 3228, 3298, 3371, 3444, 3520, 3597, 3676, 3756, 3838, 3922, 4008,
];
const QM4: [i32; 16] =
    [0, -20456, -12896, -8968, -6288, -4240, -2584, -1200, 20456, 12896, 8968, 6288, 4240, 2584, 1200, 0];
const QM6: [i32; 64] = [
    -136, -136, -136, -136, -24808, -21904, -19008, -16704, -14984, -13512, -12280, -11192, -10232, -9360, -8576, -7856,
    -7192, -6576, -6000, -5456, -4944, -4464, -4008, -3576, -3168, -2776, -2400, -2032, -1688, -1360, -1040, -728, 24808,
    21904, 19008, 16704, 14984, 13512, 12280, 11192, 10232, 9360, 8576, 7856, 7192, 6576, 6000, 5456, 4944, 4464, 4008,
    3576, 3168, 2776, 2400, 2032, 1688, 1360, 1040, 728, 432, 136, -432, -136,
];
const QM2: [i32; 4] = [-7408, -1616, 7408, 1616];
const IHN: [i32; 3] = [0, 1, 0];
const IHP: [i32; 3] = [0, 3, 2];
const WH: [i32; 3] = [0, -214, 798];
const RH2: [i32; 4] = [2, 1, 2, 1];

#[inline(always)]
fn saturate(v: i32) -> i32 {
    v.clamp(-32768, 32767)
}

#[derive(Clone, Default)]
struct Band {
    s: i32,
    sp: i32,
    sz: i32,
    r: [i32; 3],
    a: [i32; 3],
    ap: [i32; 3],
    p: [i32; 3],
    d: [i32; 7],
    b: [i32; 7],
    bp: [i32; 7],
    sg: [i32; 7],
    nb: i32,
    det: i32,
}

impl Band {
    fn block4(&mut self, d: i32) {
        self.d[0] = d;
        self.r[0] = saturate(self.s + d);
        self.p[0] = saturate(self.sz + d);

        for i in 0..3 {
            self.sg[i] = self.p[i] >> 15;
        }
        let wd1 = saturate(self.a[1] << 2);
        let mut wd2 = if self.sg[0] == self.sg[1] { -wd1 } else { wd1 };
        if wd2 > 32767 {
            wd2 = 32767;
        }
        let mut wd3 = (wd2 >> 7) + if self.sg[0] == self.sg[2] { 128 } else { -128 };
        wd3 += (self.a[2] * 32512) >> 15;
        self.ap[2] = wd3.clamp(-12288, 12288);

        self.sg[0] = self.p[0] >> 15;
        self.sg[1] = self.p[1] >> 15;
        let wd1 = if self.sg[0] == self.sg[1] { 192 } else { -192 };
        let wd2 = (self.a[1] * 32640) >> 15;
        self.ap[1] = saturate(wd1 + wd2);
        let wd3 = saturate(15360 - self.ap[2]);
        self.ap[1] = self.ap[1].clamp(-wd3, wd3);

        let wd1 = if d == 0 { 0 } else { 128 };
        self.sg[0] = d >> 15;
        for i in 1..7 {
            self.sg[i] = self.d[i] >> 15;
            let wd2 = if self.sg[i] == self.sg[0] { wd1 } else { -wd1 };
            let wd3 = (self.b[i] * 32640) >> 15;
            self.bp[i] = saturate(wd2 + wd3);
        }

        for i in (1..7).rev() {
            self.d[i] = self.d[i - 1];
            self.b[i] = self.bp[i];
        }
        for i in (1..3).rev() {
            self.r[i] = self.r[i - 1];
            self.p[i] = self.p[i - 1];
            self.a[i] = self.ap[i];
        }

        let wd1 = (self.a[1] * saturate(self.r[1] + self.r[1])) >> 15;
        let wd2 = (self.a[2] * saturate(self.r[2] + self.r[2])) >> 15;
        self.sp = saturate(wd1 + wd2);

        let mut sz = 0;
        for i in (1..7).rev() {
            sz += (self.b[i] * saturate(self.d[i] + self.d[i])) >> 15;
        }
        self.sz = saturate(sz);
        self.s = saturate(self.sp + self.sz);
    }

    fn scale(&mut self, shift_base: i32) {
        let wd1 = ((self.nb >> 6) & 31) as usize;
        let wd2 = shift_base - (self.nb >> 11);
        let wd3 = if wd2 < 0 { ILB[wd1] << -wd2 } else { ILB[wd1] >> wd2 };
        self.det = wd3 << 2;
    }
}

fn new_bands() -> [Band; 2] {
    let mut bands = [Band::default(), Band::default()];
    bands[0].det = 32;
    bands[1].det = 8;
    bands
}

pub struct G722Encoder {
    x: [i32; 24],
    band: [Band; 2],
}

impl Default for G722Encoder {
    fn default() -> Self {
        Self { x: [0; 24], band: new_bands() }
    }
}

impl G722Encoder {
    /// Encodes 16 kHz PCM. An odd trailing sample is ignored.
    pub fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>) {
        out.reserve(pcm.len() / 2);
        for pair in pcm.chunks_exact(2) {
            self.x.copy_within(2..24, 0);
            self.x[22] = pair[0] as i32;
            self.x[23] = pair[1] as i32;

            let (mut sumeven, mut sumodd) = (0i32, 0i32);
            for i in 0..12 {
                sumodd += self.x[2 * i] * QMF_COEFFS[i];
                sumeven += self.x[2 * i + 1] * QMF_COEFFS[11 - i];
            }
            let xlow = (sumeven + sumodd) >> 14;
            let xhigh = (sumeven - sumodd) >> 14;

            // Low band.
            let lb = &mut self.band[0];
            let el = saturate(xlow - lb.s);
            let wd = if el >= 0 { el } else { -(el + 1) };
            let mut i = 1;
            while i < 30 {
                if wd < (Q6[i] * lb.det) >> 12 {
                    break;
                }
                i += 1;
            }
            let ilow = if el < 0 { ILN[i] } else { ILP[i] };
            let ril = (ilow >> 2) as usize;
            let dlow = (lb.det * QM4[ril]) >> 15;
            let il4 = RL42[ril] as usize;
            lb.nb = (((lb.nb * 127) >> 7) + WL[il4]).clamp(0, 18000);
            lb.scale(8);
            lb.block4(dlow);

            // High band.
            let hb = &mut self.band[1];
            let eh = saturate(xhigh - hb.s);
            let wd = if eh >= 0 { eh } else { -(eh + 1) };
            let mih = if wd >= (564 * hb.det) >> 12 { 2 } else { 1 };
            let ihigh = if eh < 0 { IHN[mih] } else { IHP[mih] };
            let dhigh = (hb.det * QM2[ihigh as usize]) >> 15;
            let ih2 = RH2[ihigh as usize] as usize;
            hb.nb = (((hb.nb * 127) >> 7) + WH[ih2]).clamp(0, 22000);
            hb.scale(10);
            hb.block4(dhigh);

            out.push(((ihigh << 6) | ilow) as u8);
        }
    }
}

pub struct G722Decoder {
    x: [i32; 24],
    band: [Band; 2],
}

impl Default for G722Decoder {
    fn default() -> Self {
        Self { x: [0; 24], band: new_bands() }
    }
}

impl G722Decoder {
    pub fn decode(&mut self, data: &[u8], out: &mut Vec<i16>) {
        out.reserve(data.len() * 2);
        for &code in data {
            let code = code as i32;
            let ilow = (code & 0x3F) as usize;
            let ihigh = ((code >> 6) & 0x03) as usize;

            let lb = &mut self.band[0];
            let wd2 = (lb.det * QM6[ilow]) >> 15;
            let rlow = (lb.s + wd2).clamp(-16384, 16383);
            let ril = ilow >> 2;
            let dlowt = (lb.det * QM4[ril]) >> 15;
            lb.nb = (((lb.nb * 127) >> 7) + WL[RL42[ril] as usize]).clamp(0, 18000);
            lb.scale(8);
            lb.block4(dlowt);

            let hb = &mut self.band[1];
            let dhigh = (hb.det * QM2[ihigh]) >> 15;
            let rhigh = (dhigh + hb.s).clamp(-16384, 16383);
            hb.nb = (((hb.nb * 127) >> 7) + WH[RH2[ihigh] as usize]).clamp(0, 22000);
            hb.scale(10);
            hb.block4(dhigh);

            self.x.copy_within(2..24, 0);
            self.x[22] = rlow + rhigh;
            self.x[23] = rlow - rhigh;
            let (mut xout1, mut xout2) = (0i32, 0i32);
            for i in 0..12 {
                xout2 += self.x[2 * i] * QMF_COEFFS[i];
                xout1 += self.x[2 * i + 1] * QMF_COEFFS[11 - i];
            }
            out.push(saturate(xout1 >> 11) as i16);
            out.push(saturate(xout2 >> 11) as i16);
        }
    }
}

/// G.722 codec. Note the RTP clock rate is 8000 for historical reasons (RFC 3551 §4.5.2)
/// while the audio sample rate is 16 kHz.
#[derive(Default)]
pub struct G722 {
    encoder: G722Encoder,
    decoder: G722Decoder,
}

impl AudioCodec for G722 {
    fn name(&self) -> &'static str {
        "G722"
    }
    fn payload_type(&self) -> u8 {
        9
    }
    fn clock_rate(&self) -> u32 {
        8000
    }
    fn sample_rate(&self) -> u32 {
        16000
    }
    fn encode(&mut self, pcm: &[i16], out: &mut Vec<u8>) {
        self.encoder.encode(pcm, out)
    }
    fn decode(&mut self, payload: &[u8], out: &mut Vec<i16>) {
        self.decoder.decode(payload, out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn snr_best_lag(reference: &[i16], decoded: &[i16]) -> f64 {
        let mut best = f64::MIN;
        for lag in 0..64 {
            let n = reference.len().min(decoded.len().saturating_sub(lag));
            let skip = 800; // skip adaptation warm-up
            let (mut sig, mut noise) = (0f64, 0f64);
            for i in skip..n {
                let r = reference[i] as f64;
                let d = decoded[i + lag] as f64;
                sig += r * r;
                noise += (r - d) * (r - d);
            }
            best = best.max(10.0 * (sig / noise.max(1.0)).log10());
        }
        best
    }

    #[test]
    fn roundtrip_sine_has_good_snr() {
        let pcm: Vec<i16> = (0..16000)
            .map(|i| {
                let t = i as f64 / 16000.0;
                (8000.0 * (2.0 * std::f64::consts::PI * 440.0 * t).sin()
                    + 3000.0 * (2.0 * std::f64::consts::PI * 1800.0 * t).sin()) as i16
            })
            .collect();
        let mut enc = G722Encoder::default();
        let mut dec = G722Decoder::default();
        let mut bytes = Vec::new();
        enc.encode(&pcm, &mut bytes);
        assert_eq!(bytes.len(), 8000);
        let mut out = Vec::new();
        dec.decode(&bytes, &mut out);
        assert_eq!(out.len(), 16000);
        let snr = snr_best_lag(&pcm, &out);
        assert!(snr > 15.0, "G.722 SNR too low: {snr:.1} dB");
    }
}
