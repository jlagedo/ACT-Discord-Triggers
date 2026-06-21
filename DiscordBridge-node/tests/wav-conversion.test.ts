import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { resampleStereoF32 } from '../src/discord-host.js';

// Build interleaved float32 stereo from a flat sample list.
function f32(samples: number[]): Float32Array {
    return Float32Array.from(samples);
}

function readStereo(s: Float32Array): Array<[number, number]> {
    const out: Array<[number, number]> = [];
    for (let i = 0; i < s.length; i += 2) {
        out.push([s[i]!, s[i + 1]!]);
    }
    return out;
}

test('resampleStereoF32: srcRate == dstRate returns input verbatim', () => {
    const pcm = f32([0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8]); // 4 stereo frames
    const out = resampleStereoF32(pcm, 48000, 48000);
    assert.equal(out, pcm);
});

test('resampleStereoF32: 44.1k → 48k stretches frame count by ratio', () => {
    // 441 input frames at 44.1k = 10 ms; expect ~480 frames at 48k.
    const frames = 441;
    const pcm = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        pcm[i * 2] = i / frames;
        pcm[i * 2 + 1] = -i / frames;
    }
    const out = resampleStereoF32(pcm, 44100, 48000);
    const outFrames = out.length / 2;
    assert.equal(outFrames, 480);
});

test('resampleStereoF32: monotonic ramp stays monotonic after resample (no obvious aliasing)', () => {
    // Linear ramp from 0..1 across 1000 frames at 44.1k.
    const frames = 1000;
    const pcm = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        pcm[i * 2] = i / frames;
        pcm[i * 2 + 1] = i / frames;
    }
    const out = resampleStereoF32(pcm, 44100, 48000);
    const outFrames = out.length / 2;
    let prev = -1;
    for (let i = 0; i < outFrames; i++) {
        const v = out[i * 2]!;
        // Ramp must never decrease (allow equal: trailing samples clamp to last src).
        assert.ok(v >= prev, `non-monotonic at frame ${i}: ${prev} → ${v}`);
        prev = v;
    }
});

test('resampleStereoF32: 48k → 24k halves frame count and preserves channel separation', () => {
    // 8 input frames; expect 4 output frames. Left ramps 0.10..0.17, right 0.20..0.27.
    const pcm = f32([
        0.10, 0.20,
        0.11, 0.21,
        0.12, 0.22,
        0.13, 0.23,
        0.14, 0.24,
        0.15, 0.25,
        0.16, 0.26,
        0.17, 0.27,
    ]);
    const out = resampleStereoF32(pcm, 48000, 24000);
    const stereo = readStereo(out);
    assert.equal(stereo.length, 4);
    // Left ramp stays in left channel, right ramp stays in right channel.
    for (const [l, r] of stereo) {
        assert.ok(l < r, `expected L < R, got L=${l} R=${r}`);
        assert.ok(r - l > 0.09 && r - l < 0.11, `expected ~0.1 channel separation, got ${r - l}`);
    }
});
