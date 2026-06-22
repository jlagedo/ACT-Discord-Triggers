// ITU-R BS.1770 K-weighting — the loudness measure behind LUFS. Two biquad
// pre-filters (a high-frequency "head" shelf + a ~38 Hz high-pass) shape the
// signal to track perceived loudness before the mean-square: low bass is
// discounted (we hear it as quieter than its energy) and the presence region is
// lifted. `normalize` measures clips with this instead of broadband RMS so a
// bass-heavy SFX and a speech callout that read equally loud also *sound*
// equally loud.
//
// Measurement-only and self-contained: each call filters a whole interleaved
// float32 stereo buffer from a zeroed state, so there is no cross-call state to
// carry (the brief filter warm-up is a negligible fraction of any real clip).
//
// Coefficients are the canonical BS.1770-4 values at 48 kHz (the bridge's fixed
// rate) — do not reuse at any other sample rate.

const SR = 48000;

// Biquad (transposed direct-form II), denominator normalized to a0 = 1. The
// recurrence per sample x:
//   y  = b0*x + z1
//   z1 = b1*x - a1*y + z2
//   z2 = b2*x - a2*y
interface BiquadCoeffs {
    b0: number; b1: number; b2: number;
    a1: number; a2: number;
}

// Stage 1 — high-shelf "head" filter (+~4 dB above ~1.5 kHz).
const STAGE1: BiquadCoeffs = {
    b0: 1.53512485958697,
    b1: -2.69169618940638,
    b2: 1.19839281085285,
    a1: -1.69065929318241,
    a2: 0.73248077421585,
};

// Stage 2 — RLB high-pass (~38 Hz). The near-unit pole (a2 ≈ 0.990) makes this a
// long but stable impulse; keep full precision or the cutoff drifts.
const STAGE2: BiquadCoeffs = {
    b0: 1.0,
    b1: -2.0,
    b2: 1.0,
    a1: -1.99004745483398,
    a2: 0.99007225036621,
};

// BS.1770 absolute-scale calibration constant (LKFS offset).
const ABSOLUTE_OFFSET_DB = -0.691;

// One stateful biquad. Self-contained per measurement pass; constructed fresh so
// the filter starts from rest (z1 = z2 = 0).
class Biquad {
    private z1 = 0;
    private z2 = 0;
    constructor(private readonly c: BiquadCoeffs) {}

    process(x: number): number {
        const { b0, b1, b2, a1, a2 } = this.c;
        const y = b0 * x + this.z1;
        this.z1 = b1 * x - a1 * y + this.z2;
        this.z2 = b2 * x - a2 * y;
        return y;
    }
}

// K-weighted mean square of one de-interleaved channel: cascade stage1 -> stage2,
// accumulate the squared output. Returns mean(y^2) over the channel's frames.
function channelMeanSquare(samples: Float32Array, channel: 0 | 1, frames: number): number {
    if (frames === 0) return 0;
    const s1 = new Biquad(STAGE1);
    const s2 = new Biquad(STAGE2);
    let sumSq = 0;
    for (let i = 0; i < frames; i++) {
        const x = samples[i * 2 + channel]!;
        const y = s2.process(s1.process(x));
        sumSq += y * y;
    }
    return sumSq / frames;
}

// K-weighted loudness of an interleaved float32 stereo buffer, in LUFS (a
// negative value for normal program; can exceed 0 for correlated full-scale
// content). Per BS.1770 the two channels are summed with unit weight:
//   L_K = -0.691 + 10*log10(msL + msR)
// Ungated (no 400 ms-block / -70/-10 LU gating) — appropriate for short per-clip
// leveling, where each clip is one coherent gain decision. Returns -Infinity for a
// silent buffer; DC and subsonic content read far below their broadband energy,
// since the high-pass removes the steady component (only the settling transient
// remains) — which is correct, that energy is inaudible.
export function kWeightedLoudnessLufs(samples: Float32Array): number {
    const frames = samples.length >> 1; // interleaved stereo: 2 samples per frame
    if (frames === 0) return -Infinity;
    const msL = channelMeanSquare(samples, 0, frames);
    const msR = channelMeanSquare(samples, 1, frames);
    const sum = msL + msR;
    if (sum <= 0) return -Infinity;
    return ABSOLUTE_OFFSET_DB + 10 * Math.log10(sum);
}

// Magnitude (linear) of the K-weighting cascade at frequency `f` Hz — the
// analytic |H1(e^jw)|*|H2(e^jw)|. Exposed for tests so the tonal-invariant
// assertions can check the filter's response without synthesizing a tone.
export function kWeightingMagnitude(f: number): number {
    const w = (2 * Math.PI * f) / SR;
    const mag = (c: BiquadCoeffs): number => {
        const cos1 = Math.cos(w), cos2 = Math.cos(2 * w);
        const sin1 = Math.sin(w), sin2 = Math.sin(2 * w);
        // Numerator/denominator as complex sums of b/a * e^{-jkw}.
        const numRe = c.b0 + c.b1 * cos1 + c.b2 * cos2;
        const numIm = -(c.b1 * sin1 + c.b2 * sin2);
        const denRe = 1 + c.a1 * cos1 + c.a2 * cos2;
        const denIm = -(c.a1 * sin1 + c.a2 * sin2);
        const num = Math.hypot(numRe, numIm);
        const den = Math.hypot(denRe, denIm);
        return num / den;
    };
    return mag(STAGE1) * mag(STAGE2);
}
