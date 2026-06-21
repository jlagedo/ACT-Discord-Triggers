// Shared gating + fixtures for the real-synthesis tests (tts-synth, tts-e2e).
//
// These tests need two heavy externals CI never has: the sherpa-onnx native
// addon and the downloaded voice models. They auto-skip when either is absent,
// so `npm test` stays green everywhere and runs the real path only on a machine
// that has both. Point ACT_DT_MODELS_DIR at a models root to override the
// default local voice store.

import { createRequire } from 'node:module';
import { existsSync } from 'node:fs';
import { join } from 'node:path';

const DEFAULT_MODELS_DIR = 'E:\\ai';

// Reference model directory names (== catalog downloadIds).
export const PIPER_PT_BR = 'vits-piper-pt_BR-faber-medium';
export const PIPER_EN_US = 'vits-piper-en_US-amy-medium';
export const KOKORO = 'kokoro-multi-lang-v1_0';

export function modelsDir(): string | null {
    const d = (process.env['ACT_DT_MODELS_DIR'] ?? DEFAULT_MODELS_DIR).trim();
    return d && existsSync(d) ? d : null;
}

export function modelDir(name: string): string | null {
    const root = modelsDir();
    if (!root) return null;
    const dir = join(root, name);
    return existsSync(dir) ? dir : null;
}

let nativeChecked = false;
let nativeOk = false;
export function nativeAvailable(): boolean {
    if (!nativeChecked) {
        nativeChecked = true;
        try {
            createRequire(__filename)('sherpa-onnx-node');
            nativeOk = true;
        } catch {
            nativeOk = false;
        }
    }
    return nativeOk;
}

// node:test `skip` reason when the real synth path can't run here, else false.
export function synthSkip(): string | false {
    if (!nativeAvailable()) return 'sherpa-onnx-node addon not installed';
    if (!modelsDir()) return 'voice models not found (set ACT_DT_MODELS_DIR)';
    return false;
}

// RMS of a Float32 mono buffer in [0,1]; ~0 for silence.
export function rms(samples: Float32Array): number {
    if (samples.length === 0) return 0;
    let sum = 0;
    for (let i = 0; i < samples.length; i++) sum += samples[i]! * samples[i]!;
    return Math.sqrt(sum / samples.length);
}

// RMS of 16-bit signed LE stereo PCM, normalized to [0,1] (left channel).
export function rmsPcm16Stereo(pcm: Buffer): number {
    const frames = pcm.length >>> 2;
    if (frames === 0) return 0;
    let sum = 0;
    for (let i = 0; i < frames; i++) {
        const s = pcm.readInt16LE(i * 4) / 0x8000;
        sum += s * s;
    }
    return Math.sqrt(sum / frames);
}
