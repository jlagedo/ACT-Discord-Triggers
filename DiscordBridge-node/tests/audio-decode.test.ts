import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import {
    planarFloatToInterleavedStereoF32 as toStereoF32,
} from '../src/audio-decode.js';

function f32(...xs: number[]): Float32Array {
    return Float32Array.from(xs);
}

test('mono duplicates into interleaved L=R (float passthrough, no clamp)', () => {
    const out = toStereoF32([f32(0, 1, -1, 0.5)]);
    assert.equal(out.length, 4 * 2); // 4 frames * 2 samples
    const expected = [0, 1, -1, 0.5];
    for (let i = 0; i < 4; i++) {
        assert.equal(out[i * 2], expected[i], `L[${i}]`);
        assert.equal(out[i * 2 + 1], expected[i], `R[${i}]`);
    }
});

test('stereo keeps channels distinct', () => {
    const out = toStereoF32([f32(0.5, -0.5), f32(-0.25, 0.25)]);
    assert.equal(out[0], 0.5);    // L0
    assert.equal(out[1], -0.25);  // R0
    assert.equal(out[2], -0.5);   // L1
    assert.equal(out[3], 0.25);   // R1
});

test('>2 channels uses the first two only', () => {
    const out = toStereoF32([f32(1), f32(-1), f32(0.5)]); // ch2 ignored
    assert.equal(out.length, 2);
    assert.equal(out[0], 1);
    assert.equal(out[1], -1);
});

test('empty channel list yields an empty buffer', () => {
    assert.equal(toStereoF32([]).length, 0);
});

test('ragged channels clamp to the shorter length (no OOB read)', () => {
    const out = toStereoF32([f32(0.1, 0.2, 0.3), f32(0.1)]);
    assert.equal(out.length, 2); // min(3,1) = 1 frame
});

test('out-of-range floats are preserved (the exit conversion clamps, not this)', () => {
    const out = toStereoF32([f32(1.5, -1.5)]);
    assert.equal(out[0], 1.5);
    assert.equal(out[2], -1.5);
});
