import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { normalize, measureLevel, linearToDb } from '../src/normalize.js';

const SR = 48000;

// Interleaved float32 stereo sine at `freq`, amplitude `amp`, identical on L+R.
// Loudness is measured with K-weighting (see k-weighting.ts), so level-bearing
// test signals must be tones, not DC: a 1 kHz dual-mono sine reads ≈ 20·log10(amp)
// LUFS (the BS.1770 calibration point), which makes the expected gains easy to
// reason about. A constant/DC block would read ~silence (the HPF removes it).
function sineStereo(freq: number, amp: number, frames: number): Float32Array {
    const buf = new Float32Array(frames * 2);
    const w = (2 * Math.PI * freq) / SR;
    for (let i = 0; i < frames; i++) {
        const s = amp * Math.sin(i * w);
        buf[i * 2] = s;
        buf[i * 2 + 1] = s;
    }
    return buf;
}

function zerosStereo(frames: number): Float32Array {
    return new Float32Array(frames * 2);
}

function maxAbs(samples: Float32Array): number {
    let m = 0;
    for (let i = 0; i < samples.length; i++) {
        const a = Math.abs(samples[i]!);
        if (a > m) m = a;
    }
    return m;
}

// The clip's loudness in LUFS, via the same measure normalize uses.
function measureLufs(samples: Float32Array): number {
    return linearToDb(measureLevel(samples).rms);
}

test('empty buffer is returned untouched', () => {
    const empty = new Float32Array(0);
    const r = normalize(empty, -17);
    assert.equal(r.applied, false);
    assert.equal(r.gain, 1);
    assert.equal(r.samples, empty);
});

test('silence (all zeros) is left untouched, no divide-by-zero blowup', () => {
    const silence = zerosStereo(480);
    const r = normalize(silence, -17);
    assert.equal(r.applied, false);
    assert.equal(r.gain, 1);
    assert.equal(r.samples, silence);
});

test('loud clip is attenuated toward target (gain < 1)', () => {
    // 1 kHz @ 0.5 ≈ -6 LUFS, well above a -17 target.
    const loud = sineStereo(1000, 0.5, 48000);
    const r = normalize(loud, -17);
    assert.equal(r.applied, true);
    assert.ok(r.gain < 1, `expected attenuation, got gain=${r.gain}`);
    // The result's loudness should land on the -17 LUFS target.
    assert.ok(Math.abs(measureLufs(r.samples) - -17) < 0.1, `result LUFS=${measureLufs(r.samples)}`);
    assert.ok(maxAbs(r.samples) <= 1);
});

test('quiet clip is boosted but capped at +12 dB max boost', () => {
    // Very quiet 1 kHz tone (~-54 LUFS) → target wants ~+37 dB; the cap binds first.
    const quiet = sineStereo(1000, 64 / 32768, 48000);
    const r = normalize(quiet, -17);
    assert.equal(r.applied, true);
    const maxBoost = Math.pow(10, 12 / 20); // ≈ 3.981
    assert.ok(r.gain <= maxBoost + 1e-6, `gain ${r.gain} exceeded max boost ${maxBoost}`);
    assert.ok(r.gain > 3.9, `expected gain near the cap, got ${r.gain}`);
});

test('peak ceiling binds before max boost on a low-loudness, high-peak clip', () => {
    // One 0.25 frame in an otherwise-silent 400-frame buffer: very low loudness
    // demands a big boost, but the peak ceiling (0.97/0.25 ≈ 3.88) clamps the gain
    // below the +12 dB cap (≈ 3.98).
    const buf = zerosStereo(400);
    buf[0] = 0.25;
    buf[1] = 0.25;
    const r = normalize(buf, -17);
    assert.equal(r.applied, true);
    const peakLimit = 0.97 / 0.25;
    assert.ok(r.gain <= peakLimit + 1e-6, `gain ${r.gain} exceeded peak limit ${peakLimit}`);
    assert.ok(Math.abs(r.gain - peakLimit) < 1e-3, `peak ceiling should bind: gain ${r.gain} vs ${peakLimit}`);
    // No sample may clip after the boost.
    assert.ok(maxAbs(r.samples) <= 1);
});

test('near-target clip is left untouched (gain within unity epsilon)', () => {
    // 1 kHz @ 0.1413 reads ≈ -17 LUFS already.
    const onTarget = sineStereo(1000, 0.1413, 48000);
    const r = normalize(onTarget, -17);
    assert.equal(r.applied, false);
    assert.equal(r.samples, onTarget);
});

test('output length always matches input length', () => {
    const buf = sineStereo(1000, 0.27, 4800);
    const r = normalize(buf, -16);
    assert.equal(r.samples.length, buf.length);
});

test('known level skips measurement and matches measuring an equivalent clip', () => {
    // Measure a tone, then pass those exact numbers as a known level: the gain must
    // match measuring the buffer (the known path skips the scan, same gain math).
    const loud = sineStereo(1000, 0.5, 4800);
    const lvl = measureLevel(loud);
    const measured = normalize(loud, -17);
    const known = normalize(loud, -17, { rms: lvl.rms, peak: lvl.peak });
    assert.equal(known.applied, measured.applied);
    assert.ok(Math.abs(known.gain - measured.gain) < 1e-9, `known=${known.gain} measured=${measured.gain}`);
    assert.ok(Math.abs(measureLufs(known.samples) - -17) < 0.1);
});

test('known level uses the supplied numbers, not the buffer contents', () => {
    // Buffer is silent, but a known peak drives the peak-ceiling clamp: a tiny known
    // rms with a high known peak limits the gain to ceiling/peak, proving the buffer
    // was never scanned for level.
    const buf = zerosStereo(200);
    const r = normalize(buf, -17, { rms: 0.02, peak: 0.8 });
    assert.equal(r.applied, true);
    const peakLimit = 0.97 / 0.8;
    assert.ok(r.gain <= peakLimit + 1e-9, `gain ${r.gain} exceeded peak limit ${peakLimit}`);
    assert.ok(r.gain > 1, 'a -34 LUFS known rms toward -17 should boost');
});
