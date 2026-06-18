import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import {
    planarFloatToInterleavedInt16Stereo_PHASE1_SHIM as toInt16Stereo,
} from '../src/audio-decode.js';

function f32(...xs: number[]): Float32Array {
    return Float32Array.from(xs);
}

test('shim: mono duplicates into L=R with the effects.ts int16 convention', () => {
    // 0 -> 0, 1 -> 32767 (clamped), -1 -> -32768, 0.5 -> 16384
    const out = toInt16Stereo([f32(0, 1, -1, 0.5)]);
    assert.equal(out.length, 4 * 4); // 4 frames * 4 bytes
    const expected = [0, 32767, -32768, 16384];
    for (let i = 0; i < 4; i++) {
        assert.equal(out.readInt16LE(i * 4), expected[i], `L[${i}]`);
        assert.equal(out.readInt16LE(i * 4 + 2), expected[i], `R[${i}]`);
    }
});

test('shim: stereo keeps channels distinct', () => {
    const out = toInt16Stereo([f32(0.5, -0.5), f32(-0.25, 0.25)]);
    assert.equal(out.readInt16LE(0), 16384);   // L0
    assert.equal(out.readInt16LE(2), -8192);   // R0
    assert.equal(out.readInt16LE(4), -16384);  // L1
    assert.equal(out.readInt16LE(6), 8192);    // R1
});

test('shim: >2 channels uses the first two only', () => {
    const out = toInt16Stereo([f32(1), f32(-1), f32(0.5)]); // ch2 ignored
    assert.equal(out.length, 4);
    assert.equal(out.readInt16LE(0), 32767);
    assert.equal(out.readInt16LE(2), -32768);
});

test('shim: empty channel list yields an empty buffer', () => {
    assert.equal(toInt16Stereo([]).length, 0);
});

test('shim: ragged channels clamp to the shorter length (no OOB read)', () => {
    const out = toInt16Stereo([f32(0.1, 0.2, 0.3), f32(0.1)]);
    assert.equal(out.length, 4); // min(3,1) = 1 frame
});

test('shim: clamps out-of-range floats to the int16 bounds', () => {
    const out = toInt16Stereo([f32(1.5, -1.5)]);
    assert.equal(out.readInt16LE(0), 32767);
    assert.equal(out.readInt16LE(4), -32768);
});
