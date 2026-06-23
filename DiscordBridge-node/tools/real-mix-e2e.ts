// Full-pipeline mixing test on REAL trigger audio. Decodes the user's actual ACT
// Discord-trigger sound files through the production decode+resample path
// (decodeFileToFinalPcm), runs the real per-clip chain (auto-level normalize ->
// declick), then schedules them on a timeline through the REAL PcmMixer with the
// master limiter armed — the exact code that feeds Discord's Opus encoder.
//
// Scenarios: solo (per-clip auto-level convergence), dual-fire (two triggers in the
// same 20 ms chunk), staggered interleave (long clips overlapping mid-stream), and
// a pile-up (every clip inside a short window). Each timeline is rendered limiter
// OFF vs ON so the limiter's effect on real material is measurable, and the master
// bus is written to WAV for listening.
//
//   node --import tsx tools/real-mix-e2e.ts [--in <triggersDir>] [--out <dir>]

import { mkdirSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

import { decodeFileToFinalPcm } from '../src/discord-host.js';
import { PcmMixer } from '../src/pcm-mixer.js';
import { normalize, measureLevel, linearToDb, dbToLinear } from '../src/normalize.js';
import { initResampler } from '../src/resample.js';
import { declick } from '../src/declick.js';
import { kWeightedLoudnessLufs } from '../src/k-weighting.js';
import { int16ToFloat32 } from '../src/audio-format.js';
import { writeWav16 } from '../src/wav-write.js';
import { arg } from './args.js';

const SR = 48000;
const CHUNK_BYTES = 3840; // 960 frames * 2ch * 2 bytes
const CHUNK_MS = 20;
const TARGET_LUFS = -17;
const CEILING = dbToLinear(-1); // default tier, -1 dBTP

const inDir = arg('in', './.e2e-out/triggers');
const outDir = arg('out', './.e2e-out');
mkdirSync(outDir, { recursive: true });

interface Clip {
    name: string;
    pcm: Float32Array;   // processed (normalized + declicked), ready for the mixer
    durMs: number;
    inLufs: number;
    outLufs: number;
    gainDb: number;
}

// One scheduled voice: a processed clip starting at startMs on the master timeline.
interface Event { clip: Clip; startMs: number; }

interface MasterStats {
    peak: number;       // max |sample| as int16
    peakDbfs: number;
    clipped: number;    // samples pinned at the int16 rails
    nonFinite: number;
    lufs: number;       // integrated K-weighted loudness of the master
    seconds: number;
}

// Decode + process one file exactly as _enqueue does (minus random fx, which is a
// per-clip dice roll and off by default): normalize toward target, then declick.
async function loadClip(path: string, name: string): Promise<Clip> {
    const decoded = await decodeFileToFinalPcm(path);
    const before = measureLevel(decoded);
    const norm = normalize(decoded, TARGET_LUFS);
    const buf = declick(norm.applied ? norm.samples : decoded);
    return {
        name,
        pcm: buf,
        durMs: (buf.length / 2 / SR) * 1000,
        inLufs: linearToDb(before.rms),
        outLufs: kWeightedLoudnessLufs(buf),
        gainDb: norm.gain > 0 ? 20 * Math.log10(norm.gain) : 0,
    };
}

// Render a timeline through the real mixer. Voices are added at their start chunk;
// the mixer is drained one 20 ms chunk at a time until every voice is consumed
// (plus a short tail for the limiter release + declick fade). Returns the master
// s16le buffer and its stats.
function render(events: Event[], limiterOn: boolean): { pcm: Buffer; stats: MasterStats } {
    const mixer = new PcmMixer();
    mixer.configureLimiter(limiterOn, CEILING);

    const sched = events
        .map((e) => ({ ...e, startChunk: Math.round(e.startMs / CHUNK_MS), added: false }))
        .sort((a, b) => a.startChunk - b.startChunk);

    const lastEndMs = Math.max(...events.map((e) => e.startMs + e.clip.durMs));
    const totalChunks = Math.ceil(lastEndMs / CHUNK_MS) + 16; // tail for release/declick

    const out: Buffer[] = [];
    let peak = 0, clipped = 0, nonFinite = 0;
    for (let k = 0; k < totalChunks; k++) {
        for (const s of sched) {
            if (!s.added && s.startChunk === k) { mixer.addVoice(s.clip.pcm); s.added = true; }
        }
        const chunk = mixer._mixOneChunk();
        out.push(Buffer.from(chunk));
        for (let i = 0; i < chunk.length; i += 2) {
            const v = chunk.readInt16LE(i);
            if (!Number.isFinite(v)) nonFinite++;
            const a = Math.abs(v);
            if (a > peak) peak = a;
            if (a >= 32767) clipped++;
        }
    }
    const pcm = Buffer.concat(out);
    const lufs = kWeightedLoudnessLufs(int16ToFloat32(pcm));
    return {
        pcm,
        stats: {
            peak,
            peakDbfs: peak > 0 ? 20 * Math.log10(peak / 32768) : -Infinity,
            clipped,
            nonFinite,
            lufs,
            seconds: pcm.length / CHUNK_BYTES * (CHUNK_MS / 1000),
        },
    };
}

function fmtStats(s: MasterStats): string {
    return `peak=${s.peak} (${s.peakDbfs.toFixed(2)} dBFS)  clipped=${s.clipped}  ` +
        `nonFinite=${s.nonFinite}  master=${s.lufs.toFixed(1)} LUFS  len=${s.seconds.toFixed(1)}s`;
}

// Render a scenario both ways, write the limiter-ON master to WAV, print the comparison.
function scenario(title: string, file: string, events: Event[]): void {
    console.log(`\n■ ${title}`);
    for (const e of events) {
        console.log(`    @${e.startMs.toString().padStart(5)}ms  ${e.clip.name.padEnd(20)} (${(e.clip.durMs / 1000).toFixed(2)}s, ${e.clip.outLufs.toFixed(1)} LUFS)`);
    }
    const off = render(events, false);
    const on = render(events, true);
    console.log(`    limiter OFF : ${fmtStats(off.stats)}`);
    console.log(`    limiter ON  : ${fmtStats(on.stats)}`);
    writeWav16(join(outDir, file), on.pcm, { sampleRate: SR, channels: 2 });
    const wouldClip = off.stats.clipped > 0;
    const onClean = on.stats.clipped === 0 && on.stats.nonFinite === 0;
    console.log(`    -> ${wouldClip ? `OFF clips ${off.stats.clipped} samples; ` : 'OFF stays clean (no pile-up overflow); '}` +
        `${onClean ? `ON clean, held to ${on.stats.peakDbfs.toFixed(2)} dBFS` : '✗ ON STILL CLIPPED'}  [wrote ${file}]`);
}

async function main(): Promise<void> {
    await initResampler();
    const files = readdirSync(inDir).filter((f) => /\.(wav|mp3|ogg|flac)$/i.test(f)).sort();
    if (files.length === 0) { console.error(`No audio in ${inDir}`); process.exit(1); }

    console.log(`Real-trigger mixing test — ${files.length} clips from ${inDir}`);
    console.log(`Pipeline: decodeFileToFinalPcm -> normalize(${TARGET_LUFS} LUFS) -> declick -> PcmMixer + limiter(-1 dBTP)\n`);

    const clips: Record<string, Clip> = {};
    console.log('Per-clip auto-level (solo) — does each real trigger land on target?');
    console.log('  clip                   dur     inLUFS   gain(dB)  outLUFS   err');
    console.log('  ' + '-'.repeat(68));
    for (const f of files) {
        const c = await loadClip(join(inDir, f), f.replace(/\.[^.]+$/, ''));
        clips[c.name] = c;
        const err = c.outLufs - TARGET_LUFS;
        const flag = Math.abs(err) > 1.5 ? '  <== short of target (peak/boost-bound)' : '';
        console.log(
            '  ' + c.name.padEnd(22) +
            (c.durMs / 1000).toFixed(2).padStart(6) + 's' +
            c.inLufs.toFixed(1).padStart(8) +
            c.gainDb.toFixed(1).padStart(10) +
            c.outLufs.toFixed(1).padStart(9) +
            err.toFixed(1).padStart(6) + flag,
        );
    }
    const outs = Object.values(clips).map((c) => c.outLufs).filter(Number.isFinite);
    console.log(`\n  Loudness spread after auto-level: ${Math.min(...outs).toFixed(1)} .. ${Math.max(...outs).toFixed(1)} LUFS ` +
        `(${(Math.max(...outs) - Math.min(...outs)).toFixed(1)} dB) — tighter = more uniform volume between triggers`);

    const C = (n: string): Clip => {
        const c = clips[n];
        if (!c) throw new Error(`clip ${n} not loaded`);
        return c;
    };
    const names = Object.keys(clips);

    // 1) Dual fire: two triggers in the SAME chunk (worst-case simultaneous).
    scenario('Dual fire — two triggers at t=0 (simultaneous)', 'real-dual-fire.wav', [
        { clip: C(names[0]!), startMs: 0 },
        { clip: C(names[1]!), startMs: 0 },
    ]);

    // 2) Staggered interleave: long clips overlapping mid-stream at offsets.
    const longSorted = Object.values(clips).sort((a, b) => b.durMs - a.durMs);
    scenario('Interleaved — three long clips staggered, overlapping in the middle', 'real-interleave.wav', [
        { clip: longSorted[0]!, startMs: 0 },
        { clip: longSorted[1]!, startMs: 400 },
        { clip: longSorted[2]!, startMs: 900 },
    ]);

    // 3) Pile-up: every clip fired inside a 1.5 s window — heavy concurrent sum.
    const pileup: Event[] = Object.values(clips).map((c, i) => ({ clip: c, startMs: i * 150 }));
    scenario('Pile-up — all clips within 1.5 s (limiter stress)', 'real-pileup.wav', pileup);

    // 4) Rapid same-trigger retrigger: same loud clip 4x, 80 ms apart (echo-stack).
    const loud = Object.values(clips).reduce((a, b) => (b.outLufs > a.outLufs ? b : a));
    scenario(`Rapid retrigger — "${loud.name}" x4, 80 ms apart`, 'real-retrigger.wav', [
        { clip: loud, startMs: 0 },
        { clip: loud, startMs: 80 },
        { clip: loud, startMs: 160 },
        { clip: loud, startMs: 240 },
    ]);

    console.log(`\nDone. Master WAVs in ${outDir} — listen to real-*.wav to hear the mixed bus.`);
}

main().catch((e) => { console.error(e); process.exit(1); });
