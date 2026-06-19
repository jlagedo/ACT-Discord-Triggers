// Declick ramps for trigger playback. A clip that starts or ends on a non-zero
// sample steps against the mixer's digital silence — silence -> +12000 at the
// onset, or +13000 -> silence at the tail — and that one-sample discontinuity
// is an audible click once Opus encodes it. The PcmMixer feeds one long-lived
// AudioResource and switches to silence inline (it never push(null)s), so
// @discordjs/voice's end-of-resource silence padding never runs to soften the
// transition; the clip itself has to ramp.
//
// So fade each clip in from / out to zero over a few ms. Like normalize.ts this
// is per-clip and offline: the whole 48 kHz / 16-bit / stereo buffer is in hand,
// so we just scale the edge frames. The fade-in is deliberately shorter than the
// fade-out — 2 ms is enough to kill an onset click (a click is a single-sample
// jump) while preserving the attack/punch of percussive trigger SFX, whereas a
// tail is decaying anyway and tolerates a longer 5 ms ramp.
//
// discord-host applies this in _enqueue, as the LAST per-clip transform (after
// any random effect and normalize), so the final samples are guaranteed to reach
// zero regardless of what those stages did to the level.

const FRAME_BYTES = 4; // interleaved s16le stereo: L,R = 2 + 2 bytes

export const FADE_IN_FRAMES = 96;   // 2 ms at 48 kHz (short: preserves attack)
export const FADE_OUT_FRAMES = 240; // 5 ms at 48 kHz

// Linear fade-in on the first frames and fade-out on the last frames of an
// interleaved s16le stereo buffer. Returns a NEW buffer; never mutates the input
// (callers pass WavCache-shared buffers, so in-place editing would corrupt the
// cache). Only the ramp regions are rewritten — the middle is copied verbatim.
export function declick(pcm: Buffer): Buffer {
    // Align to whole stereo frames; a stray trailing byte (malformed upstream)
    // would otherwise let the ramp loop read one byte past the end. Matches the
    // mixer's odd-byte tolerance.
    const aligned = pcm.length & ~(FRAME_BYTES - 1);
    const out = Buffer.from(pcm.subarray(0, aligned));
    const totalFrames = aligned / FRAME_BYTES;
    if (totalFrames === 0) return out;

    // Clamp each ramp to the clip length so a sub-fade-length blip still ramps
    // (over its whole self) instead of indexing past the buffer.
    const fadeInLen = Math.min(FADE_IN_FRAMES, totalFrames);
    const fadeOutLen = Math.min(FADE_OUT_FRAMES, totalFrames);
    const fadeOutStart = totalFrames - fadeOutLen;

    for (let f = 0; f < totalFrames; f++) {
        // Untouched middle: gain is exactly 1, leave the copied bytes as-is.
        if (f >= fadeInLen && f < fadeOutStart) continue;
        const inGain = f < fadeInLen ? (f + 1) / fadeInLen : 1;
        const outGain = f >= fadeOutStart ? (totalFrames - f) / fadeOutLen : 1;
        const gain = inGain * outGain; // product covers clips short enough to overlap
        const off = f * FRAME_BYTES;
        out.writeInt16LE(Math.round(out.readInt16LE(off) * gain), off);
        out.writeInt16LE(Math.round(out.readInt16LE(off + 2) * gain), off + 2);
    }
    return out;
}
