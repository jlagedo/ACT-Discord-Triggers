import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { normalize } from '../src/normalize.js';

// Build N interleaved float32 stereo frames where every sample equals `value`. A
// constant amplitude makes RMS == |value|, so the expected gain is easy to reason
// about in the assertions below.
function constStereo(frames: number, value: number): Float32Array {
    const buf = new Float32Array(frames * 2);
    buf.fill(value);
    return buf;
}

function maxAbs(samples: Float32Array): number {
    let m = 0;
    for (let i = 0; i < samples.length; i++) {
        const a = Math.abs(samples[i]!);
        if (a > m) m = a;
    }
    return m;
}

function rmsNorm(samples: Float32Array): number {
    let sumSq = 0;
    for (let i = 0; i < samples.length; i++) sumSq += samples[i]! * samples[i]!;
    return Math.sqrt(sumSq / samples.length);
}

test('empty buffer is returned untouched', () => {
    const empty = new Float32Array(0);
    const r = normalize(empty, -20);
    assert.equal(r.applied, false);
    assert.equal(r.gain, 1);
    assert.equal(r.samples, empty);
});

test('silence (all zeros) is left untouched, no divide-by-zero blowup', () => {
    const silence = constStereo(480, 0);
    const r = normalize(silence, -20);
    assert.equal(r.applied, false);
    assert.equal(r.gain, 1);
    assert.equal(r.samples, silence);
});

test('loud clip is attenuated toward target (gain < 1)', () => {
    // Half-scale constant → RMS = -6 dBFS, well above a -20 target.
    const loud = constStereo(480, 0.5);
    const r = normalize(loud, -20);
    assert.equal(r.applied, true);
    assert.ok(r.gain < 1, `expected attenuation, got gain=${r.gain}`);
    // RMS of the result should land near the -20 dBFS target (10^(-20/20)=0.1).
    assert.ok(Math.abs(rmsNorm(r.samples) - 0.1) < 0.01, `rms=${rmsNorm(r.samples)}`);
    assert.ok(maxAbs(r.samples) <= 1);
});

test('quiet clip is boosted but capped at +12 dB max boost', () => {
    // Very quiet constant → target wants ~+34 dB; the cap should bind first.
    const quiet = constStereo(480, 64 / 32768);
    const r = normalize(quiet, -20);
    assert.equal(r.applied, true);
    const maxBoost = Math.pow(10, 12 / 20); // ≈ 3.981
    assert.ok(r.gain <= maxBoost + 1e-6, `gain ${r.gain} exceeded max boost ${maxBoost}`);
    assert.ok(r.gain > 3.9, `expected gain near the cap, got ${r.gain}`);
});

test('peak ceiling binds before max boost on a low-RMS, high-peak clip', () => {
    // One peak frame in an otherwise-silent buffer: low RMS demands a big boost,
    // but the peak ceiling (0.97) clamps the gain below the +12 dB cap.
    const buf = constStereo(100, 0);
    buf[0] = 0.25;
    buf[1] = 0.25;
    const r = normalize(buf, -20);
    assert.equal(r.applied, true);
    const peakNorm = 0.25;
    const peakLimit = 0.97 / peakNorm;
    assert.ok(r.gain <= peakLimit + 1e-6, `gain ${r.gain} exceeded peak limit ${peakLimit}`);
    // No sample may clip after the boost.
    assert.ok(maxAbs(r.samples) <= 1);
});

test('near-target clip is left untouched (gain within unity epsilon)', () => {
    // Constant chosen so RMS ≈ -20 dBFS already (0.1).
    const onTarget = constStereo(480, 0.1);
    const r = normalize(onTarget, -20);
    assert.equal(r.applied, false);
    assert.equal(r.samples, onTarget);
});

test('output length always matches input length', () => {
    const buf = constStereo(123, 0.27);
    const r = normalize(buf, -16);
    assert.equal(r.samples.length, buf.length);
});

test('known level skips measurement and matches measuring an equivalent clip', () => {
    // A constant half-scale clip: RMS == peak == 0.5. Passing that as a known
    // level must yield the identical gain to measuring it.
    const loud = constStereo(480, 0.5);
    const measured = normalize(loud, -20);
    const known = normalize(loud, -20, { rms: 0.5, peak: 0.5 });
    assert.equal(known.applied, measured.applied);
    assert.ok(Math.abs(known.gain - measured.gain) < 1e-9, `known=${known.gain} measured=${measured.gain}`);
    assert.ok(Math.abs(rmsNorm(known.samples) - 0.1) < 0.01);
});

test('known level uses the supplied numbers, not the buffer contents', () => {
    // Buffer is silent, but we assert that a known peak drives the peak-ceiling
    // clamp: a tiny known rms with a high known peak limits the gain to the
    // ceiling/peak, proving the buffer was never scanned for level.
    const buf = constStereo(200, 0);
    const r = normalize(buf, -20, { rms: 0.02, peak: 0.8 });
    assert.equal(r.applied, true);
    const peakLimit = 0.97 / 0.8;
    assert.ok(r.gain <= peakLimit + 1e-9, `gain ${r.gain} exceeded peak limit ${peakLimit}`);
    assert.ok(r.gain > 1, 'a -34 dBFS known rms toward -20 should boost');
});
