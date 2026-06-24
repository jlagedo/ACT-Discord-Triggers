import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { conditionSource, DC_BLOCK_POLE, SILENCE_THRESHOLD } from '../src/source-conditioning.js';
import { applyEffect, mulberry32 } from '../src/effects.js';
import { normalize } from '../src/normalize.js';
import { declick } from '../src/declick.js';

// conditionSource is the file-path ingest stage: sanitize → DC-block → trim
// silence → edge-fade, run on the 48 kHz interleaved float32 stereo buffer
// before any random effect. Its job is to make an uncontrolled source safe so a
// length-extending effect (echo/reverb) can't relocate a hot/junk source edge
// into the buffer interior and play it as a "gunshot" pop.

const SR = 48000;
const FRAME = 2; // interleaved float32 stereo

function sineStereo(freq: number, amp: number, frames: number, dc = 0): Float32Array {
    const buf = new Float32Array(frames * FRAME);
    const w = (2 * Math.PI * freq) / SR;
    for (let i = 0; i < frames; i++) {
        const s = amp * Math.sin(i * w) + dc;
        buf[i * FRAME] = s;
        buf[i * FRAME + 1] = s;
    }
    return buf;
}

function zerosStereo(frames: number): Float32Array {
    return new Float32Array(frames * FRAME);
}

function maxAbs(samples: Float32Array): number {
    let m = 0;
    for (let i = 0; i < samples.length; i++) {
        const a = Math.abs(samples[i]!);
        if (a > m) m = a;
    }
    return m;
}

function meanSample(samples: Float32Array): number {
    let sum = 0;
    for (let i = 0; i < samples.length; i++) sum += samples[i]!;
    return sum / samples.length;
}

// Frame peak max(|L|,|R|) over an interleaved buffer.
function framePeak(samples: Float32Array, frame: number): number {
    return Math.max(Math.abs(samples[frame * FRAME]!), Math.abs(samples[frame * FRAME + 1]!));
}

test('conditionSource: removes a DC offset (output mean ≈ 0)', () => {
    const input = sineStereo(1000, 0.4, 48000, 0.3); // 1 s tone biased +0.3
    assert.ok(Math.abs(meanSample(input)) > 0.25, 'fixture should carry the DC bias');
    const out = conditionSource(input);
    assert.ok(Math.abs(meanSample(out)) < 0.01, `DC should be blocked, mean=${meanSample(out)}`);
});

test('conditionSource: trims leading and trailing silence, preserves the body', () => {
    const lead = zerosStereo(2400);   // 50 ms silence
    const body = sineStereo(1000, 0.5, 9600); // 200 ms tone
    const trail = zerosStereo(4800);  // 100 ms silence
    const input = new Float32Array(lead.length + body.length + trail.length);
    input.set(lead, 0);
    input.set(body, lead.length);
    input.set(trail, lead.length + body.length);

    const out = conditionSource(input);
    const inFrames = input.length / FRAME;
    const outFrames = out.length / FRAME;
    // The bulk of the silence is gone. The output runs a bit past the 9600-frame
    // body: the DC-blocker (a high-pass) rings down slowly past the body's edges,
    // so a low-level settling tail stays above the silence floor — harmless.
    assert.ok(outFrames < inFrames - 4000, `expected silence trimmed, in=${inFrames} out=${outFrames}`);
    assert.ok(outFrames > 9000 && outFrames < 12000, `body roughly preserved, out=${outFrames}`);
    // Body content survived (well above the silence floor somewhere in the middle).
    assert.ok(maxAbs(out) > 0.3, `tone preserved, peak=${maxAbs(out)}`);
});

test('conditionSource: edges fade to (near) zero', () => {
    const out = conditionSource(sineStereo(1000, 0.6, 9600));
    const frames = out.length / FRAME;
    assert.ok(framePeak(out, 0) < 0.02, `first frame should be ~0, got ${framePeak(out, 0)}`);
    assert.ok(framePeak(out, frames - 1) < 0.02, `last frame should be ~0, got ${framePeak(out, frames - 1)}`);
});

test('conditionSource: sanitizes NaN/Infinity to finite output', () => {
    const input = sineStereo(1000, 0.4, 4800);
    input[100] = NaN;
    input[101] = Infinity;
    input[2000] = -Infinity;
    const out = conditionSource(input);
    for (let i = 0; i < out.length; i++) {
        assert.ok(Number.isFinite(out[i]!), `sample ${i} should be finite, got ${out[i]}`);
    }
});

