import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { PcmMixer } from '../src/pcm-mixer.js';
import { decodeFileToFinalPcm } from '../src/discord-host.js';
import { declick, declickIn, declickOut, FADE_IN_FRAMES, FADE_OUT_FRAMES } from '../src/declick.js';

// Fix verification for the "subtle click at the start/end of a sound" artifact.
//
// Mechanism: a decoded clip frequently starts or ends on a large non-zero
// sample (a tone cut mid-cycle, a lossy decoder's first/last frame). The mixer
// feeds ONE long-lived AudioResource and switches to digital silence (0x0000)
// inline with no fade, so that one-sample step from/to e.g. +0.40 is a
// full-amplitude discontinuity the Opus encoder reproduces as a click.
// `declick` ramps each clip's edges to/from zero so there is no step.
//
// The pipeline currency is interleaved float32 stereo; the mixer's output (and
// only its output) is int16 — so the mixer-boundary assertion below reads int16.

const FIX = join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'audio');
const FRAME_SAMPLES = 2; // float32 stereo

function constStereo(value: number, frames: number): Float32Array {
    const buf = new Float32Array(frames * FRAME_SAMPLES);
    for (let i = 0; i < frames; i++) {
        buf[i * FRAME_SAMPLES] = value;
        buf[i * FRAME_SAMPLES + 1] = value;
    }
    return buf;
}

// Left-channel sample at a frame for a float32 interleaved buffer.
function frameL(samples: Float32Array, frame: number): number {
    return samples[frame * FRAME_SAMPLES]!;
}

// Largest absolute sample-to-sample step (left channel) across an int16 mixer
// output buffer, with the frame index where it occurs.
function maxStepLInt16(pcm: Buffer): { step: number; frame: number } {
    let step = 0;
    let frame = 0;
    let prev = pcm.readInt16LE(0);
    const frames = pcm.length >>> 2;
    for (let f = 1; f < frames; f++) {
        const s = pcm.readInt16LE(f * 4);
        const d = Math.abs(s - prev);
        if (d > step) { step = d; frame = f; }
        prev = s;
    }
    return { step, frame };
}

test('declick: middle is untouched, edges ramp from/to ~0', () => {
    const amplitude = 0.375;
    const frames = 4000; // comfortably longer than fadeIn + fadeOut
    const out = declick(constStereo(amplitude, frames));

    assert.equal(out.length, frames * FRAME_SAMPLES, 'length preserved');

    // Middle (between the two ramps) is bit-exact.
    const mid = (FADE_IN_FRAMES + (frames - FADE_OUT_FRAMES)) >> 1;
    assert.equal(frameL(out, mid), amplitude, 'middle sample unchanged');
    assert.equal(frameL(out, FADE_IN_FRAMES), amplitude, 'first full-gain frame');
    assert.equal(frameL(out, frames - FADE_OUT_FRAMES - 1), amplitude, 'last full-gain frame');

    // Edges start and end near zero (a click trigger reduced to a soft ramp).
    assert.ok(Math.abs(frameL(out, 0)) < amplitude / 10, `onset ramps from ~0 (got ${frameL(out, 0)})`);
    assert.ok(Math.abs(frameL(out, frames - 1)) < amplitude / 10, `tail ramps to ~0 (got ${frameL(out, frames - 1)})`);

    // Fade-in monotonic up, fade-out monotonic down.
    for (let f = 1; f < FADE_IN_FRAMES; f++) {
        assert.ok(frameL(out, f) >= frameL(out, f - 1), `fade-in non-decreasing at ${f}`);
    }
    for (let f = frames - FADE_OUT_FRAMES + 1; f < frames; f++) {
        assert.ok(frameL(out, f) <= frameL(out, f - 1), `fade-out non-increasing at ${f}`);
    }
});

test('declick: does not mutate the input buffer', () => {
    const input = constStereo(0.27, 1000);
    const copy = input.slice();
    declick(input);
    assert.deepEqual(input, copy, 'input untouched (callers share WavCache buffers)');
});

test('declick: a clip shorter than fadeIn+fadeOut still starts and ends near zero', () => {
    const amplitude = 0.375;
    const out = declick(constStereo(amplitude, 50)); // 50 < 96 + 240, ramps overlap
    assert.equal(out.length, 50 * FRAME_SAMPLES);
    assert.ok(Math.abs(frameL(out, 0)) < amplitude, 'onset attenuated');
    assert.ok(Math.abs(frameL(out, 49)) < amplitude / 4, 'tail near zero');
});

