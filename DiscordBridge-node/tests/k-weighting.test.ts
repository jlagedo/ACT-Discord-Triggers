import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { kWeightedLoudnessLufs, kWeightingMagnitude } from '../src/k-weighting.js';

const SR = 48000;

// Interleaved float32 stereo sine at `freq`, amplitude `amp`, identical on L+R.
// Length is a whole number of frames; a few thousand frames keeps the filter
// warm-up a negligible fraction of the measured energy.
function sineStereo(freq: number, amp: number, frames: number): Float32Array {
    const out = new Float32Array(frames * 2);
    const w = (2 * Math.PI * freq) / SR;
    for (let i = 0; i < frames; i++) {
        const s = amp * Math.sin(i * w);
        out[i * 2] = s;
        out[i * 2 + 1] = s;
    }
    return out;
}

test('1 kHz dual-mono sine reads ≈ 20·log10(amp) LUFS (the BS.1770 calibration point)', () => {
    // At 1 kHz the K cascade's ~+0.69 dB shelf gain and the −0.691 LUFS offset
    // very nearly cancel, so a dual-mono 1 kHz tone reads its bare amplitude in dB.
    const amp = 0.5;
    const lufs = kWeightedLoudnessLufs(sineStereo(1000, amp, 48000));
    const expected = 20 * Math.log10(amp); // ≈ -6.02
    assert.ok(Math.abs(lufs - expected) < 0.15, `1 kHz @ ${amp}: got ${lufs} LUFS, expected ≈ ${expected}`);
});

test('low-frequency tone reads quieter than a 1 kHz tone of the same amplitude (HPF rolls off bass)', () => {
    const amp = 0.5;
    const ref = kWeightedLoudnessLufs(sineStereo(1000, amp, 48000));
    const low = kWeightedLoudnessLufs(sineStereo(60, amp, 48000));
    assert.ok(low < ref - 1, `60 Hz (${low}) should be clearly quieter than 1 kHz (${ref})`);
});

test('high-frequency tone reads louder than a 1 kHz tone of the same amplitude (shelf lift)', () => {
    const amp = 0.5;
    const ref = kWeightedLoudnessLufs(sineStereo(1000, amp, 48000));
    const high = kWeightedLoudnessLufs(sineStereo(8000, amp, 48000));
    assert.ok(high > ref + 1, `8 kHz (${high}) should be clearly louder than 1 kHz (${ref})`);
});

test('silence reads -Infinity (no steady component survives the high-pass)', () => {
    assert.equal(kWeightedLoudnessLufs(new Float32Array(4800)), -Infinity);
});

test('DC (constant) reads far below its broadband energy — the RLB high-pass removes it', () => {
    // A 0.5 DC block is -6 dBFS broadband but inaudible; K-weighting must read it
    // far quieter (only the filter's settling transient survives), well under any
    // real program level.
    const dc = new Float32Array(48000 * 2);
    dc.fill(0.5);
    const lufs = kWeightedLoudnessLufs(dc);
    assert.ok(lufs < -25, `DC @ 0.5 should read far below -6 dBFS broadband; got ${lufs} LUFS`);
});

test('empty buffer reads -Infinity', () => {
    assert.equal(kWeightedLoudnessLufs(new Float32Array(0)), -Infinity);
});

test('analytic cascade magnitude: ~unity near 1 kHz, attenuated low, boosted high', () => {
    const m1k = kWeightingMagnitude(1000);
    assert.ok(Math.abs(m1k - 1.083) < 0.02, `|H(1k)| ≈ 1.083, got ${m1k}`);
    assert.ok(kWeightingMagnitude(60) < 0.95, `|H(60)| should be < 1, got ${kWeightingMagnitude(60)}`);
    assert.ok(kWeightingMagnitude(10000) > 1.4, `|H(10k)| should be on the shelf plateau, got ${kWeightingMagnitude(10000)}`);
});

test('a 1 kHz tone agrees with the analytic magnitude (filter matches its transfer function)', () => {
    // Energy-only invariant: K-RMS / broadband-RMS at 1 kHz == |H(1k)|.
    const amp = 0.5;
    const lufs = kWeightedLoudnessLufs(sineStereo(1000, amp, 48000));
    // Back out the K-weighted per-sample RMS from the dual-mono LUFS:
    //   LUFS = -0.691 + 10log10(2 * msChan)  ->  msChan = 10^((LUFS+0.691)/10) / 2
    const msChan = Math.pow(10, (lufs + 0.691) / 10) / 2;
    const kRms = Math.sqrt(msChan);
    const broadbandRms = amp / Math.SQRT2;
    const ratio = kRms / broadbandRms;
    assert.ok(Math.abs(ratio - kWeightingMagnitude(1000)) < 0.01, `ratio ${ratio} vs |H(1k)| ${kWeightingMagnitude(1000)}`);
});