test('conditionSource: does not mutate the input buffer', () => {
    const input = sineStereo(1000, 0.4, 4800, 0.2);
    const snapshot = Float32Array.from(input);
    conditionSource(input);
    assert.deepEqual(input, snapshot);
});

test('conditionSource: all-silence input returns without throwing', () => {
    const out = conditionSource(zerosStereo(4800));
    assert.ok(out instanceof Float32Array);
    assert.ok(maxAbs(out) < SILENCE_THRESHOLD * 2, 'silence stays silent');
});

test('conditionSource: empty / sub-frame input is handled', () => {
    assert.equal(conditionSource(new Float32Array(0)).length, 0);
    assert.equal(conditionSource(new Float32Array(1)).length, 0); // odd sample → 0 aligned frames
});

test('DC_BLOCK_POLE and SILENCE_THRESHOLD are sane constants', () => {
    assert.ok(DC_BLOCK_POLE > 0.99 && DC_BLOCK_POLE < 1);
    assert.ok(SILENCE_THRESHOLD > 0 && SILENCE_THRESHOLD < 0.01);
});

// --- Regression: the actual "gunshot" bug --------------------------------------
//
// Build a clip that mimics the corrupt source: a tone body, then silence, then a
// full-scale alternating junk burst in the final few frames. A length-extending
// effect (echo) appends a tail, moving that burst into the buffer interior where
// the per-clip output declick can no longer fade it. conditionSource must remove
// the burst at ingest so the interior stays clean.

function junkTailFixture(): Float32Array {
    const bodyFrames = 9600;   // 200 ms tone
    const gapFrames = 9000;    // ~190 ms silence (as in the real file)
    const junkFrames = 6;      // corrupt burst at EOF
    const total = bodyFrames + gapFrames + junkFrames;
    const buf = new Float32Array(total * FRAME);
    const body = sineStereo(440, 0.3, bodyFrames);
    buf.set(body, 0);
    // gap stays zero
    const junk = [0.69, 0.0, 0.0, 0.80, 0.89, 0.76]; // non-bandlimited garbage
    for (let j = 0; j < junkFrames; j++) {
        const f = bodyFrames + gapFrames + j;
        buf[f * FRAME] = junk[j]!;
        buf[f * FRAME + 1] = junk[j]!;
    }
    return buf;
}

// Run a buffer through the production per-clip chain for a length-extending
// effect, then return the largest interior single-sample discontinuity (left
// channel). A discontinuity — not amplitude — is what makes the artifact audible
// as a click/pop: the junk burst zig-zags ~0.7 between adjacent samples, while a
// legitimately-echoed bandlimited body tone moves smoothly (slope « 0.05). The
// final declick fade-out region is excluded (its tail edge is ramped by design).
function effectInteriorMaxJump(src: Float32Array): number {
    const fx = applyEffect('echo', src, mulberry32(12345));
    const norm = normalize(fx, -17);
    const out = declick(norm.applied ? norm.samples : fx);
    const frames = out.length / FRAME;
    const fadeOut = 240; // FADE_OUT_FRAMES — declick ramps this region by design
    let maxJump = 0;
    for (let f = 1; f < frames - fadeOut; f++) {
        const j = Math.abs(out[f * FRAME]! - out[(f - 1) * FRAME]!);
        if (j > maxJump) maxJump = j;
    }
    return maxJump;
}

test('regression: conditioning removes the effect-exposed junk-tail transient', () => {
    const fixture = junkTailFixture();

    // Without conditioning the echo relocates the full-scale burst into the
    // interior — a click (large sample-to-sample jump) survives the output declick.
    const rawJump = effectInteriorMaxJump(fixture);
    assert.ok(rawJump > 0.3, `unconditioned interior should have a click (test must bite), got ${rawJump}`);

    // With conditioning the burst is faded out at ingest, so the echo only repeats
    // the smooth body tone — no interior discontinuity.
    const condJump = effectInteriorMaxJump(conditionSource(fixture));
    assert.ok(condJump < 0.1, `conditioned interior should be smooth, got ${condJump}`);
});
