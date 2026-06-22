import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { LookaheadLimiter, LOOKAHEAD_FRAMES } from '../src/limiter.js';

// Build an interleaved float64 stereo buffer: `frames` frames at (aL, aR).
function constStereo(aL: number, aR: number, frames: number): Float64Array {
    const out = new Float64Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        out[i * 2] = aL;
        out[i * 2 + 1] = aR;
    }
    return out;
}

function maxAbs(buf: Float64Array): number {
    let m = 0;
    for (let i = 0; i < buf.length; i++) {
        const a = buf[i]! < 0 ? -buf[i]! : buf[i]!;
        if (a > m) m = a;
    }
    return m;
}

const EPS = 1e-9;

test('sub-ceiling signal passes at unity (after the look-ahead delay)', () => {
    const lim = new LookaheadLimiter(0.891); // -1 dBTP
    const frames = 600;
    const amp = 0.1; // well under the ceiling -> never limited
    const buf = constStereo(amp, amp, frames);
    lim.process(buf);

    // The first LOOKAHEAD_FRAMES frames are the delayed zero-init silence.
    for (let f = 0; f < LOOKAHEAD_FRAMES; f++) {
        assert.equal(buf[f * 2], 0, `frame ${f} should be delayed silence`);
    }
    // Past the delay, gain stays exactly unity (target is always 1).
    for (let f = LOOKAHEAD_FRAMES + 10; f < frames; f++) {
        assert.ok(Math.abs(buf[f * 2]! - amp) < EPS, `frame ${f} L drifted`);
        assert.ok(Math.abs(buf[f * 2 + 1]! - amp) < EPS, `frame ${f} R drifted`);
    }
});

test('hot signal is held at the ceiling with no overshoot', () => {
    const ceiling = 0.5;
    const lim = new LookaheadLimiter(ceiling);
    const buf = constStereo(1.0, 1.0, 1000); // peak 1.0 >> ceiling
    lim.process(buf);

    // Nowhere does the output exceed the ceiling (the brickwall guarantee — the
    // look-ahead means the onset never slips through during the attack).
    assert.ok(maxAbs(buf) <= ceiling + EPS, `overshoot: max ${maxAbs(buf)} > ${ceiling}`);

    // Once the envelope settles, the held level sits right at the ceiling.
    const settled = 900;
    assert.ok(Math.abs(Math.abs(buf[settled * 2]!) - ceiling) < 1e-3,
        `settled level ${buf[settled * 2]} != ${ceiling}`);
});

test('gain is channel-linked (same gain to L and R, image preserved)', () => {
    const ceiling = 0.5;
    const lim = new LookaheadLimiter(ceiling);
    const inL = 1.0, inR = 0.3; // asymmetric; the louder channel drives the gain
    const buf = constStereo(inL, inR, 800);
    lim.process(buf);

    const f = 700; // settled
    const gL = buf[f * 2]! / inL;
    const gR = buf[f * 2 + 1]! / inR;
    assert.ok(Math.abs(gL - gR) < 1e-6, `gain split L=${gL} R=${gR} (must be linked)`);
    // And the louder channel is exactly at the ceiling.
    assert.ok(Math.abs(buf[f * 2]! - ceiling) < 1e-3);
});

test('gain recovers to unity after a loud burst (release)', () => {
    const ceiling = 0.5;
    const lim = new LookaheadLimiter(ceiling);
    // Loud burst ducks the gain to 0.5...
    lim.process(constStereo(1.0, 1.0, 500));
    // ...then a long quiet passage; release should bring the gain back to ~1.
    const quiet = 0.1;
    const tail = constStereo(quiet, quiet, 24000); // 0.5 s at 48 kHz
    lim.process(tail);

    const last = tail.length / 2 - 1;
    assert.ok(Math.abs(tail[last * 2]! - quiet) < 1e-3,
        `did not recover: ${tail[last * 2]} != ${quiet}`);
});

test('no overshoot on a sudden full-scale step transient', () => {
    const ceiling = 0.5;
    const lim = new LookaheadLimiter(ceiling);
    const silence = constStereo(0, 0, 200);
    const burst = constStereo(1.0, 1.0, 500);
    lim.process(silence);
    lim.process(burst);
    // The step's onset must never break the ceiling — this is what look-ahead
    // buys over a feed-forward limiter.
    assert.ok(maxAbs(silence) <= ceiling + EPS);
    assert.ok(maxAbs(burst) <= ceiling + EPS, `step overshoot: ${maxAbs(burst)}`);
});

test('setCeiling retargets without resetting the running envelope', () => {
    const lim = new LookaheadLimiter(0.5);
    lim.process(constStereo(1.0, 1.0, 500)); // settle at 0.5
    lim.setCeiling(0.25);
    const buf = constStereo(1.0, 1.0, 1000);
    lim.process(buf);
    // The first LOOKAHEAD_FRAMES outputs are audio already committed to the
    // delay line under the OLD ceiling — a mid-stream ceiling drop is not
    // retroactive to it. Once the pipeline flushes, the new ceiling holds.
    let m = 0;
    for (let f = LOOKAHEAD_FRAMES; f < 1000; f++) {
        const a = Math.abs(buf[f * 2]!);
        if (a > m) m = a;
    }
    assert.ok(m <= 0.25 + EPS, `did not adopt new ceiling past the delay: ${m}`);
    assert.ok(Math.abs(Math.abs(buf[900 * 2]!) - 0.25) < 1e-3);
});

test('reset() clears state back to unity + silence', () => {
    const lim = new LookaheadLimiter(0.5);
    lim.process(constStereo(1.0, 1.0, 500));
    lim.reset();
    const buf = constStereo(0.1, 0.1, 600);
    lim.process(buf);
    // First frames are silence again (delay line re-zeroed), and the gain is
    // back at unity (no residual ducking from the previous burst).
    assert.equal(buf[0], 0);
    assert.ok(Math.abs(buf[(LOOKAHEAD_FRAMES + 50) * 2]! - 0.1) < EPS);
});
