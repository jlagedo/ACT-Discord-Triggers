import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { StreamingResampler } from '../src/stream-resampler.js';
import { resampleStereoF32 } from '../src/discord-host.js';

// Build `frames` of interleaved float32 stereo with a per-channel sine so adjacent
// frames differ (a constant buffer would hide boundary errors). L and R use
// different frequencies so a channel swap would also show up.
function sineStereo(frames: number): Float32Array {
    const buf = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        buf[i * 2] = 0.24 * Math.sin((2 * Math.PI * 7 * i) / frames);
        buf[i * 2 + 1] = 0.18 * Math.sin((2 * Math.PI * 11 * i) / frames);
    }
    return buf;
}

// Resample `src` through the streaming resampler in frame-aligned chunks of the
// given sizes (cycled), concatenating push() outputs + the final flush().
function streamResample(src: Float32Array, srcRate: number, dstRate: number, chunkFrames: number[]): Float32Array {
    const rs = new StreamingResampler(srcRate, dstRate);
    const out: Float32Array[] = [];
    let off = 0;
    let k = 0;
    const totalFrames = src.length >>> 1;
    while (off < totalFrames) {
        const n = Math.min(chunkFrames[k % chunkFrames.length]!, totalFrames - off);
        out.push(rs.push(src.subarray(off * 2, (off + n) * 2)));
        off += n;
        k++;
    }
    out.push(rs.flush());
    let total = 0;
    for (const p of out) total += p.length;
    const joined = new Float32Array(total);
    let w = 0;
    for (const p of out) { joined.set(p, w); w += p.length; }
    return joined;
}

// Max absolute per-sample difference between two equal-length float buffers.
function maxDiff(a: Float32Array, b: Float32Array): number {
    assert.equal(a.length, b.length, `length mismatch ${a.length} vs ${b.length}`);
    let m = 0;
    for (let i = 0; i < a.length; i++) {
        const d = Math.abs(a[i]! - b[i]!);
        if (d > m) m = d;
    }
    return m;
}

for (const [srcRate, label] of [[22050, 'Piper 22050'], [24000, 'Kokoro 24000']] as const) {
    test(`streamed resample matches whole-buffer resampleStereoF32 (${label})`, () => {
        const src = sineStereo(2000);
        const whole = resampleStereoF32(src, srcRate, 48000);
        // A mix of chunk sizes including small, prime, and large.
        for (const sizes of [[1], [1, 2, 3], [97, 193, 389], [500], [1, 999]]) {
            const streamed = streamResample(src, srcRate, 48000, sizes);
            assert.equal(streamed.length, whole.length, `length for chunks ${sizes.join(',')}`);
            // Same interpolation math; cross-chunk continuity makes it sample-identical
            // up to float rounding.
            assert.ok(maxDiff(streamed, whole) <= 1e-5, `maxDiff for chunks ${sizes.join(',')}`);
        }
    });
}

test('identity passthrough when src == dst rate', () => {
    const src = sineStereo(500);
    const rs = new StreamingResampler(48000, 48000);
    const pushed = rs.push(src);
    assert.deepEqual(pushed, src);
    assert.equal(rs.flush().length, 0);
});

test('tolerates a trailing odd sample by frame-aligning the chunk', () => {
    const rs = new StreamingResampler(22050, 48000);
    const frame = sineStereo(100);
    const stray = Float32Array.from([...frame, 0.123]); // one extra mono sample
    // Must not throw; output is whole frames only.
    const a = rs.push(stray);
    const b = rs.flush();
    assert.equal((a.length + b.length) % 2, 0);
    assert.ok(a.length + b.length > 0);
});

test('empty input produces no output and no throw', () => {
    const rs = new StreamingResampler(22050, 48000);
    assert.equal(rs.push(new Float32Array(0)).length, 0);
    assert.equal(rs.flush().length, 0);
});
