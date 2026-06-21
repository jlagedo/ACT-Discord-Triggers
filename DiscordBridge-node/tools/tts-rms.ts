// Batch RMS / peak loudness probe across the installed ONNX catalog voices.
//
// Purpose: validate that a neural voice's output loudness is essentially
// text-independent (constant per model), which is the precondition for baking a
// per-voice RMS into the catalog and applying a fixed normalize gain during
// streaming synthesis instead of the current whole-buffer measure (see
// normalize.ts / the streaming plan).
//
// For each catalog voice that is installed under the models root, it synthesizes
// a fixed set of varied callout lines through the EXACT bridge path
// (OnnxTts.synth -> monoFloat32ToStereoInt16 -> resampleStereo16) and measures
// RMS + peak in dBFS with the same math normalizePcm16 uses. The number that
// matters is the per-voice stddev across lines: if it's small (<~1 dB) over
// short and long, quiet- and loud-worded lines, loudness is text-independent and
// a single baked value per voice is safe.
//
//   npm run tts:rms                         # all installed catalog voices, default models root
//   npm run tts:rms -- --models E:\ai       # explicit models root
//   npm run tts:rms -- --filter pt_BR       # only voices whose id contains the substring
//   npm run tts:rms -- --json rms.json      # also write per-voice results as JSON (feeds baking)
//   npm run tts:rms -- --lines 8 --threads 4
//
// Models root resolution: --models, else ACT_DT_MODELS_DIR, else E:\ai.

