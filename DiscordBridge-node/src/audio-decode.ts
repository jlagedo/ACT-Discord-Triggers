// Helpers around the `audio-decode` package. The bridge calls `decode()` (from
// 'audio-decode') directly — it auto-detects the container (wav/mp3/ogg/flac/
// opus/…) via `audio-type` and decodes to planar Float32 PCM, with each codec's
// WASM inlined (no native bindings, nothing to stage). This module adds the
// planar->interleaved downmix into the bridge's internal currency (interleaved
// float32 stereo) and a startup warm-up. No int16 conversion happens here — the
// whole interior pipeline is float; int16 lives only at the mixer's output.
import decode from 'audio-decode';

import * as log from './file-log.js';

// Planar Float32 channels -> interleaved float32 stereo (L,R,L,R,…) at the SOURCE
// sample rate (the caller resamples to 48k). Downmix: mono duplicates L=R; stereo
// passes through; >2ch keeps the first two channels (predictable; a proper
// fold-down matrix is out of scope here).
export function planarFloatToInterleavedStereoF32(channelData: Float32Array[]): Float32Array {
    const ch = channelData.length;
    if (ch === 0) return new Float32Array(0);
    const L = channelData[0]!;
    const R = ch === 1 ? channelData[0]! : channelData[1]!;
    if (ch > 2) {
        log.warn(`decode: ${ch}-channel audio downmixed to stereo (first two channels)`);
    }
    const frames = Math.min(L.length, R.length);
    const out = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        out[i * 2] = L[i]!;
        out[i * 2 + 1] = R[i]!;
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
