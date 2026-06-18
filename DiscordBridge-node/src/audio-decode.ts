// Helpers around the `audio-decode` package. The bridge calls `decode()` (from
// 'audio-decode') directly — it auto-detects the container (wav/mp3/ogg/flac/
// opus/…) via `audio-type` and decodes to planar Float32 PCM, with each codec's
// WASM inlined (no native bindings, nothing to stage). This module only adds
// the (temporary) float->int16 conversion and a startup warm-up.
//
// The float->int16 conversion (`*_PHASE1_SHIM`) is deliberately temporary:
// Phase 3 moves the pipeline to float32 end-to-end and deletes it, letting
// decoded float ride straight into the mixer.
import decode from 'audio-decode';

import * as log from './file-log.js';

function floatToInt16(f: number): number {
    // Match effects.ts: decode is int16/32768, so encode is round(f*32768),
    // hard-clamped to the int16 range (no dither — that arrives in Phase 3a).
    let s = Math.round(f * 32768);
    if (s > 32767) s = 32767;
    else if (s < -32768) s = -32768;
    return s;
}

// TEMPORARY Phase-1 shim: planar Float32 channels -> interleaved 16-bit signed
// LE stereo Buffer at the SOURCE sample rate (the caller resamples to 48k).
// Downmix: mono duplicates L=R; stereo passes through; >2ch keeps the first two
// channels (predictable; a proper fold-down matrix is out of scope here).
// Deleted wholesale in Phase 3 — keep self-contained so removal is one place.
export function planarFloatToInterleavedInt16Stereo_PHASE1_SHIM(channelData: Float32Array[]): Buffer {
    const ch = channelData.length;
    if (ch === 0) return Buffer.alloc(0);
    const L = channelData[0]!;
    const R = ch === 1 ? channelData[0]! : channelData[1]!;
    if (ch > 2) {
        log.warn(`decode: ${ch}-channel audio downmixed to stereo (first two channels)`);
    }
    const frames = Math.min(L.length, R.length);
    const out = Buffer.allocUnsafe(frames * 4);
    for (let i = 0; i < frames; i++) {
        out.writeInt16LE(floatToInt16(L[i]!), i * 4);
        out.writeInt16LE(floatToInt16(R[i]!), i * 4 + 2);
    }
    return out;
}

// Instantiate the codecs triggers most commonly use, concurrently, at startup.
// Two purposes: move WASM compile cost off the first trigger's hot path, and —
// because this runs BEFORE the bridge prints BRIDGE_READY — make the build
// self-test fail loudly if esbuild didn't fold a codec's dynamic import / WASM
// into the bundled artifact (a real packaging gate). `decode.<fmt>()` is the
// per-format factory exposed by audio-decode; calling it forces the lazy
// import + WASM instantiation, then we free the throwaway decoder instance.
export async function warmupDecoders(): Promise<void> {
    const factories = [decode.wav, decode.mp3, decode.oga, decode.flac];
    await Promise.all(factories.map(async (make) => {
        const d = await make();
        try { d.free(); } catch { /* ignore */ }
    }));
}