import { readFileSync, existsSync, writeFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { dirname } from 'node:path';
import { OnnxTts, type OnnxSynthConfig } from '../src/tts.js';
import { monoFloat32ToStereoInt16, resampleStereo16 } from '../src/discord-host.js';
import { measureLevel, linearToDb } from '../src/normalize.js';
import { arg } from './args.js';

// Varied callout lines: short imperatives through long multi-sentence pulls,
// with soft- and hard-consonant words, so a low per-voice stddev across this set
// is real evidence of text-independent loudness rather than same-length samples.
const LINES = [
    'Stack.',
    'Spread now.',
    'Tank buster on you, use a cooldown.',
    'Move out of the fire and bait the tethers to the edge.',
    'Pull complete. The boss is at twelve percent. Stack for the raid wide.',
    'Three, two, one, go. Everybody stack on the marker and pop mitigation.',
    'Look away. Shhh. Soft sibilance, hushed whispers, easy now.',
    'ATTACK! Hard stops, kick, block, strike, crack the shield!',
];

interface Voice {
    id: string;
    family: string;
    sid: number;
    lang: string;
    downloadId: string;
}

interface CatalogFile { voices: Voice[] }

interface LineMeasure { rmsDb: number; peakDb: number; genMs: number; audioMs: number }

interface VoiceResult {
    id: string;
    family: string;
    sid: number;
    n: number;
    rmsMeanDb: number;
    rmsStdDb: number;
    rmsMinDb: number;
    rmsMaxDb: number;
    peakMaxDb: number;
    rtfMean: number;
}

function loadCatalog(): Voice[] {
    const here = dirname(fileURLToPath(import.meta.url));
    const path = join(here, '..', '..', 'ACT_DiscordTriggers.Core', 'Tts', 'onnx-voices.json');
    const file = JSON.parse(readFileSync(path, 'utf8')) as CatalogFile;
    return file.voices;
}

// Mirror OnnxCatalog.IsInstalled: Kokoro needs model.onnx + voices.bin; Piper
// needs any *.onnx in <root>/<downloadId>.
function isInstalled(v: Voice, root: string): boolean {
    const dir = join(root, v.downloadId);
    if (!existsSync(dir)) return false;
    if (v.family === 'kokoro') {
        return existsSync(join(dir, 'model.onnx')) && existsSync(join(dir, 'voices.bin'));
    }
    try { return readdirSync(dir).some(n => n.toLowerCase().endsWith('.onnx')); } catch { return false; }
}

// RMS + peak in dBFS over the final 48k/16-bit/stereo buffer, measured with the
// exact runtime math (normalize.ts measureLevel) so the numbers are directly
// comparable to a normalize target and bakeable as-is.
function measure(pcm: Buffer): { rmsDb: number; peakDb: number } {
    const { rms, peak } = measureLevel(pcm);
    return { rmsDb: linearToDb(rms), peakDb: linearToDb(peak) };
}

function mean(xs: number[]): number { return xs.reduce((a, b) => a + b, 0) / xs.length; }
function stddev(xs: number[]): number {
    const m = mean(xs);
    return Math.sqrt(mean(xs.map(x => (x - m) ** 2)));
}

async function measureVoice(v: Voice, root: string, threads: number, lines: string[]): Promise<VoiceResult | null> {
    const desc: OnnxSynthConfig = {
        family: v.family === 'kokoro' ? 'kokoro' : 'piper',
        modelDir: join(root, v.downloadId),
        sid: v.sid,
        lang: v.lang,
        speedSlider: 10,
        threads,
    };
    const tts = new OnnxTts();
    const cfg = tts.configure(desc);
    if (!cfg.ok) { process.stderr.write(`  ! ${v.id}: ${cfg.error}\n`); return null; }

    // Warm-up (discarded): the first synth pays the cold model load. Running it
    // before the timed loop keeps RTF at steady state, matching the bridge's
    // join-time warm-up so per-line gen times reflect real callout latency.
    await tts.synth('Ready.');

    const per: LineMeasure[] = [];
    for (const text of lines) {
        const t0 = process.hrtime.bigint();
        const audio = await tts.synth(text);
        const genMs = Number(process.hrtime.bigint() - t0) / 1e6;
        if (!audio || audio.samples.length === 0) continue;
        const stereo = monoFloat32ToStereoInt16(audio.samples);
        const final = resampleStereo16(stereo, audio.sampleRate, 48000);
        const m = measure(final);
        per.push({ ...m, genMs, audioMs: (audio.samples.length / audio.sampleRate) * 1000 });
    }
    if (per.length === 0) { process.stderr.write(`  ! ${v.id}: no audio\n`); return null; }

    const rms = per.map(p => p.rmsDb);
    return {
        id: v.id,
        family: v.family,
        sid: v.sid,
        n: per.length,
        rmsMeanDb: mean(rms),
        rmsStdDb: stddev(rms),
        rmsMinDb: Math.min(...rms),
        rmsMaxDb: Math.max(...rms),
        peakMaxDb: Math.max(...per.map(p => p.peakDb)),
        rtfMean: mean(per.map(p => p.genMs / p.audioMs)),
    };
}

function f(n: number, w = 7): string {
    return (Number.isFinite(n) ? n.toFixed(2) : '-inf').padStart(w);
}

async function main(): Promise<void> {
    const root = resolve(arg('models', process.env['ACT_DT_MODELS_DIR'] ?? 'E:\\ai'));
    const filter = arg('filter', '');
    const threads = Number(arg('threads', '4')) || 4;
    const lineCount = Number(arg('lines', String(LINES.length))) || LINES.length;
    const lines = LINES.slice(0, Math.max(1, lineCount));
    const jsonOut = arg('json', '');

    const catalog = loadCatalog()
        .filter(v => (filter ? v.id.includes(filter) : true))
        .filter(v => isInstalled(v, root));

    process.stdout.write(`Models root: ${root}\n`);
    process.stdout.write(`Voices to measure: ${catalog.length} (${lines.length} lines each, ${threads} threads)\n\n`);
    if (catalog.length === 0) {
        process.stderr.write('No installed catalog voices matched. Check --models / --filter.\n');
        process.exitCode = 1;
        return;
    }

    const header = `${'voice'.padEnd(38)} fam  sid    rmsMean  rmsStd  rmsMin  rmsMax  peakMax    RTF`;
    process.stdout.write(header + '\n' + '-'.repeat(header.length) + '\n');

    const results: VoiceResult[] = [];
    for (const v of catalog) {
        const r = await measureVoice(v, root, threads, lines);
        if (!r) continue;
        results.push(r);
        process.stdout.write(
            `${r.id.padEnd(38)} ${r.family.slice(0, 3).padEnd(3)} ${String(r.sid).padStart(3)}  ` +
            `${f(r.rmsMeanDb)} ${f(r.rmsStdDb, 6)} ${f(r.rmsMinDb)} ${f(r.rmsMaxDb)} ${f(r.peakMaxDb)} ${f(r.rtfMean, 6)}\n`,
        );
    }

    if (results.length > 0) {
        const means = results.map(r => r.rmsMeanDb);
        const worstStd = Math.max(...results.map(r => r.rmsStdDb));
        process.stdout.write('\n=== Summary ===\n');
        process.stdout.write(`Voices measured:           ${results.length}\n`);
        process.stdout.write(`Per-voice RMS stddev:      max ${f(worstStd, 5).trim()} dB  (small => loudness is text-independent => bakeable)\n`);
        process.stdout.write(`Across-voice RMS mean:     ${f(mean(means), 5).trim()} dB, spread ${f(Math.min(...means), 5).trim()}..${f(Math.max(...means), 5).trim()} dB (std ${f(stddev(means), 5).trim()})\n`);
        process.stdout.write(`Loudest peak seen:         ${f(Math.max(...results.map(r => r.peakMaxDb)), 5).trim()} dBFS (headroom check for a fixed gain)\n`);
    }

    if (jsonOut) {
        const out = resolve(jsonOut);
        writeFileSync(out, JSON.stringify({ root, lines, results }, null, 2) + '\n', 'utf8');
        process.stdout.write(`\nWrote ${out}\n`);
    }
}

main().catch((err: unknown) => {
    process.stderr.write(`tts-rms failed: ${err instanceof Error ? err.stack ?? err.message : String(err)}\n`);
    process.exit(1);
});
