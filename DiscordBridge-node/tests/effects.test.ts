import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import {
    EFFECT_NAMES,
    applyEffect,
    applyRandomEffect,
    mulberry32,
    type EffectName,
} from '../src/effects.js';

// A short interleaved float32 stereo buffer with a bit of signal in both channels.
function sineStereo(frames: number, freq = 220): Float32Array {
    const buf = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        const s = Math.sin((2 * Math.PI * freq * i) / 48000) * 0.37;
        buf[i * 2] = s;
        buf[i * 2 + 1] = s;
    }
    return buf;
}

function allFinite(buf: Float32Array): boolean {
    for (let i = 0; i < buf.length; i++) {
        if (!Number.isFinite(buf[i]!)) return false;
    }
    return true;
}

test('every effect produces frame-aligned, finite, non-empty output', () => {
    const input = sineStereo(4800); // 100 ms
    for (const name of EFFECT_NAMES) {
        const out = applyEffect(name, input, mulberry32(1));
        assert.equal(out.length % 2, 0, `${name}: output not stereo-frame aligned`);
        assert.ok(out.length > 0, `${name}: output empty`);
        assert.ok(allFinite(out), `${name}: output not finite`);
    }
});

test('effects are deterministic for a fixed rng seed', () => {
    const input = sineStereo(2400);
    for (const name of EFFECT_NAMES) {
        const a = applyEffect(name, input, mulberry32(42));
        const b = applyEffect(name, input, mulberry32(42));
        assert.deepEqual(a, b, `${name}: not deterministic under same seed`);
    }
});

test('echo and reverb extend the buffer with a tail', () => {
    const input = sineStereo(4800);
    assert.ok(applyEffect('echo', input, mulberry32(3)).length > input.length, 'echo should add a tail');
    assert.ok(applyEffect('reverb', input, mulberry32(3)).length > input.length, 'reverb should add a tail');
});

test('tremolo / distortion / muffle preserve length (sample-wise)', () => {
    const input = sineStereo(4800);
    for (const name of ['tremolo', 'distortion', 'muffle'] as EffectName[]) {
        assert.equal(applyEffect(name, input, mulberry32(5)).length, input.length, `${name} changed length`);
    }
});

test('applyRandomEffect returns a known name and a valid buffer', () => {
    const input = sineStereo(2400);
    const r = applyRandomEffect(input, mulberry32(99));
    assert.ok((EFFECT_NAMES as readonly string[]).includes(r.name), `unknown effect name ${r.name}`);
    assert.equal(r.samples.length % 2, 0);
    assert.ok(allFinite(r.samples));
});

test('applyRandomEffect picks deterministically for a fixed seed', () => {
    const input = sineStereo(1200);
    const a = applyRandomEffect(input, mulberry32(7));
    const b = applyRandomEffect(input, mulberry32(7));
    assert.equal(a.name, b.name);
    assert.deepEqual(a.samples, b.samples);
});

test('buffers too short to hold a stereo frame are returned untouched', () => {
    const tiny = Float32Array.from([0.01]); // 1 sample < one stereo frame
    for (const name of EFFECT_NAMES) {
        assert.equal(applyEffect(name, tiny, mulberry32(1)), tiny, `${name} mangled a tiny buffer`);
    }
});

test('silence in -> finite output (no NaN from feedback paths)', () => {
    const silence = new Float32Array(4800 * 2);
    for (const name of EFFECT_NAMES) {
        const out = applyEffect(name, silence, mulberry32(2));
        assert.ok(allFinite(out), `${name} produced non-finite output from silence`);
    }
});
