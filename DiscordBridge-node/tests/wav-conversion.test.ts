import { test, before } from 'node:test';
import { strict as assert } from 'node:assert';

import { resampleStereoF32 } from '../src/discord-host.js';
import { initResampler } from '../src/resample.js';

// resampleStereoF32 is r8brain-backed; the module must be loaded first (mirrors
// the bridge, which awaits initResampler() before BRIDGE_READY).
before(async () => { await initResampler(); });

// Build interleaved float32 stereo from a flat sample list.
function f32(samples: number[]): Float32Array {
    return Float32Array.from(samples);
}

test('resampleStereoF32: srcRate == dstRate returns input verbatim', () => {
    const pcm = f32([0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8]); // 4 stereo frames
    const out = resampleStereoF32(pcm, 48000, 48000);
    assert.equal(out, pcm);
});

test('resampleStereoF32: 44.1k → 48k stretches frame count by the exact ratio', () => {
    // 441 input frames at 44.1k = 10 ms; expect exactly 480 frames at 48k.
    const frames = 441;
    const pcm = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        pcm[i * 2] = 0.3 * Math.sin((2 * Math.PI * 1000 * i) / 44100);
        pcm[i * 2 + 1] = pcm[i * 2]!;
    }
    const out = resampleStereoF32(pcm, 44100, 48000);
    assert.equal(out.length / 2, 480);
});

test('resampleStereoF32: 48k → 24k halves the frame count exactly', () => {
    const frames = 480;
    const pcm = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        pcm[i * 2] = 0.3 * Math.sin((2 * Math.PI * 1000 * i) / 48000);
        pcm[i * 2 + 1] = pcm[i * 2]!;
    }
    const out = resampleStereoF32(pcm, 48000, 24000);
    assert.equal(out.length / 2, 240);
});

test('resampleStereoF32: channels stay independent (constant L/R offset preserved)', () => {
    // Right = Left + 0.1 (a constant DC offset between channels). A linear,
    // per-channel resampler must keep that offset and never bleed one channel into
    // the other. Checked in the interior, away from the filter's edge ramps.
    const frames = 4000;
    const pcm = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        const l = 0.3 * Math.sin((2 * Math.PI * 200 * i) / 44100);
        pcm[i * 2] = l;
        pcm[i * 2 + 1] = l + 0.1;
    }
    const out = resampleStereoF32(pcm, 44100, 48000);
    const outFrames = out.length / 2;
    const guard = 300; // skip edge transients
    for (let i = guard; i < outFrames - guard; i++) {
        const sep = out[i * 2 + 1]! - out[i * 2]!;
        assert.ok(Math.abs(sep - 0.1) < 0.01, `channel separation drifted at ${i}: ${sep}`);
    }
});
