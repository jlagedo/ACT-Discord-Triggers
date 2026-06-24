// Ingest conditioning for decoded sound files. User-supplied trigger audio is
// uncontrolled: it can carry a DC offset, subsonic rumble, leading/trailing
// silence, or — as seen in the wild — a corrupt full-scale junk burst in its
// final samples. The per-clip declick in discord-host._enqueue runs LAST (after
// any random effect) and only fades the assembled buffer's edges; a length-
// extending effect (echo/reverb/down-pitch) appends a tail that relocates a hot
// source edge into the buffer interior, where declick no longer reaches it — and
// it plays back as an isolated "gunshot" pop. The fix is the standard one: treat
// the source like untrusted input and sanitize it at ingest, before any creative
// effect, so its edges are already zero whatever an effect does to the length.
//
// This runs once per decoded file (the result is WavCached), on the bridge's
// 48 kHz interleaved float32 stereo buffer. It is applied ONLY on the file path
// (decodeFileToFinalPcm); the SpeakPcm/TTS path is controlled and skips it.
//
// Pure: returns a NEW buffer and never mutates the input (callers share cached
// buffers, like declick.ts).

import { declick } from './declick.js';

const FRAME_SAMPLES = 2; // interleaved float32 stereo: L,R

// One-pole DC-blocker pole. cutoff ≈ (1 - R) * SR / (2π); R = 0.9975 ≈ 19 Hz at
// 48 kHz — below the audible band, so it only strips DC offset / subsonic rumble.
export const DC_BLOCK_POLE = 0.9975;

// Frames quieter than this on BOTH channels are silence for trimming. 0.001 is
// ≈ −60 dBFS — conservative, so real low-level content is never clipped off.
export const SILENCE_THRESHOLD = 0.001;

// Condition a decoded 48 kHz interleaved float32 stereo buffer:
//   sanitize (NaN/Inf → 0) → DC-block (per channel) → trim silence → edge-fade.
// Returns a new buffer; the input is left untouched.
export function conditionSource(samples: Float32Array): Float32Array {
    const aligned = samples.length & ~(FRAME_SAMPLES - 1);
    if (aligned === 0) return new Float32Array(0);

    // 1+2+3. Sanitize, DC-block, and locate the non-silence span in one
    // per-channel pass into a fresh array. y[n] = x[n] - x[n-1] + R*y[n-1],
    // independent L/R state; non-finite inputs become 0 before they can poison
    // the filter state. The silence bounds (per-frame peak below threshold) are
    // tracked on the filtered signal as we write it, so no second scan is needed.
    const filtered = new Float32Array(aligned);
    let xPrevL = 0, yPrevL = 0, xPrevR = 0, yPrevR = 0;
    let firstFrame = -1;
    let lastFrame = -1;
    for (let i = 0, f = 0; i < aligned; i += FRAME_SAMPLES, f++) {
        let xL = samples[i]!;
        let xR = samples[i + 1]!;
        if (!Number.isFinite(xL)) xL = 0;
        if (!Number.isFinite(xR)) xR = 0;
        const yL = xL - xPrevL + DC_BLOCK_POLE * yPrevL;
        const yR = xR - xPrevR + DC_BLOCK_POLE * yPrevR;
        filtered[i] = yL;
        filtered[i + 1] = yR;
        xPrevL = xL; yPrevL = yL;
        xPrevR = xR; yPrevR = yR;
        if (Math.max(Math.abs(yL), Math.abs(yR)) >= SILENCE_THRESHOLD) {
            if (firstFrame === -1) firstFrame = f;
            lastFrame = f;
        }
    }

    // Trim leading/trailing silence. If the whole clip is silent there is
    // nothing to frame — hand back the filtered buffer unchanged (decode already
    // rejects truly empty input upstream).
    const trimmed = firstFrame === -1
        ? filtered
        : filtered.slice(firstFrame * FRAME_SAMPLES, (lastFrame + 1) * FRAME_SAMPLES);

    // 4. Edge-fade so both source edges reach zero — a length-extending effect
    // can then never bury a hot edge mid-buffer. Reuses the pipeline's declick.
    return declick(trimmed);
}