test('declicked clip has no full-amplitude step at the mixer silence boundaries', () => {
    // 1.5 chunks of constant +0.375 — the realistic case where the clip ends
    // mid-chunk. Undeclicked, the mixer steps 12000 -> 0 (int16) at onset and tail.
    const amplitude = 0.375;
    const m = new PcmMixer();
    m.addVoice(declick(constStereo(amplitude, 1440)));
    const out = Buffer.concat([m._mixOneChunk(), m._mixOneChunk()]);

    const { step } = maxStepLInt16(out);
    // Old hard step was the full ~12000 (int16); the ramps cap any single step
    // well under 800 (≈ amplitude / smallest fade-frame count).
    assert.ok(step < 800, `max boundary step should be small after declick, got ${step}`);
});

// ----------------------------------------------------------------------------
// Streaming edge declick: declickIn (first chunk) / declickOut (last chunk)
// ----------------------------------------------------------------------------

test('declickIn ramps the onset only, leaves the tail verbatim', () => {
    const amp = 0.375;
    const frames = 4000;
    const out = declickIn(constStereo(amp, frames));
    assert.ok(Math.abs(frameL(out, 0)) < amp / 10, `onset ramps from ~0 (got ${frameL(out, 0)})`);
    assert.equal(frameL(out, FADE_IN_FRAMES), amp, 'first full-gain frame after fade-in');
    // Tail untouched — no fade-out.
    assert.equal(frameL(out, frames - 1), amp, 'tail is verbatim (no fade-out)');
});

test('declickOut ramps the tail only, leaves the onset verbatim', () => {
    const amp = 0.375;
    const frames = 4000;
    const out = declickOut(constStereo(amp, frames));
    assert.equal(frameL(out, 0), amp, 'onset is verbatim (no fade-in)');
    assert.equal(frameL(out, frames - FADE_OUT_FRAMES - 1), amp, 'last full-gain frame before fade-out');
    assert.ok(Math.abs(frameL(out, frames - 1)) < amp / 10, `tail ramps to ~0 (got ${frameL(out, frames - 1)})`);
});

test('declickIn(first) + middles + declickOut(last) == declick(whole)', () => {
    // The streaming holdback fades the first emitted chunk's onset and the last
    // chunk's tail; interior chunks are verbatim. With chunks each longer than
    // both ramps, the concatenation must equal a single whole-buffer declick.
    const amp = 0.25;
    const a = constStereo(amp, 1000); // first
    const b = constStereo(amp, 1000); // middle (verbatim)
    const c = constStereo(amp, 1000); // last
    const streamed = Float32Array.from([...declickIn(a), ...b, ...declickOut(c)]);
    const whole = declick(Float32Array.from([...a, ...b, ...c]));
    assert.deepEqual(streamed, whole);
});

test('declickIn / declickOut do not mutate the input', () => {
    const input = constStereo(0.25, 1000);
    const copy = input.slice();
    declickIn(input);
    declickOut(input);
    assert.deepEqual(input, copy);
});

test('real fixtures end on non-zero samples; declick ramps them to ~0', async () => {
    // Concrete clips whose decoded edges land far from zero — playing either one
    // produced the click. minEnding is "well clear of zero"; the post-declick
    // edges must be near silence. Thresholds are float fractions of full scale.
    const cases: Array<{ file: string; minEnding: number }> = [
        { file: 'wav-44100-mono-s16.wav', minEnding: 5000 / 32768 },
        { file: 'vorbis-44100-mono.ogg', minEnding: 5000 / 32768 },
    ];
    for (const { file, minEnding } of cases) {
        const pcm = await decodeFileToFinalPcm(join(FIX, file));
        const frames = pcm.length >>> 1;
        const rawLast = Math.abs(frameL(pcm, frames - 1));
        assert.ok(rawLast >= minEnding, `${file} decodes to a click trigger: ends at |${rawLast}|`);

        const dc = declick(pcm);
        assert.ok(Math.abs(frameL(dc, 0)) < 500 / 32768, `${file} onset declicked (got ${frameL(dc, 0)})`);
        assert.ok(Math.abs(frameL(dc, frames - 1)) < 500 / 32768, `${file} tail declicked (got ${frameL(dc, frames - 1)})`);
    }
});
