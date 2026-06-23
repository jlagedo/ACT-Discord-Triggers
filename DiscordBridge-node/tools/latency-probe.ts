// Per-stage processing-latency probe. Times each DSP stage of the per-clip path
// (decode+resample -> normalize -> declick) on real trigger audio, the way the
// bridge's recv->enqueue stamp lumps them. Warm runs (decode hits the OS cache),
// median of N. Reports alongside the fixed structural latencies of the stream.
//
//   node --import tsx tools/latency-probe.ts [--in <dir>] [--iters 7]

import { readdirSync } from 'node:fs';
import { join } from 'node:path';
import { performance } from 'node:perf_hooks';

import { decodeFileToFinalPcm } from '../src/discord-host.js';
import { normalize, measureLevel } from '../src/normalize.js';
import { declick } from '../src/declick.js';
import { initResampler } from '../src/resample.js';
import { warmupDecoders } from '../src/audio-decode.js';
import { arg } from './args.js';

const SR = 48000;
const inDir = arg('in', './.e2e-out/triggers');
const ITERS = Number(arg('iters', '7'));

const median = (xs: number[]): number => {
    const s = [...xs].sort((a, b) => a - b);
    return s[Math.floor(s.length / 2)]!;
};

async function main(): Promise<void> {
    await initResampler();
    await warmupDecoders();
    const files = readdirSync(inDir).filter((f) => /\.(wav|mp3|ogg|flac)$/i.test(f)).sort();

    console.log(`Per-clip processing latency — median of ${ITERS} warm runs\n`);
    console.log('  clip                   dur     decode+rs   normalize   declick    total (recv->enqueue)');
    console.log('  ' + '-'.repeat(82));

    const totals: number[] = [];
    for (const f of files) {
        const path = join(inDir, f);
        const dec: number[] = [], nrm: number[] = [], dck: number[] = [], tot: number[] = [];
        let durMs = 0;
        for (let it = 0; it < ITERS; it++) {
            const t0 = performance.now();
            const decoded = await decodeFileToFinalPcm(path);
            const t1 = performance.now();
            measureLevel(decoded);
            const norm = normalize(decoded, -17);
            const t2 = performance.now();
            const buf = declick(norm.applied ? norm.samples : decoded);
            const t3 = performance.now();
            dec.push(t1 - t0); nrm.push(t2 - t1); dck.push(t3 - t2); tot.push(t3 - t0);
            durMs = (buf.length / 2 / SR) * 1000;
        }
        totals.push(median(tot));
        console.log(
            '  ' + f.replace(/\.[^.]+$/, '').padEnd(22) +
            (durMs / 1000).toFixed(2).padStart(6) + 's' +
            (median(dec).toFixed(2) + 'ms').padStart(12) +
            (median(nrm).toFixed(2) + 'ms').padStart(12) +
            (median(dck).toFixed(2) + 'ms').padStart(11) +
            (median(tot).toFixed(2) + 'ms').padStart(14),
        );
    }
    console.log(`\n  Processing latency across clips: ${Math.min(...totals).toFixed(1)}–${Math.max(...totals).toFixed(1)} ms ` +
        `(median ${median(totals).toFixed(1)} ms). This is the recv->enqueue span.`);

    // Fixed structural latencies added to the audio stream (not CPU time).
    const LOOKAHEAD = 96, OPUS = 960;
    console.log('\n  Fixed structural latency added to the stream (constant, not per-clip CPU):');
    console.log(`    mixer scheduling jitter   0..20 ms   (voice waits for the next 20 ms pull; log: enqueue->firstEmit)`);
    console.log(`    limiter look-ahead        ${(LOOKAHEAD / SR * 1000).toFixed(1)} ms      (${LOOKAHEAD} frames, only when limiter enabled — it is by default)`);
    console.log(`    Opus frame / mix chunk    ${(OPUS / SR * 1000).toFixed(0)} ms       (one 960-frame chunk per emit)`);
    console.log(`    Discord jitter+network    variable    (bot mode; your logs show rtt=n/a => local-output mode, none)`);
    console.log(`    local-output device buf   ~10..30 ms  (WASAPI shared-mode, local mode only)`);
}

main().catch((e) => { console.error(e); process.exit(1); });
