import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { int16ToFloat32, floatToInt16 } from '../src/audio-format.js';

test('int16ToFloat32 maps the int16 range into [-1, 1)', () => {
    const buf = Buffer.alloc(4 * 2);
    buf.writeInt16LE(0, 0);
    buf.writeInt16LE(32767, 2);
    buf.writeInt16LE(-32768, 4);
    buf.writeInt16LE(16384, 6);
    const f = int16ToFloat32(buf);
    assert.equal(f.length, 4);
    assert.equal(f[0], 0);
    assert.ok(Math.abs(f[1]! - 32767 / 32768) < 1e-9);
    assert.equal(f[2], -1);            // -32768 / 32768
    assert.equal(f[3], 0.5);           // 16384 / 32768
});

test('floatToInt16 rounds to nearest and hard-clamps both rails', () => {
    const f = Float32Array.from([0, 0.5, -1, 1, 2, -2, 0.4999 / 32768]);
    const buf = floatToInt16(f);
    assert.equal(buf.readInt16LE(0), 0);
    assert.equal(buf.readInt16LE(2), 16384);
    assert.equal(buf.readInt16LE(4), -32768);
    assert.equal(buf.readInt16LE(6), 32767);   // 1.0 -> 32768 clamped
    assert.equal(buf.readInt16LE(8), 32767);   // +2 clamped
    assert.equal(buf.readInt16LE(10), -32768); // -2 clamped
    assert.equal(buf.readInt16LE(12), 0);      // sub-LSB rounds to 0
});

test('round-trip int16 -> float -> int16 is lossless across the range', () => {
    const buf = Buffer.alloc(0x10000 * 2);
    for (let v = -32768, i = 0; v <= 32767; v++, i++) buf.writeInt16LE(v, i * 2);
    const back = floatToInt16(int16ToFloat32(buf));
    assert.equal(Buffer.compare(back, buf), 0);
});

test('floatToInt16 writes into a supplied output buffer in place', () => {
    const f = Float32Array.from([0.25, -0.25]);
    const out = Buffer.allocUnsafe(4);
    const r = floatToInt16(f, out);
    assert.equal(r, out, 'returns the same buffer instance');
    assert.equal(out.readInt16LE(0), 8192);
    assert.equal(out.readInt16LE(2), -8192);
});

test('int16ToFloat32 drops a trailing odd byte', () => {
    const buf = Buffer.from([0x00, 0x40, 0xff]); // one s16 sample (16384) + stray byte
    const f = int16ToFloat32(buf);
    assert.equal(f.length, 1);
    assert.equal(f[0], 0.5);
});
