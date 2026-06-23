// Auto-leveling (normalize) convergence probe. The promise of auto-volume is that
// spectrally-different clips all land on ONE perceived loudness so the user isn't
// reaching for the volume knob between triggers. This feeds a battery of edge-case
// signals through the exact runtime normalize() and re-measures the output LUFS,
// reporting how far each lands from target and which constraint (target / peak
// ceiling / max-boost cap) bound the gain. It then runs the normalized clips through
// the real PcmMixer + limiter to confirm the end-to-end level is clean.
//
//   npm run normalize:e2e            (or: node --import tsx tools/normalize-e2e.ts)

import { normalize, measureLevel, linearToDb } from '../src/normalize.js';
import { kWeightedLoudnessLufs } from '../src/k-weighting.js';
import { PcmMixer } from '../src/pcm-mixer.js';

const SR = 48000;
const TARGET = -17; // default normalizeTarget (negated to LUFS)

// Build an interleaved float32 stereo buffer from a per-sample mono function.
function stereo(durSec: number, fn: (t: number, i: number) => number): Float32Array {
    const frames = Math.floor(durSec * SR);
    const out = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        const s = fn(i / SR, i);
        out[i * 2] = s;
        out[i * 2 + 1] = s;
    }
    return out;
}

const sine = (durSec: number, freq: number, amp: number) =>
    stereo(durSec, (t) => amp * Math.sin(2 * Math.PI * freq * t));

// Sparse loud impulses on a near-silent bed: high broadband peak, low average
// loudness — the classic case where the peak ceiling stops the boost.
function peakyImpulses(durSec: number, peakAmp: number, bedAmp: number): Float32Array {
    const frames = Math.floor(durSec * SR);
    const out = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        let s = bedAmp * Math.sin(2 * Math.PI * 1000 * (i / SR));
        if (i % (SR / 4) < 24) s = peakAmp; // a 0.5 ms spike 4x/second
        out[i * 2] = s;
        out[i * 2 + 1] = s;
    }
    return out;
}

interface Case { name: string; buf: Float32Array; }
const cases: Case[] = [
    { name: 'mid 1kHz  -10 dBFS (loud, on-band)', buf: sine(0.5, 1000, 0.3162) },
    { name: 'mid 1kHz  -30 dBFS (quiet, on-band)', buf: sine(0.5, 1000, 0.03162) },
    { name: 'mid 1kHz  -45 dBFS (very quiet)', buf: sine(0.5, 1000, 0.005623) },
    { name: 'full-scale 1kHz   0 dBFS (hot)', buf: sine(0.5, 1000, 0.999) },
    { name: 'bass 60Hz  -10 dBFS peak', buf: sine(0.5, 60, 0.3162) },
    { name: 'treble 9kHz -10 dBFS peak', buf: sine(0.5, 9000, 0.3162) },
    { name: 'peaky impulses (peak~0 dBFS, sparse)', buf: peakyImpulses(0.5, 0.98, 0.01) },
];

console.log(`Normalize convergence probe — target ${TARGET} LUFS (default)\n`);
console.log('  clip                                    inLUFS   gain(dB)  outLUFS   err   bound');
console.log('  ' + '-'.repeat(86));

let worstErr = 0;
const normalized: Float32Array[] = [];
for (const c of cases) {
    const { rms, peak } = measureLevel(c.buf);
    const inLufs = linearToDb(rms);
    const res = normalize(c.buf, TARGET);
    const out = res.applied ? res.samples : c.buf;
    normalized.push(out);
    const outLufs = kWeightedLoudnessLufs(out);
    const err = outLufs - TARGET;
    const gainDb = res.gain > 0 ? 20 * Math.log10(res.gain) : -Infinity;

    // Why did the gain land where it did?
    let bound = 'target';
    if (!res.applied && Math.abs(inLufs - TARGET) > 0.2) bound = 'SKIPPED'; // unexpected no-op
    else if (gainDb >= 11.95) bound = 'MAX-BOOST(+12)';
    else if (peak * res.gain >= 0.9695) bound = 'PEAK-CEILING';

    if (Math.abs(err) > Math.abs(worstErr)) worstErr = err;
    const flag = Math.abs(err) > 1.0 ? '  <== off target' : '';
    console.log(
        '  ' + c.name.padEnd(40) +
        inLufs.toFixed(1).padStart(7) +
        gainDb.toFixed(1).padStart(10) +
        outLufs.toFixed(1).padStart(9) +
        err.toFixed(1).padStart(6) + '   ' +
        bound.padEnd(16) + flag,
    );
}

console.log('\n  Spread of output loudness (how uniform auto-volume actually is):');
const outs = normalized.map((b) => kWeightedLoudnessLufs(b)).filter(Number.isFinite);
const min = Math.min(...outs), max = Math.max(...outs);
console.log(`    min ${min.toFixed(1)} LUFS   max ${max.toFixed(1)} LUFS   spread ${(max - min).toFixed(1)} dB`);
console.log(`    (target is one number; anything the peak ceiling or +12 cap can't reach lands below it)`);

// End-to-end: drain the normalized clips overlapped through the real mixer + limiter,
// the way runtime stacks concurrent triggers. Confirms no clipping at the int16 edge.
console.log('\n  End-to-end through PcmMixer + limiter (-1 dBTP), all 7 clips overlapped:');
const mixer = new PcmMixer();
mixer.configureLimiter(true, Math.pow(10, -1 / 20));
for (const b of normalized) mixer.addVoice(b);
let maxAbs = 0, clipped = 0, nonFinite = 0;
const frames = Math.max(...normalized.map((b) => b.length)) / 2;
const chunks = Math.ceil(frames / 960) + 4;
for (let k = 0; k < chunks; k++) {
    const chunk = mixer._mixOneChunk();
    for (let i = 0; i < chunk.length; i += 2) {
        const s = chunk.readInt16LE(i);
        if (!Number.isFinite(s)) nonFinite++;
        const a = Math.abs(s);
        if (a > maxAbs) maxAbs = a;
        if (a >= 32767) clipped++;
    }
}
const peakDb = maxAbs > 0 ? (20 * Math.log10(maxAbs / 32768)).toFixed(2) : '-inf';
console.log(`    summed peak = ${maxAbs} (${peakDb} dBFS)   clipped=${clipped}   nonFinite=${nonFinite}`);
console.log(`    ${clipped === 0 && nonFinite === 0 ? '✓ limiter held the overlapped sum clean' : '✗ overlapped sum produced clipping/garbage'}`);
