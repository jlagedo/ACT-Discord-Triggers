// Auto-leveling (loudness normalization) for trigger playback. Some sounds come
// in mastered hot and others whisper-quiet; after a random effect the spread is
// worse still (echo/reverb tails drop loudness, distortion pushes it up). This
// brings each clip toward one target loudness so the user isn't reaching for the
// volume knob between triggers.
//
// It's per-clip and offline: the whole 48 kHz interleaved float32 stereo buffer
// is in hand before playback, so there's no streaming compressor to maintain —
// just measure the clip, pick one gain, apply it. The loudness proxy is ITU-R
// BS.1770 K-weighting (LUFS) — see k-weighting.ts — which tracks perceived volume
// across different spectra far better than broadband energy (a bass-heavy clip
// and a speech clip that read equally loud also sound equally loud). The peak is
// measured separately, broadband, and used only as a brick-wall ceiling so a
// boost can never clip.
//
// discord-host applies this in _enqueue, AFTER any random effect, so the effect's
// own level change is what gets corrected.

import { kWeightedLoudnessLufs } from './k-weighting.js';

export const FULL_SCALE = 32768; // |int16| range; one LSB == 1 / FULL_SCALE

// Leave a sliver of headroom under 0 dBFS. A gain that would push the loudest
// sample past this is clamped down to it — the limiter that makes boosting safe.
const PEAK_CEILING = 0.97; // ≈ -0.26 dBFS

// Cap how much we'll amplify. Without this, a near-silent clip (or the quiet tail
// of one) demands enormous gain and we'd just amplify hiss/noise into the mix.
const MAX_BOOST_DB = 12;
const MAX_BOOST_LINEAR = Math.pow(10, MAX_BOOST_DB / 20);

// Buffers quieter than this are treated as silence and left untouched (avoids a
// divide-by-tiny-RMS blowup and pointless work on empty/near-empty clips).
const MIN_RMS = 1 / FULL_SCALE; // one LSB

// Skip the rewrite when the gain is within this of unity — sub-perceptual, not
// worth allocating a new buffer for.
const UNITY_EPSILON = 0.01; // ≈ 0.086 dB

export interface NormalizeResult {
    samples: Float32Array;
    gain: number;     // linear gain actually applied (1 when not applied)
    applied: boolean; // false → samples is the input buffer, untouched
}

// A pre-known loudness for the buffer, so normalizePcm16 can skip its measurement
// pass. Used for neural-TTS clips whose loudness is baked per-voice in the catalog:
// the gain is derived from these instead of scanning every sample, which is what
// lets the synth path stream. Only valid when the buffer wasn't relevelled after
// baking (e.g. a random effect) — the caller is responsible for that gate.
export interface KnownLevel {
    rms: number;  // K-weighted loudness as a linear full-scale-equivalent (its dB value is LUFS)
    peak: number; // broadband sample peak, 0..1 (1.0 == 0 dBFS)
}

export function dbToLinear(db: number): number {
    return Math.pow(10, db / 20);
}

// Linear full-scale fraction (0..1) -> dBFS; 0 maps to -Infinity. The inverse of
// dbToLinear, used by offline tooling to report baked levels in dB.
export function linearToDb(x: number): number {
    return x > 0 ? 20 * Math.log10(x) : -Infinity;
}

// Measure an interleaved float32 stereo buffer's loudness and peak. `rms` is the
// K-weighted (BS.1770 LUFS) loudness expressed as a linear full-scale-equivalent
// — i.e. dbToLinear(L_K), so its dB value is the clip's LUFS and computeGain can
// land it on a LUFS target with no extra conversion. `peak` is the broadband
// sample peak (1.0 == 0 dBFS), the clip-safety ceiling, kept metric-independent.
// The same math `normalize` levels with, exposed so offline tooling bakes voice
// loudness from the exact runtime numbers.
export function measureLevel(samples: Float32Array): { rms: number; peak: number } {
    const sampleCount = samples.length; // float samples across L+R
    if (sampleCount === 0) return { rms: 0, peak: 0 };
    let peak = 0;
    for (let i = 0; i < sampleCount; i++) {
        const a = Math.abs(samples[i]!);
        if (a > peak) peak = a;
    }
    const lufs = kWeightedLoudnessLufs(samples);
    const rms = Number.isFinite(lufs) ? dbToLinear(lufs) : 0;
    return { rms, peak };
}

// The single gain decision, shared by the runtime-measure path and the streaming
// fixed-gain path: land loudness on target, then clamp so it never exceeds the
// peak ceiling (no clipping) nor the max-boost cap (no amplifying near-silence).
// `rms` is the K-weighted loudness as a linear full-scale-equivalent and
// `targetDbfs` the LUFS target (negative), so dbToLinear(target)/rms is exactly
// the gain that lands the clip at the target LUFS; `peak` is the broadband sample
// peak (0..1). Returns 1 (no change) for a silent/degenerate buffer. The streaming
// path folds this gain into the mono->stereo conversion so a clip is leveled
// without a whole-buffer scan.
export function computeGain(rms: number, peak: number, targetDbfs: number): number {
    if (rms < MIN_RMS || peak <= 0) return 1;
    const peakLimit = PEAK_CEILING / peak;
    return Math.min(dbToLinear(targetDbfs) / rms, peakLimit, MAX_BOOST_LINEAR);
}

// Normalize an interleaved float32 stereo buffer toward `targetDbfs` (a negative
// LUFS value, e.g. -17). Measures K-weighted loudness + broadband peak across
// every sample (both channels together — we want a single coherent gain, not
// per-channel balance changes), then derives one gain bounded by the peak ceiling
// and the max-boost cap.
// When `known` is supplied (a baked per-voice level), the measurement pass is
// skipped and the gain is derived from it — same gain math, no buffer scan.
// The result stays float (the peak ceiling already guarantees no sample exceeds
// ~0.97); the single int16 conversion happens later at the mixer's output.
export function normalize(samples: Float32Array, targetDbfs: number, known?: KnownLevel): NormalizeResult {
    const sampleCount = samples.length; // float samples across L+R
    if (sampleCount === 0) return { samples, gain: 1, applied: false };

    const { rms, peak } = known ?? measureLevel(samples);
    if (rms < MIN_RMS || peak <= 0) return { samples, gain: 1, applied: false };

    const gain = computeGain(rms, peak, targetDbfs);
    if (Math.abs(gain - 1) < UNITY_EPSILON) return { samples, gain: 1, applied: false };

    const out = new Float32Array(sampleCount);
    for (let i = 0; i < sampleCount; i++) {
        out[i] = samples[i]! * gain;
    }
    return { samples: out, gain, applied: true };
}
