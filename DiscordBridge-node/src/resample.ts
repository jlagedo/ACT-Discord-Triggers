// High-quality sample-rate conversion for the bridge, backed by r8brain-wasm
// (r8brain-free-src compiled to WebAssembly). Replaces the former linear
// interpolation: r8brain is a polyphase windowed-sinc converter (SINAD ~184-238
// dB vs linear's ~9-30), so every file and every TTS callout is band-limited and
// image-free instead of muffled-and-grainy.
//
// The pipeline currency is interleaved float32 stereo; r8brain is mono-per-object
// and works in float64, so this module owns the de-interleave / channel-split and
// the float32<->float64 conversion at its edges. Three entry points:
//   - resampleStereoF32  — true stereo (one Resampler per channel), file path.
//   - resampleMono       — single mono buffer, buffered TTS / probe.
//   - MonoStreamResampler — stateful mono stream, streaming TTS (replaces the old
//                           StreamingResampler); r8brain keeps cross-chunk
//                           continuity internally so concatenated output is
//                           sample-exact to a whole-buffer resample.
//
// Latency note: file/mono paths are one-shot and cached, so they use a tight
// transition band (full passband, latency irrelevant). The streaming path uses a
// wider band that drops first-output latency to ~18 ms (speech sits well inside
// the resulting passband) so live callouts start promptly.

// Type-only import (erased at compile time); the runtime values come from the
// dynamic import in initResampler so the package stays external + on-disk ESM
// (its .wasm is located via import.meta.url, which only resolves for real files).
import type { R8brainModule, Resampler as R8Resampler } from 'r8brain-wasm';

type R8brainApi = typeof import('r8brain-wasm');

const TARGET_SAMPLE_RATE = 48000;
// Max input samples per processInto call; also sizes r8brain's internal buffers.
// Large enough that whole files / synth chunks pass in a handful of blocks.
const MAX_IN = 8192;
// Transition band (percent of spectral space). Tight for one-shot paths (full
// passband), wide for streaming (shorter filter -> ~18 ms first-output latency).
const FILE_TRANSBAND = 2.0;
const STREAM_TRANSBAND = 6.0;
// Headroom over the nominal per-block output (ceil(maxIn*ratio)) for the scratch
// buffer, so processInto never overflows it on a hot block.
const OUT_MARGIN = 256;

let api: R8brainApi | null = null;
let mod: R8brainModule | null = null;
let initPromise: Promise<void> | null = null;

// Load + instantiate the WASM module once. Awaited at bridge startup (before
// BRIDGE_READY) so the sync resample functions below can assume it's ready and a
// staging/load failure fails the build self-test loudly. Also pre-warms the
// construct/process JIT so the first real callout pays no one-time cost.
export async function initResampler(): Promise<void> {
    if (mod) return;
    if (!initPromise) {
        initPromise = (async () => {
            api = await import('r8brain-wasm');
            mod = await api.init();
            warmup();
        })();
    }
    await initPromise;
}

// The module compile is paid above, but the first run through each resample path
// still JIT-warms its code (~1-3 ms once). Run a throwaway resample through the
// real exported paths at startup so that cost lands here, not on the first
// callout. mod/api are already assigned, so the sync paths below work.
function warmup(): void {
    try {
        const tone = new Float32Array(512);
        for (let i = 0; i < tone.length; i++) tone[i] = Math.sin((2 * Math.PI * 440 * i) / 24000);
        // File/one-shot path (transBand 2.0).
        resampleMono(tone, 44100, TARGET_SAMPLE_RATE);
        // Streaming path (transBand 6.0): construct + push + flush, both TTS rates.
        for (const src of [22050, 24000]) {
            const rs = new MonoStreamResampler(src, TARGET_SAMPLE_RATE);
            rs.push(tone);
            rs.flush();
        }
    } catch {
        // Warm-up is best-effort; a failure here must not block startup.
    }
}

function requireMod(): { m: R8brainModule; a: R8brainApi } {
    if (!mod || !api) {
        throw new Error('resampler not initialized; call initResampler() first');
    }
    return { m: mod, a: api };
}

function f64ToF32(src: Float64Array): Float32Array {
    const out = new Float32Array(src.length);
    for (let i = 0; i < src.length; i++) out[i] = src[i]!;
    return out;
}

function concatF32(parts: Float32Array[]): Float32Array {
    if (parts.length === 1) return parts[0]!;
    let total = 0;
    for (const p of parts) total += p.length;
    const out = new Float32Array(total);
    let off = 0;
    for (const p of parts) { out.set(p, off); off += p.length; }
    return out;
}

// Resample a whole mono buffer at `transBand`, draining the resampler's latency
// tail and returning exactly floor(input.length * dstRate/srcRate) samples — the
// same length contract the old linear converter held (callers depend on it for
// duration math and the WAV sink).
function resampleMonoFull(
    input: Float32Array, srcRate: number, dstRate: number, transBand: number,
): Float32Array {
    if (srcRate === dstRate) return input;
    const { m, a } = requireMod();
    const ratio = dstRate / srcRate;
    const expected = Math.floor(input.length * ratio);
    const result = new Float64Array(expected);
    if (expected === 0) return new Float32Array(0);

    const rs = new a.Resampler(m, {
        srcRate, dstRate, maxInLen: MAX_IN, transBand, resolution: a.Resolution.R24,
    });
    const scratch = new Float64Array(Math.ceil(MAX_IN * ratio) + OUT_MARGIN);
    const inBuf = new Float64Array(MAX_IN);
    let written = 0;
    try {
        for (let off = 0; off < input.length && written < expected; off += MAX_IN) {
            const len = Math.min(MAX_IN, input.length - off);
            for (let i = 0; i < len; i++) inBuf[i] = input[off + i]!;
            const n = rs.processInto(inBuf.subarray(0, len), scratch);
            const take = Math.min(n, expected - written);
            for (let i = 0; i < take; i++) result[written + i] = scratch[i]!;
            written += take;
        }
        // Drain the latency tail with silence until the full expected count is out.
        if (written < expected) {
            const zeros = new Float64Array(MAX_IN);
            let guard = 0;
            while (written < expected && guard++ < 100000) {
                const n = rs.processInto(zeros, scratch);
                const take = Math.min(n, expected - written);
                for (let i = 0; i < take; i++) result[written + i] = scratch[i]!;
                written += take;
            }
        }
    } finally {
        rs.destroy();
    }
    return f64ToF32(result);
}

