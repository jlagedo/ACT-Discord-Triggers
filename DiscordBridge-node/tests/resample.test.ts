import { test, before } from 'node:test';
import { strict as assert } from 'node:assert';

import {
    initResampler, resampleStereoF32, resampleMono, MonoStreamResampler,
} from '../src/resample.js';

// The r8brain dist is committed in the package, so the module is always present
// (unlike the sherpa-onnx native addon) — no skip gate needed.
before(async () => { await initResampler(); });

function sineMono(n: number, freq: number, rate: number, amp = 0.5): Float32Array {
    const buf = new Float32Array(n);
    for (let i = 0; i < n; i++) buf[i] = amp * Math.sin((2 * Math.PI * freq * i) / rate);
    return buf;
}

function rms(a: Float32Array): number {
    let s = 0;
    for (const v of a) s += v * v;
    return Math.sqrt(s / Math.max(1, a.length));
}

function maxAbs(a: Float32Array): number {
    let m = 0;
    for (const v of a) { const d = Math.abs(v); if (d > m) m = d; }
    return m;
}

function maxDiff(a: Float32Array, b: Float32Array): number {
    assert.equal(a.length, b.length, `length mismatch ${a.length} vs ${b.length}`);
    let m = 0;
    for (let i = 0; i < a.length; i++) {
        const d = Math.abs(a[i]! - b[i]!);
        if (d > m) m = d;
    }
    return m;
}

// Stream a mono buffer through MonoStreamResampler in cycled chunk sizes,
// concatenating push() outputs + the final flush().
function streamMono(src: Float32Array, srcRate: number, dstRate: number, chunkSizes: number[]): Float32Array {
    const rs = new MonoStreamResampler(srcRate, dstRate);
    const parts: Float32Array[] = [];
    let off = 0, k = 0;
    while (off < src.length) {
        const n = Math.min(chunkSizes[k % chunkSizes.length]!, src.length - off);
        parts.push(rs.push(src.subarray(off, off + n)));
        off += n; k++;
    }
    parts.push(rs.flush());
    let total = 0;
    for (const p of parts) total += p.length;
    const out = new Float32Array(total);
    let w = 0;
    for (const p of parts) { out.set(p, w); w += p.length; }
    return out;
}

// The streamed resampler keeps cross-chunk continuity internally, so the output
// is sample-identical regardless of how the input is chunked.
for (const [srcRate, label] of [[22050, 'Piper 22050'], [24000, 'Kokoro 24000']] as const) {
    test(`MonoStreamResampler: chunking is sample-identical (${label})`, () => {
        const src = sineMono(13000, 700, srcRate);
        const whole = streamMono(src, srcRate, 48000, [src.length]); // one big push
        for (const sizes of [[1], [1, 2, 3], [97, 193, 389], [500], [1, 999]]) {
            const streamed = streamMono(src, srcRate, 48000, sizes);
            assert.equal(streamed.length, whole.length, `length for chunks ${sizes.join(',')}`);
            assert.ok(maxDiff(streamed, whole) <= 1e-6, `maxDiff for chunks ${sizes.join(',')}`);
        }
    });

    test(`MonoStreamResampler: exact output length (${label})`, () => {
        const src = sineMono(10000, 1000, srcRate);
        const out = streamMono(src, srcRate, 48000, [333, 4096, 97]);
        const expected = Math.floor((src.length * 48000) / srcRate);
        assert.equal(out.length, expected);
    });
}

test('resampleMono: exact length + energy preserved', () => {
    const src = sineMono(22050, 1000, 22050, 0.5);
    const out = resampleMono(src, 22050, 48000);
    assert.equal(out.length, Math.floor((src.length * 48000) / 22050));
    // A clean sine keeps its RMS through a transparent resample (allow some edge slack).
    assert.ok(Math.abs(rms(out) - rms(src)) < 0.05, `rms ${rms(src)} -> ${rms(out)}`);
});

test('resampleStereoF32: channels are independent (left-only stays left-only)', () => {
    const frames = 8000;
    const interleaved = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        interleaved[i * 2] = 0.5 * Math.sin((2 * Math.PI * 1500 * i) / 22050); // left
        interleaved[i * 2 + 1] = 0;                                            // right silent
    }
    const out = resampleStereoF32(interleaved, 22050, 48000);
    const n = out.length >>> 1;
    const left = new Float32Array(n), right = new Float32Array(n);
    for (let i = 0; i < n; i++) { left[i] = out[i * 2]!; right[i] = out[i * 2 + 1]!; }
    assert.equal(maxAbs(right), 0, 'right channel must stay digital silence');
    assert.ok(rms(left) > 0.1, `left channel should carry the tone (rms=${rms(left)})`);
});

test('identity passthrough when src == dst rate', () => {
    const mono = sineMono(500, 1000, 48000);
    assert.equal(resampleMono(mono, 48000, 48000), mono);
    const rs = new MonoStreamResampler(48000, 48000);
    assert.equal(rs.push(mono), mono);
    assert.equal(rs.flush().length, 0);
    const stereo = new Float32Array(200);
    assert.equal(resampleStereoF32(stereo, 48000, 48000), stereo);
});

test('MonoStreamResampler.push does not throw on a chunk larger than the internal block', () => {
    const rs = new MonoStreamResampler(24000, 48000);
    const big = sineMono(20000, 1000, 24000); // > MAX_IN (8192)
    const a = rs.push(big);
    const b = rs.flush();
    assert.ok(a.length + b.length > 0);
    assert.equal(a.length + b.length, Math.floor((20000 * 48000) / 24000));
});

test('empty input produces no output and no throw', () => {
    const rs = new MonoStreamResampler(22050, 48000);
    assert.equal(rs.push(new Float32Array(0)).length, 0);
    assert.equal(rs.flush().length, 0);
    assert.equal(resampleMono(new Float32Array(0), 22050, 48000).length, 0);
});

test('r8brain module upsamples (direct smoke)', async () => {
    // Dynamic import: the package exports only an ESM ("import") condition, so a
    // static import resolves through tsx's CJS path and fails.
    const { init, Resampler, Resolution } = await import('r8brain-wasm');
    const mod = await init();
    const rs = new Resampler(mod, { srcRate: 24000, dstRate: 48000, maxInLen: 2048, resolution: Resolution.R24 });
    const inb = new Float64Array(2048);
    for (let i = 0; i < 2048; i++) inb[i] = Math.sin((2 * Math.PI * 1000 * i) / 24000);
    const out = new Float64Array(8192);
    let total = 0, fed = 0;
    for (let r = 0; r < 8; r++) { total += rs.processInto(inb, out); fed += 2048; }
    rs.destroy();
    // 24k -> 48k is ~2x; after the latency fill the output is well above 1.5x input.
    assert.ok(total > fed * 1.5, `expected ~2x output, got ${total} from ${fed}`);
});
