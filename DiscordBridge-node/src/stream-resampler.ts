// Stateful linear-interpolation resampler for 16-bit signed LE stereo, fed one
// chunk at a time. The stateless resampleStereo16 (discord-host.ts) fabricates a
// fake "last frame" at each buffer edge (its nextIdx clamp), so resampling a
// stream chunk-by-chunk independently would inject a tiny discontinuity at every
// chunk boundary — an audible click after Opus. This carries the source-frame
// window and the absolute output phase across calls, so the concatenated output
// is byte-identical to resampling the whole utterance at once.
//
// Used by the streaming TTS path: each onProgress chunk (already mono->stereo at
// the model rate) goes through push(); flush() emits the final tail after synth
// completes. Same interpolation math and rounding as resampleStereo16.

const FRAME_BYTES = 4; // interleaved s16le stereo

export class StreamingResampler {
    private readonly ratio: number;       // dst / src
    private readonly identity: boolean;   // src == dst: passthrough
    private src = Buffer.alloc(0);        // pending source frames (window)
    private baseFrame = 0;                // absolute src-frame index of src[0]
    private outFrame = 0;                 // next output frame index to emit

    constructor(srcRate: number, dstRate = 48000) {
        this.ratio = dstRate / srcRate;
        this.identity = srcRate === dstRate;
    }

    // Feed one source chunk; returns the output frames that are now fully
    // resolvable (both interpolation neighbors present). Trailing partial output
    // is held until the next push/flush.
    push(chunk: Buffer): Buffer {
        if (this.identity) {
            const a = chunk.length & ~(FRAME_BYTES - 1);
            return a === chunk.length ? chunk : chunk.subarray(0, a);
        }
        const aligned = chunk.length & ~(FRAME_BYTES - 1);
        if (aligned > 0) {
            const part = chunk.subarray(0, aligned);
            this.src = this.src.length ? Buffer.concat([this.src, part]) : Buffer.from(part);
        }
        return this._produce(false);
    }

    // Emit the final tail, clamping the last interpolation neighbor to the last
    // source frame (exactly as resampleStereo16 does at the buffer end).
    flush(): Buffer {
        if (this.identity) return Buffer.alloc(0);
        return this._produce(true);
    }

    private _produce(final: boolean): Buffer {
        const avail = this.src.length >>> 2;           // frames currently held
        if (avail === 0) {
            if (final) this.src = Buffer.alloc(0);
            return Buffer.alloc(0);
        }
        const lastAbs = this.baseFrame + avail - 1;    // absolute index of last held frame
        const lastLocal = avail - 1;

        // On flush, produce exactly floor(totalSrcFrames * ratio) output frames —
        // the same count resampleStereo16 yields — clamping the right neighbor at
        // the very end. Mid-stream there's no fixed count: emit while the right
        // interpolation neighbor is actually present and stop otherwise. The
        // per-frame availability guard (not a precomputed bound) is what keeps FP
        // rounding of i/ratio vs lastAbs*ratio from ever indexing past the window.
        const finalCap = Math.floor((lastAbs + 1) * this.ratio);
        // Generous upper bound for preallocation; the loop emits <= this.
        const maxN = Math.max(0, Math.ceil((lastAbs + 1) * this.ratio) - this.outFrame) + 2;
        const out = Buffer.alloc(maxN * FRAME_BYTES);

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
            const ao = a * FRAME_BYTES;
            const bo = b * FRAME_BYTES;
            const l1 = this.src.readInt16LE(ao);
            const l2 = this.src.readInt16LE(bo);
            const r1 = this.src.readInt16LE(ao + 2);
            const r2 = this.src.readInt16LE(bo + 2);
            const ko = k * FRAME_BYTES;
            out.writeInt16LE(Math.round(l1 + (l2 - l1) * frac), ko);
            out.writeInt16LE(Math.round(r1 + (r2 - r1) * frac), ko + 2);
            k++;
        }
        this.outFrame += k;

        if (final) {
            this.src = Buffer.alloc(0);
        } else {
            // Keep from the source frame the next output interpolates from (its
            // left neighbor); drop everything already behind it.
            const keepFromAbs = Math.floor(this.outFrame / this.ratio);
            const dropLocal = keepFromAbs - this.baseFrame;
            if (dropLocal > 0) {
                this.src = this.src.subarray(dropLocal * FRAME_BYTES);
                this.baseFrame = keepFromAbs;
            }
        }
        return out.subarray(0, k * FRAME_BYTES); // maxN over-estimates, so k < maxN
    }
}
