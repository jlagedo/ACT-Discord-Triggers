// Render every trigger sound through the REAL production playback pipeline so the
// output can be auditioned. Mirrors discord-host exactly for a file trigger:
//   decodeFileToFinalPcm -> conditionSource (ingest: sanitize/DC-block/trim/fade)
//   -> [random effect] -> normalize(-17 LUFS) -> declick -> PcmMixer + limiter
// One WAV per (file, effect) plus a dry (no-effect) baseline, mirrored into an
// output tree so each source's variants sit together for A/B listening.
//
//   node --import tsx tools/render-samples.ts [--in <dir>] [--out <dir>] [--max-seconds N]

import { mkdirSync, readdirSync } from 'node:fs';
import { join, relative, extname, dirname } from 'node:path';

import { decodeFileToFinalPcm } from '../src/discord-host.js';
import { conditionSource } from '../src/source-conditioning.js';
import { PcmMixer } from '../src/pcm-mixer.js';
import { normalize, dbToLinear } from '../src/normalize.js';
import { initResampler } from '../src/resample.js';
import { declick } from '../src/declick.js';
import { applyEffect, EFFECT_NAMES, mulberry32 } from '../src/effects.js';
import { writeWav16 } from '../src/wav-write.js';
import { arg } from './args.js';

const SR = 48000;
const CHUNK_MS = 20;
const TARGET_LUFS = -17;
const CEILING = dbToLinear(-1); // default tier, -1 dBTP

const rootDir = arg('in', join(process.env.HOME ?? '', 'Library/CloudStorage/ProtonDrive-eigenrift@proton.me-folder/sound'));
const outDir = arg('out', './.samples-out');
const maxSeconds = Number(arg('max-seconds', '60'));
mkdirSync(outDir, { recursive: true });

const AUDIO_RE = /\.(wav|mp3|ogg|flac|m4a)$/i;

function walk(dir: string): string[] {
    const out: string[] = [];
    for (const ent of readdirSync(dir, { withFileTypes: true })) {
        if (ent.name.startsWith('.')) continue;
        const p = join(dir, ent.name);
        if (ent.isDirectory()) out.push(...walk(p));
        else if (ent.isFile() && AUDIO_RE.test(ent.name)) out.push(p);
    }
    return out;
}

// Render one processed clip (single voice) through the real mixer with the
// master limiter armed — exactly what feeds Discord's Opus encoder.
function renderMaster(pcm: Float32Array): Buffer {
    const mixer = new PcmMixer();
    mixer.configureLimiter(true, CEILING);
    const totalChunks = Math.ceil((pcm.length / 2 / SR) * 1000 / CHUNK_MS) + 16; // tail
    const out: Buffer[] = [];
    let added = false;
    for (let k = 0; k < totalChunks; k++) {
        if (!added) { mixer.addVoice(pcm); added = true; }
        out.push(Buffer.from(mixer._mixOneChunk()));
    }
    return Buffer.concat(out);
}

// Per-clip chain after conditioning: optional effect -> normalize -> declick.
function processClip(conditioned: Float32Array, effect: typeof EFFECT_NAMES[number] | 'dry', seed: number): Float32Array {
    const base = effect === 'dry'
        ? conditioned
        : applyEffect(effect, conditioned, mulberry32(0x51ed ^ (seed + effect.length)));
    const norm = normalize(base, TARGET_LUFS);
    return declick(norm.applied ? norm.samples : base);
}

async function main(): Promise<void> {
    await initResampler();
    const files = walk(rootDir).sort();
    console.log(`Rendering ${files.length} source files through the conditioned pipeline.`);
    console.log(`Pipeline: decode -> conditionSource -> [effect] -> normalize(${TARGET_LUFS} LUFS) -> declick -> mixer+limiter(-1 dBTP)`);
    console.log(`Output: ${outDir}  (one folder per source; dry.wav + <effect>.wav)\n`);

    let wavCount = 0;
    for (const f of files) {
        const rel = relative(rootDir, f);
        process.stdout.write(`• ${rel} ... `);
        let decoded: Float32Array;
        try {
            decoded = await decodeFileToFinalPcm(f);
        } catch (e) {
            console.log(`decode error: ${(e as Error).message}`);
            continue;
        }
        const durSec = decoded.length / 2 / SR;
        if (durSec > maxSeconds) { console.log(`skip (${durSec.toFixed(1)}s > ${maxSeconds}s)`); continue; }

        const conditioned = conditionSource(decoded);
        const stemDir = join(outDir, dirname(rel), rel.slice(rel.lastIndexOf('/') + 1).replace(extname(rel), ''));
        mkdirSync(stemDir, { recursive: true });

        const variants: Array<typeof EFFECT_NAMES[number] | 'dry'> = ['dry', ...EFFECT_NAMES];
        for (let i = 0; i < variants.length; i++) {
            const v = variants[i]!;
            const master = renderMaster(processClip(conditioned, v, i));
            writeWav16(join(stemDir, `${v}.wav`), master, { sampleRate: SR, channels: 2 });
            wavCount++;
        }
        console.log(`${variants.length} WAVs (${durSec.toFixed(1)}s, conditioned ${(conditioned.length / 2 / SR).toFixed(1)}s)`);
    }

    console.log(`\nDone. ${wavCount} WAVs in ${outDir}. Open it and compare dry.wav vs each effect per folder.`);
}

main().catch((e) => { console.error(e); process.exit(1); });
