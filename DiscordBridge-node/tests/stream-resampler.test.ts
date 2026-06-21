import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { StreamingResampler } from '../src/stream-resampler.js';
import { resampleStereo16 } from '../src/discord-host.js';

// Build `frames` of interleaved s16le stereo with a per-channel sine so adjacent
// frames differ (a constant buffer would hide boundary errors). L and R use
// different frequencies so a channel swap would also show up.
function sineStereo(frames: number): Buffer {
    const buf = Buffer.alloc(frames * 4);
    for (let i = 0; i < frames; i++) {
        const l = Math.round(8000 * Math.sin((2 * Math.PI * 7 * i) / frames));
        const r = Math.round(6000 * Math.sin((2 * Math.PI * 11 * i) / frames));
        buf.writeInt16LE(l, i * 4);
        buf.writeInt16LE(r, i * 4 + 2);
    }
    return buf;
}

// Resample `src` through the streaming resampler in frame-aligned chunks of the
// given sizes (cycled), concatenating push() outputs + the final flush().
function streamResample(src: Buffer, srcRate: number, dstRate: number, chunkFrames: number[]): Buffer {
    const rs = new StreamingResampler(srcRate, dstRate);
    const out: Buffer[] = [];
    let off = 0;
    let k = 0;
    const totalFrames = src.length >>> 2;
    while (off < totalFrames) {
        const n = Math.min(chunkFrames[k % chunkFrames.length]!, totalFrames - off);
        out.push(rs.push(src.subarray(off * 4, (off + n) * 4)));
        off += n;
        k++;
    }
    out.push(rs.flush());
    return Buffer.concat(out);
}

// Max absolute per-sample int16 difference between two equal-length buffers.
function maxDiff(a: Buffer, b: Buffer): number {
    assert.equal(a.length, b.length, `length mismatch ${a.length} vs ${b.length}`);
    let m = 0;
    for (let i = 0; i < a.length; i += 2) {
        const d = Math.abs(a.readInt16LE(i) - b.readInt16LE(i));
        if (d > m) m = d;
    }
    return m;
}

for (const [srcRate, label] of [[22050, 'Piper 22050'], [24000, 'Kokoro 24000']] as const) {
    test(`streamed resample matches whole-buffer resampleStereo16 (${label})`, () => {
        const src = sineStereo(2000);
        const whole = resampleStereo16(src, srcRate, 48000);
        // A mix of chunk sizes including small, prime, and large.
        for (const sizes of [[1], [1, 2, 3], [97, 193, 389], [500], [1, 999]]) {
            const streamed = streamResample(src, srcRate, 48000, sizes);
            assert.equal(streamed.length, whole.length, `length for chunks ${sizes.join(',')}`);
            assert.ok(maxDiff(streamed, whole) <= 1, `maxDiff for chunks ${sizes.join(',')}`);
        }
    });
}

test('identity passthrough when src == dst rate', () => {
    const src = sineStereo(500);
    const rs = new StreamingResampler(48000, 48000);
    const out = Buffer.concat([rs.push(src), rs.flush()]);
    assert.equal(Buffer.compare(out, src), 0);
});

test('tolerates a trailing odd byte by frame-aligning the chunk', () => {
    const rs = new StreamingResampler(22050, 48000);
    const frame = sineStereo(100);
    const stray = Buffer.concat([frame, Buffer.from([0xff])]); // one extra byte
    // Must not throw; output is whole frames only.
    const out = Buffer.concat([rs.push(stray), rs.flush()]);
    assert.equal(out.length % 4, 0);
    assert.ok(out.length > 0);
});

test('empty input produces no output and no throw', () => {
    const rs = new StreamingResampler(22050, 48000);
    assert.equal(rs.push(Buffer.alloc(0)).length, 0);
    assert.equal(rs.flush().length, 0);
});
