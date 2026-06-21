// Stateful linear-interpolation resampler for interleaved float32 stereo, fed one
// chunk at a time. The stateless resampleStereoF32 (discord-host.ts) fabricates a
// fake "last frame" at each buffer edge (its nextIdx clamp), so resampling a
// stream chunk-by-chunk independently would inject a tiny discontinuity at every
// chunk boundary — an audible click after Opus. This carries the source-frame
// window and the absolute output phase across calls, so the concatenated output
// is sample-identical to resampling the whole utterance at once.
//
// Used by the streaming TTS path: each onProgress chunk (already mono->stereo at
// the model rate) goes through push(); flush() emits the final tail after synth
// completes. Same interpolation math as resampleStereoF32.

const FRAME_SAMPLES = 2; // interleaved float32 stereo

function concatF32(a: Float32Array, b: Float32Array): Float32Array {
    if (a.length === 0) return b;
    if (b.length === 0) return a;
    const out = new Float32Array(a.length + b.length);
    out.set(a, 0);
    out.set(b, a.length);
    return out;
}

export class StreamingResampler {
    private readonly ratio: number;       // dst / src
    private readonly identity: boolean;   // src == dst: passthrough
    private src: Float32Array = new Float32Array(0); // pending source frames (window)
    private baseFrame = 0;                // absolute src-frame index of src[0]
    private outFrame = 0;                 // next output frame index to emit

    constructor(srcRate: number, dstRate = 48000) {
        this.ratio = dstRate / srcRate;
        this.identity = srcRate === dstRate;
    }

    // Feed one source chunk; returns the output frames that are now fully
    // resolvable (both interpolation neighbors present). Trailing partial output
    // is held until the next push/flush.
    push(chunk: Float32Array): Float32Array {
        if (this.identity) {
            const a = chunk.length & ~(FRAME_SAMPLES - 1);
            return a === chunk.length ? chunk : chunk.subarray(0, a);
        }
        const aligned = chunk.length & ~(FRAME_SAMPLES - 1);
        if (aligned > 0) {
            this.src = concatF32(this.src, aligned === chunk.length ? chunk : chunk.subarray(0, aligned));
        }
        return this._produce(false);
    }

    // Emit the final tail, clamping the last interpolation neighbor to the last
    // source frame (exactly as resampleStereoF32 does at the buffer end).
    flush(): Float32Array {
        if (this.identity) return new Float32Array(0);
        return this._produce(true);
    }

    private _produce(final: boolean): Float32Array {
        const avail = this.src.length >>> 1;           // frames currently held
        if (avail === 0) {
            if (final) this.src = new Float32Array(0);
            return new Float32Array(0);
        }
        const lastAbs = this.baseFrame + avail - 1;    // absolute index of last held frame
        const lastLocal = avail - 1;

        // On flush, produce exactly floor(totalSrcFrames * ratio) output frames —
        // the same count resampleStereoF32 yields — clamping the right neighbor at
        // the very end. Mid-stream there's no fixed count: emit while the right
        // interpolation neighbor is actually present and stop otherwise. The
        // per-frame availability guard (not a precomputed bound) is what keeps FP
        // rounding of i/ratio vs lastAbs*ratio from ever indexing past the window.
        const finalCap = Math.floor((lastAbs + 1) * this.ratio);
        // Generous upper bound for preallocation; the loop emits <= this.
        const maxN = Math.max(0, Math.ceil((lastAbs + 1) * this.ratio) - this.outFrame) + 2;
        const out = new Float32Array(maxN * FRAME_SAMPLES);

        let k = 0;
        for (;;) {
            const i = this.outFrame + k;
            if (final && i >= finalCap) break;
            const srcPos = i / this.ratio;
            const srcIdx = Math.floor(srcPos);
            if (!final && srcIdx + 1 > lastAbs) break; // right neighbor not here yet
            const nextIdx = srcIdx + 1;
            const frac = srcPos - srcIdx;
            // Clamp reads into the held window — a no-op mid-stream (the guard
            // above guarantees in-range) and the end-of-stream right-neighbor
            // clamp on flush.
            let a = srcIdx - this.baseFrame;
            let b = nextIdx - this.baseFrame;
            if (a < 0) a = 0; else if (a > lastLocal) a = lastLocal;
            if (b < 0) b = 0; else if (b > lastLocal) b = lastLocal;
            const ao = a * FRAME_SAMPLES;
            const bo = b * FRAME_SAMPLES;
            const l1 = this.src[ao]!;
            const l2 = this.src[bo]!;
            const r1 = this.src[ao + 1]!;
            const r2 = this.src[bo + 1]!;
            const ko = k * FRAME_SAMPLES;
            out[ko] = l1 + (l2 - l1) * frac;
            out[ko + 1] = r1 + (r2 - r1) * frac;
            k++;
        }
        this.outFrame += k;

        if (final) {
            this.src = new Float32Array(0);
        } else {
            // Keep from the source frame the next output interpolates from (its
            // left neighbor); drop everything already behind it.
            const keepFromAbs = Math.floor(this.outFrame / this.ratio);
            const dropLocal = keepFromAbs - this.baseFrame;
            if (dropLocal > 0) {
                this.src = this.src.subarray(dropLocal * FRAME_SAMPLES);
                this.baseFrame = keepFromAbs;
            }
        }
        return out.subarray(0, k * FRAME_SAMPLES); // maxN over-estimates, so k < maxN
    }
}
