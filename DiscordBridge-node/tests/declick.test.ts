import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { PcmMixer } from '../src/pcm-mixer.js';
import { decodeFileToFinalPcm } from '../src/discord-host.js';
import { declick, FADE_IN_FRAMES, FADE_OUT_FRAMES } from '../src/declick.js';

// Fix verification for the "subtle click at the start/end of a sound" artifact.
//
// Mechanism: a decoded clip frequently starts or ends on a large non-zero
// sample (a tone cut mid-cycle, a lossy decoder's first/last frame). The mixer
// feeds ONE long-lived AudioResource and switches to digital silence (0x0000)
// inline with no fade, so that one-sample step from/to e.g. +13000 is a
// full-amplitude discontinuity the Opus encoder reproduces as a click.
// `declick` ramps each clip's edges to/from zero so there is no step.

const FIX = join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'audio');
const FRAME_BYTES = 4; // s16le stereo

function constStereo(value: number, frames: number): Buffer {
    const buf = Buffer.alloc(frames * FRAME_BYTES);
    for (let i = 0; i < frames; i++) {
        buf.writeInt16LE(value, i * FRAME_BYTES);
        buf.writeInt16LE(value, i * FRAME_BYTES + 2);
    }
    return buf;
}

// Largest absolute sample-to-sample step (left channel) across a buffer, with
// the frame index where it occurs. An edge click shows up here: a clean signal
// steps by small amounts; a discontinuity spikes.
function maxStepL(pcm: Buffer): { step: number; frame: number } {
    let step = 0;
    let frame = 0;
    let prev = pcm.readInt16LE(0);
    const frames = pcm.length >>> 2;
    for (let f = 1; f < frames; f++) {
        const s = pcm.readInt16LE(f * FRAME_BYTES);
        const d = Math.abs(s - prev);
        if (d > step) { step = d; frame = f; }
        prev = s;
    }
    return { step, frame };
}

function frameL(pcm: Buffer, frame: number): number {
    return pcm.readInt16LE(frame * FRAME_BYTES);
}

test('declick: middle is untouched, edges ramp from/to ~0', () => {
    const amplitude = 12000;
    const frames = 4000; // comfortably longer than fadeIn + fadeOut
    const out = declick(constStereo(amplitude, frames));

    assert.equal(out.length, frames * FRAME_BYTES, 'length preserved');

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
    const input = constStereo(9000, 1000);
    const copy = Buffer.from(input);
    declick(input);
    assert.equal(Buffer.compare(input, copy), 0, 'input untouched (callers share WavCache buffers)');
});

test('declick: a clip shorter than fadeIn+fadeOut still starts and ends near zero', () => {
    const amplitude = 12000;
    const out = declick(constStereo(amplitude, 50)); // 50 < 96 + 240, ramps overlap
    assert.equal(out.length, 50 * FRAME_BYTES);
    assert.ok(Math.abs(frameL(out, 0)) < amplitude, 'onset attenuated');
    assert.ok(Math.abs(frameL(out, 49)) < amplitude / 4, 'tail near zero');
});

test('declicked clip has no full-amplitude step at the mixer silence boundaries', () => {
    // 1.5 chunks of constant +12000 — the realistic case where the clip ends
    // mid-chunk. Undeclicked, the mixer steps 12000 -> 0 at onset and tail.
    const amplitude = 12000;
    const m = new PcmMixer();
    m.addVoice(declick(constStereo(amplitude, 1440)));
    const out = Buffer.concat([m._mixOneChunk(), m._mixOneChunk()]);

    const { step } = maxStepL(out);
    // Old hard step was the full 12000; the ramps cap any single step well under
    // 800 (≈ amplitude / smallest fade-frame count).
    assert.ok(step < 800, `max boundary step should be small after declick, got ${step}`);
});

test('real fixtures end on non-zero samples; declick ramps them to ~0', async () => {
    // Concrete clips whose decoded edges land far from zero — playing either one
    // produced the click. minEdge is "well clear of zero"; the post-declick edges
    // must be near silence.
    const cases: Array<{ file: string; minEnding: number }> = [
        { file: 'wav-44100-mono-s16.wav', minEnding: 5000 },
        { file: 'vorbis-44100-mono.ogg', minEnding: 5000 },
    ];
    for (const { file, minEnding } of cases) {
        const pcm = await decodeFileToFinalPcm(join(FIX, file));
        const frames = pcm.length >>> 2;
        const rawLast = Math.abs(frameL(pcm, frames - 1));
        assert.ok(rawLast >= minEnding, `${file} decodes to a click trigger: ends at |${rawLast}|`);

        const dc = declick(pcm);
        assert.ok(Math.abs(frameL(dc, 0)) < 500, `${file} onset declicked (got ${frameL(dc, 0)})`);
        assert.ok(Math.abs(frameL(dc, frames - 1)) < 500, `${file} tail declicked (got ${frameL(dc, frames - 1)})`);
    }
});
