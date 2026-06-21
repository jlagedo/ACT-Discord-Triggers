// The bridge's internal audio currency: interleaved 32-bit float stereo
// (L,R,L,R,…), nominal range [-1, 1] but deliberately allowed to exceed it
// BETWEEN DSP stages so headroom is preserved (an echo/reverb tail or a summed
// mix can momentarily run hot and be pulled back down later without the
// information having been clipped away mid-chain).
//
// Every interior stage — decode, resample, FX, normalize, declick, mix — works
// in this format. int16 survives only at the two edges:
//   • ingest: the SpeakPcm wire frame arrives s16le and is widened here once.
//   • exit:   the mixer converts the summed float bus to s16le exactly once,
//             right before prism's Opus encoder (StreamType.Raw wants s16le).
//
// This is the single place the int16<->float scaling convention lives. Keeping
// one converter (symmetric *32768 with a hard clamp, matching the decode path)
// removes the per-stage requantization the old all-int16 pipeline incurred.
//
// The exit converter (floatToInt16) is also the natural future home for TPDF
// dither and a master limiter; today it is a straight round + clamp so the
// format migration is behaviour-preserving and verifiable on its own.

// 1.0 == 0 dBFS. int16 spans [-32768, 32767]; we scale by 32768 and clamp the
// positive overflow (1.0 -> 32768 -> 32767), which is the convention the decoder
// and effects already used.
const SCALE = 32768;

// Widen interleaved s16le PCM to interleaved float32 in [-1, 1). A trailing odd
// byte (malformed upstream) is dropped so the read never walks past the end —
// matches the odd-byte tolerance of the mixer/declick paths.
export function int16ToFloat32(pcm: Buffer): Float32Array {
    const n = pcm.length >>> 1; // whole int16 samples (both channels)
    const out = new Float32Array(n);
    for (let i = 0; i < n; i++) {
        out[i] = pcm.readInt16LE(i * 2) / SCALE;
    }
    return out;
}

// Narrow interleaved float to interleaved s16le PCM: round to nearest and hard
// clamp to the int16 range. THIS is the pipeline's single quantization point.
// Accepts a Float64Array too, so the mixer can pass its float64 sum bus straight
// through. When `out` is supplied it must hold at least samples.length*2 bytes
// and is written in place; otherwise a fresh Buffer is allocated.
export function floatToInt16(samples: Float32Array | Float64Array, out?: Buffer): Buffer {
    const n = samples.length;
    const buf = out ?? Buffer.allocUnsafe(n * 2);
    for (let i = 0; i < n; i++) {
        let s = Math.round(samples[i]! * SCALE);
        if (s > 32767) s = 32767;
        else if (s < -32768) s = -32768;
        buf.writeInt16LE(s, i * 2);
    }
    return buf;
}
