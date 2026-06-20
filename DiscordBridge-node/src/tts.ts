// ONNX neural TTS synthesis (Piper / Kokoro via sherpa-onnx-node).
//
// This module is synthesis-only: it turns text into the model's raw Float32 mono
// samples + sample rate. It knows nothing about Discord's 48 kHz/16-bit/stereo PCM
// format — discord-host.ts owns that conversion (the pinned `resampleStereo16`),
// so the audio-format constraint stays in one place and there is no import cycle.
//
// The native addon is loaded lazily (createRequire) the first time a model is
// built, so the bridge still starts and prints BRIDGE_READY even if the
// sherpa-onnx runtime isn't staged — only ONNX synthesis fails, SAPI is unaffected.
//
// The voice descriptor (family/sid/lang/modelDir/speed/threads) is resolved C#-side
// from the catalog and pushed in SetConfig's `ttsParams`. The crash-critical espeak
// `lang` arrives pre-vetted (an unknown one hard-exits the whole process), so this
// module never computes it.

import { createRequire } from 'node:module';
import { existsSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import * as log from './file-log.js';
import type { OpResult } from './pipe-server.js';

// The raw Float32 mono samples + rate a synth produces. Also the shape
// sherpa-onnx-node's generateAsync resolves with (it ships no declarations).
export interface OnnxAudio { samples: Float32Array; sampleRate: number }

// Minimal typed surface over sherpa-onnx-node. Only the members this module
// touches are modelled; the rest stays opaque.
interface GenerationConfigOpts { sid: number; speed: number; extra?: { lang: string } }
interface OfflineTtsInstance {
    readonly sampleRate: number;
    readonly numSpeakers: number;
    generateAsync(opts: {
        text: string;
        generationConfig: object;
        onProgress?: (info: { samples?: Float32Array; progress?: number }) => number;
    }): Promise<OnnxAudio | undefined>;
}
interface SherpaModule {
    OfflineTts: new (config: object) => OfflineTtsInstance;
    GenerationConfig: new (opts: GenerationConfigOpts) => object;
}

// Base createRequire on __filename, not import.meta.url: esbuild emits CJS where
// import.meta is `{}` (so createRequire(import.meta.url) would throw at startup),
// while __filename is a real CJS global in the bundle. The native addon is in the
// esbuild `external` list and resolves from dist/node_modules at runtime.
let sherpaMod: SherpaModule | null = null;
function loadSherpa(): SherpaModule {
    if (!sherpaMod) {
        const requireNative = createRequire(__filename);
        sherpaMod = requireNative('sherpa-onnx-node') as SherpaModule;
    }
    return sherpaMod;
}

export interface OnnxSynthConfig {
    family: 'piper' | 'kokoro';
    modelDir: string;   // absolute <modelsDir>/<downloadId>
    sid: number;
    lang: string;       // espeak-ng voice id; '' = the model's own / lexicon
    speedSlider: number; // raw UI slider 0..20
    threads: number;
}

function toInt(v: string | undefined, fallback: number): number {
    const n = Number(v);
    return Number.isFinite(n) ? Math.trunc(n) : fallback;
}

// Parse the SetConfig `ttsParams` bag into a synth descriptor, or null when no
// installed ONNX voice is selected (engine != onnx, or no model dir) — in which
// case the bridge loads nothing and SpeakText is skipped.
export function parseTtsParams(raw: Record<string, string> | undefined): OnnxSynthConfig | null {
    if (!raw || raw['engine'] !== 'onnx') return null;
    const modelDir = raw['modelDir'] ?? '';
    if (!modelDir) return null;
    return {
        family: raw['family'] === 'kokoro' ? 'kokoro' : 'piper',
        modelDir,
        sid: toInt(raw['sid'], 0),
        lang: raw['lang'] ?? '',
        speedSlider: toInt(raw['speed'], 10),
        threads: toInt(raw['threads'], 1),
    };
}

// UI speed slider (0..20, 10 = normal) -> sherpa generationConfig.speed (0.5..1.5x).
// Exported for unit tests.
export function sliderToSpeed(slider: number): number {
    const s = 0.5 + slider / 20;
    return s > 0 ? s : 1.0;
}

export class OnnxTts {
    private desc: OnnxSynthConfig | null = null;
    private ready = false;
    // The currently-loaded model, keyed by modelDir+threads. Only one is kept
    // (a neural model is 200-650 MB), so switching Piper voices reloads while
    // switching Kokoro speakers — same pack/dir — does not.
    private model: { key: string; tts: OfflineTtsInstance } | null = null;

    // Apply a new descriptor (or null to unload). Validates the model files up
    // front and returns the failure so the host can surface it to the user; a
    // SpeakText with no ready voice is skipped, never crashing the bridge.
    configure(desc: OnnxSynthConfig | null): OpResult {
        this.desc = desc;
        if (!desc) { this.ready = false; return { ok: true, error: '' }; }
        const err = this._validate(desc);
        this.ready = err === '';
        if (!this.ready) { this.model = null; return { ok: false, error: err }; }
        return { ok: true, error: '' };
    }

    isReady(): boolean { return this.ready && this.desc !== null; }

    describe(): string {
        if (!this.desc) return 'none';
        return `${this.desc.family} sid=${this.desc.sid} lang='${this.desc.lang}'`;
    }

    // Synthesize text to the model's raw Float32 mono samples + rate, or null when
    // no voice is ready or synthesis fails / yields nothing. Off the event loop:
    // generateAsync dispatches to a libuv worker, so the mixer's 20 ms frame
    // delivery and Discord keepalives keep running during synthesis.
    async synth(text: string): Promise<OnnxAudio | null> {
        if (!this.ready || !this.desc) return null;
        if (!text || text.trim().length === 0) return null;
        try {
            const tts = this._ensureModel(this.desc);
            const sherpa = loadSherpa();
            const gcOpts: GenerationConfigOpts = {
                sid: this.desc.sid,
                speed: sliderToSpeed(this.desc.speedSlider),
            };
            if (this.desc.lang) gcOpts.extra = { lang: this.desc.lang };
            const gc = new sherpa.GenerationConfig(gcOpts);

            const chunks: Float32Array[] = [];
            const result = await tts.generateAsync({
                text,
                generationConfig: gc,
                onProgress: (info) => {
                    const s = info?.samples;
                    if (s?.length) chunks.push(Float32Array.from(s));
                    return 1;
                },
            });

            // The resolved value is the full audio for non-streaming TTS; the
            // accumulated onProgress chunks are the fallback if it isn't populated.
            let samples: Float32Array | null = null;
            if (result?.samples?.length) samples = Float32Array.from(result.samples);
            else if (chunks.length) samples = concatFloat32(chunks);
            if (!samples || samples.length === 0) return null;

            const sampleRate = result?.sampleRate ?? tts.sampleRate;
            if (!Number.isFinite(sampleRate) || sampleRate <= 0) {
                log.error(`ONNX synth: invalid sample rate ${sampleRate}`);
                return null;
            }
            return { samples, sampleRate };
        } catch (e) {
            log.error('ONNX synth failed', e);
            return null;
        }
    }

    private _ensureModel(desc: OnnxSynthConfig): OfflineTtsInstance {
        const key = `${desc.modelDir}#${desc.threads}`;
        if (this.model?.key === key) return this.model.tts;
        const sherpa = loadSherpa();
        const tts = new sherpa.OfflineTts({ model: buildModelConfig(desc), maxNumSentences: 1 });
        this.model = { key, tts };
        log.info(`ONNX model loaded: ${key} sampleRate=${tts.sampleRate} speakers=${tts.numSpeakers}`);
        return tts;
    }

    // Returns '' when the model dir holds everything sherpa needs, else a short
    // user-facing reason. Mirrors OnnxCatalog.IsInstalled on the C# side.
    private _validate(desc: OnnxSynthConfig): string {
        if (!existsSync(desc.modelDir)) return `model folder not found: ${desc.modelDir}`;
        if (!existsSync(join(desc.modelDir, 'tokens.txt'))) return `tokens.txt missing in ${desc.modelDir}`;
        if (desc.family === 'kokoro') {
            if (!existsSync(join(desc.modelDir, 'model.onnx'))) return `model.onnx missing in ${desc.modelDir}`;
            if (!existsSync(join(desc.modelDir, 'voices.bin'))) return `voices.bin missing in ${desc.modelDir}`;
        } else if (!findOnnx(desc.modelDir)) {
            return `no .onnx model in ${desc.modelDir}`;
        }
        return '';
    }
}

function findOnnx(dir: string): string | null {
    try {
        const f = readdirSync(dir).find(n => n.toLowerCase().endsWith('.onnx'));
        return f ? join(dir, f) : null;
    } catch { return null; }
}

// Build the sherpa OfflineTts `model` config. numThreads/provider/debug are
// siblings of the family block (per sherpa-onnx-node's OfflineTts API).
function buildModelConfig(desc: OnnxSynthConfig): Record<string, unknown> {
    const dataDir = join(desc.modelDir, 'espeak-ng-data');
    const tokens = join(desc.modelDir, 'tokens.txt');
    let model: Record<string, unknown>;
    if (desc.family === 'kokoro') {
        // Kokoro ships per-locale lexicons; include the ones present.
        const lexicon = ['lexicon-us-en.txt', 'lexicon-gb-en.txt']
            .map(n => join(desc.modelDir, n))
            .filter(existsSync)
            .join(',');
        model = {
            kokoro: {
                model: join(desc.modelDir, 'model.onnx'),
                voices: join(desc.modelDir, 'voices.bin'),
                tokens, dataDir, lexicon,
            },
        };
    } else {
        model = {
            vits: {
                model: findOnnx(desc.modelDir) ?? join(desc.modelDir, 'model.onnx'),
                tokens, dataDir,
            },
        };
    }
    model['numThreads'] = desc.threads > 0 ? desc.threads : 1;
    model['provider'] = 'cpu';
    model['debug'] = false;
    return model;
}

function concatFloat32(chunks: Float32Array[]): Float32Array {
    let total = 0;
    for (const c of chunks) total += c.length;
    const out = new Float32Array(total);
    let off = 0;
    for (const c of chunks) { out.set(c, off); off += c.length; }
    return out;
}