// Interleaved float32 stereo -> 48k interleaved float32 stereo. True stereo: each
// channel runs through its own Resampler (file sources have L != R). Sync after
// initResampler(). transBand 2.0 (one-shot/cached, full passband).
export function resampleStereoF32(samples: Float32Array, srcRate: number, dstRate: number): Float32Array {
    if (srcRate === dstRate) return samples;
    const frames = samples.length >>> 1;
    const left = new Float32Array(frames);
    const right = new Float32Array(frames);
    for (let i = 0; i < frames; i++) {
        left[i] = samples[i * 2]!;
        right[i] = samples[i * 2 + 1]!;
    }
    const lr = resampleMonoFull(left, srcRate, dstRate, FILE_TRANSBAND);
    const rr = resampleMonoFull(right, srcRate, dstRate, FILE_TRANSBAND);
    const n = Math.min(lr.length, rr.length);
    const out = new Float32Array(n * 2);
    for (let i = 0; i < n; i++) {
        out[i * 2] = lr[i]!;
        out[i * 2 + 1] = rr[i]!;
    }
    return out;
}

// Mono float32 -> 48k mono float32. One Resampler; transBand 2.0. Used by the
// buffered TTS path and the probe tool, which duplicate to stereo afterwards.
export function resampleMono(mono: Float32Array, srcRate: number, dstRate: number): Float32Array {
    return resampleMonoFull(mono, srcRate, dstRate, FILE_TRANSBAND);
}

// Stateful streaming mono resampler for the TTS path. push() feeds a synth chunk
// and returns the mono samples now resolvable; flush() drains the latency tail to
// the exact length and releases the resampler. transBand 6.0 (low latency). The
// caller duplicates the mono output to stereo (folding the baked voice gain).
export class MonoStreamResampler {
    private readonly identity: boolean;
    private readonly ratio: number;
    private rs: R8Resampler | null = null;
    private scratch: Float64Array = new Float64Array(0);
    private inBuf: Float64Array = new Float64Array(0);
    private totalIn = 0;   // source samples fed so far
    private emitted = 0;   // output samples returned so far

    constructor(srcRate: number, dstRate: number = TARGET_SAMPLE_RATE) {
        this.identity = srcRate === dstRate;
        this.ratio = dstRate / srcRate;
        if (!this.identity) {
            const { m, a } = requireMod();
            this.rs = new a.Resampler(m, {
                srcRate, dstRate, maxInLen: MAX_IN,
                transBand: STREAM_TRANSBAND, resolution: a.Resolution.R24,
            });
            this.scratch = new Float64Array(Math.ceil(MAX_IN * this.ratio) + OUT_MARGIN);
            this.inBuf = new Float64Array(MAX_IN);
        }
    }

    // Feed one mono chunk (any length); returns the resampled mono samples now
    // resolvable. Chunks larger than MAX_IN are split internally so processInto
    // never throws on an oversized block.
    push(mono: Float32Array): Float32Array {
        if (this.identity) return mono;
        if (mono.length === 0) return new Float32Array(0);
        const rs = this.rs!;
        this.totalIn += mono.length;
        const parts: Float32Array[] = [];
        for (let off = 0; off < mono.length; off += MAX_IN) {
            const len = Math.min(MAX_IN, mono.length - off);
            for (let i = 0; i < len; i++) this.inBuf[i] = mono[off + i]!;
            const n = rs.processInto(this.inBuf.subarray(0, len), this.scratch);
            if (n > 0) {
                parts.push(f64ToF32(this.scratch.subarray(0, n)));
                this.emitted += n;
            }
        }
        return parts.length === 0 ? new Float32Array(0) : concatF32(parts);
    }

    // Emit the final tail so the total output is exactly
    // floor(totalIn * dstRate/srcRate) samples, then release the resampler.
    flush(): Float32Array {
        if (this.identity || !this.rs) return new Float32Array(0);
        const rs = this.rs;
        const expected = Math.floor(this.totalIn * this.ratio);
        let remaining = expected - this.emitted;
        const parts: Float32Array[] = [];
        try {
            if (remaining > 0) {
                const zeros = new Float64Array(MAX_IN);
                let guard = 0;
                while (remaining > 0 && guard++ < 100000) {
                    const n = rs.processInto(zeros, this.scratch);
                    if (n > 0) {
                        const take = Math.min(n, remaining);
                        parts.push(f64ToF32(this.scratch.subarray(0, take)));
                        this.emitted += take;
                        remaining -= take;
                    }
                }
            }
        } finally {
            // Release the native resampler even if processInto throws mid-drain,
            // so a synth failure can't leak the WASM resampler.
            rs.destroy();
            this.rs = null;
        }
        return parts.length === 0 ? new Float32Array(0) : concatF32(parts);
    }

    // Release the native resampler without draining. Idempotent — safe to call
    // after flush() (which already released it) or on an error path that never
    // reached flush().
    dispose(): void {
        if (this.rs) {
            this.rs.destroy();
            this.rs = null;
        }
    }
}
