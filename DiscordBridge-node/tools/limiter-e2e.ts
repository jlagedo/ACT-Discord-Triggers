// End-to-end harness for the master bus limiter. Drives the REAL PcmMixer (the
// same code path that feeds Discord's Opus encoder) with overlapping full-scale
// signals, captures the mixed 48k/16/stereo bus to WAV files you can listen to,
// and runs edge-limit checks. The diagnostic ACT_DT_AUDIO_SINK only captures
// per-clip PRE-mix audio, so this is the way to observe the limiter — it lives
// on the summed bus, after mixing.
//
//   npm run limiter:e2e            (writes WAVs to ./.e2e-out)
//   npm run limiter:e2e -- --out <dir>
//
// Reports peak level and the count of samples pinned at the int16 rails
// (clipping) for each scenario, limiter OFF vs ON.

import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

import { PcmMixer } from '../src/pcm-mixer.js';
import { writeWav16 } from '../src/wav-write.js';
import { dbToLinear } from '../src/normalize.js';
import { arg } from './args.js';

const SR = 48000;
const CEILING_DB = -1;
const CEILING = dbToLinear(CEILING_DB); // ≈ 0.891
const FULL_SCALE = 32768;

// Interleaved float32 stereo sine at `freq`, amplitude `amp`, `seconds` long.
function sine(freq: number, amp: number, seconds: number): Float32Array {
    const frames = Math.round(SR * seconds);
    const out = new Float32Array(frames * 2);
    const w = (2 * Math.PI * freq) / SR;
    for (let i = 0; i < frames; i++) {
        const s = amp * Math.sin(i * w);
        out[i * 2] = s;
        out[i * 2 + 1] = s;
    }
    return out;
}

// Interleaved DC at +amp on both channels.
function dc(amp: number, seconds: number): Float32Array {
    const frames = Math.round(SR * seconds);
    const out = new Float32Array(frames * 2);
    out.fill(amp);
    return out;
}

// Full-scale alternating ±1 (Nyquist) — the worst case for peak buildup.
function nyquist(amp: number, seconds: number): Float32Array {
    const frames = Math.round(SR * seconds);
    const out = new Float32Array(frames * 2);
    for (let i = 0; i < frames; i++) {
        const s = (i & 1) === 0 ? amp : -amp;
        out[i * 2] = s;
        out[i * 2 + 1] = s;
    }
    return out;
}

interface Stats {
    peak: number;        // max |sample| as int16
    peakDbfs: number;
    clipped: number;     // samples pinned at ±32767/-32768
    total: number;
    nonFinite: number;   // NaN/Inf leaks (should always be 0)
}

// Pull `seconds` worth of chunks out of the mixer and concat to one s16le buffer.
function drain(mixer: PcmMixer, seconds: number): Buffer {
    const chunkBytes = 3840; // 960 frames * 2ch * 2 bytes
    const chunks = Math.ceil((SR * seconds) / 960);
    const parts: Buffer[] = [];
    for (let i = 0; i < chunks; i++) parts.push(mixer._mixOneChunk());
    return Buffer.concat(parts, chunks * chunkBytes);
}

function analyze(pcm: Buffer): Stats {
    let peak = 0;
    let clipped = 0;
    let nonFinite = 0;
    const total = pcm.length / 2;
    for (let i = 0; i < pcm.length; i += 2) {
        const s = pcm.readInt16LE(i);
        if (!Number.isFinite(s)) nonFinite++;
        const a = s < 0 ? -s : s;
        if (a > peak) peak = a;
        if (s >= 32767 || s <= -32768) clipped++;
    }
    return {
        peak,
        peakDbfs: peak > 0 ? 20 * Math.log10(peak / FULL_SCALE) : -Infinity,
        clipped,
        total,
        nonFinite,
    };
}

function row(label: string, st: Stats): string {
    const pct = ((st.clipped / st.total) * 100).toFixed(2);
    return `${label.padEnd(34)} peak=${String(st.peak).padStart(6)} `
        + `(${st.peakDbfs.toFixed(2).padStart(7)} dBFS)  clipped=${String(st.clipped).padStart(7)} `
        + `(${pct.padStart(6)}%)  nonFinite=${st.nonFinite}`;
}

// Build a mixer pre-loaded with `make()`'s voices, optionally limited.
function run(make: () => Float32Array[], limited: boolean, seconds: number): Buffer {
    const m = new PcmMixer();
    if (limited) m.configureLimiter(true, CEILING);
    for (const v of make()) m.addVoice(v);
    return drain(m, seconds);
}

function main(): void {
    const outDir = arg('out', join(process.cwd(), '.e2e-out'));
    mkdirSync(outDir, { recursive: true });
    const fmt = { sampleRate: SR, channels: 2 };

    const ceilingInt = Math.round(CEILING * FULL_SCALE);
    console.log(`Master bus limiter E2E — ceiling ${CEILING_DB} dBTP (${CEILING.toFixed(4)} ≈ ${ceilingInt})`);
    console.log(`Output dir: ${outDir}\n`);

    let failures = 0;
    const check = (cond: boolean, msg: string): void => {
        if (!cond) { failures++; console.log(`   ✗ ${msg}`); } else { console.log(`   ✓ ${msg}`); }
    };

    // --- Scenario 1: three overlapping near-full-scale sines (sum ≈ 2.7x) -----
    const overlap = (): Float32Array[] => [
        sine(220, 0.9, 2),
        sine(440, 0.9, 2),
        sine(660, 0.9, 2),
    ];
    const offPcm = run(overlap, false, 2);
    const onPcm = run(overlap, true, 2);
    writeWav16(join(outDir, 'overlap-limiter-off.wav'), offPcm, fmt);
    writeWav16(join(outDir, 'overlap-limiter-on.wav'), onPcm, fmt);
    const offSt = analyze(offPcm);
    const onSt = analyze(onPcm);
    console.log('Scenario 1 — three overlapping 0.9 sines (sum ~2.7x full scale):');
    console.log('   ' + row('limiter OFF (hard clamp)', offSt));
    console.log('   ' + row('limiter ON  (-1 dBTP)', onSt));
    check(offSt.clipped > 0, 'OFF actually clips (demonstrates the problem)');
    check(onSt.clipped === 0, 'ON has zero clipped samples');
    check(onSt.peak <= ceilingInt + 2, `ON peak within the ceiling (${onSt.peak} <= ${ceilingInt + 2})`);
    check(onSt.nonFinite === 0, 'ON output is all finite');
    console.log();

    // --- Edge 1: summed full-scale DC -----------------------------------------
    const dcPcm = run(() => [dc(0.95, 1), dc(0.95, 1), dc(0.95, 1)], true, 1);
    const dcSt = analyze(dcPcm);
    writeWav16(join(outDir, 'edge-dc.wav'), dcPcm, fmt);
    console.log('Edge 1 — three 0.95 DC voices (sum 2.85x):');
    console.log('   ' + row('limiter ON', dcSt));
    check(dcSt.clipped === 0, 'no clipping on summed DC');
    check(dcSt.peak <= ceilingInt + 2, 'DC held at the ceiling');
    console.log();

    // --- Edge 2: Nyquist (alternating ±full-scale) ----------------------------
    const nyPcm = run(() => [nyquist(1.0, 1), nyquist(1.0, 1)], true, 1);
    const nySt = analyze(nyPcm);
    writeWav16(join(outDir, 'edge-nyquist.wav'), nyPcm, fmt);
    console.log('Edge 2 — two full-scale Nyquist voices (sum 2x, worst-case peaks):');
    console.log('   ' + row('limiter ON', nySt));
    check(nySt.clipped === 0, 'no clipping on Nyquist sum');
    check(nySt.nonFinite === 0, 'finite output on Nyquist sum');
    console.log();

    // --- Edge 3: sudden full-scale transient amid silence (look-ahead) --------
    const transient = (): Float32Array[] => {
        const frames = SR; // 1 s
        const buf = new Float32Array(frames * 2);
        // silence, then a 5 ms full-scale burst starting at 0.5 s
        const start = Math.round(SR * 0.5);
        const end = start + Math.round(SR * 0.005);
        for (let i = start; i < end; i++) { buf[i * 2] = 1.0; buf[i * 2 + 1] = 1.0; }
        return [buf];
    };
    const trPcm = run(transient, true, 1);
    const trSt = analyze(trPcm);
    writeWav16(join(outDir, 'edge-transient.wav'), trPcm, fmt);
    console.log('Edge 3 — sudden 5 ms full-scale transient amid silence:');
    console.log('   ' + row('limiter ON', trSt));
    check(trSt.clipped === 0, 'look-ahead catches the transient onset (no clip)');
    check(trSt.peak <= ceilingInt + 2, 'transient held at the ceiling');
    console.log();

    // --- Control: a quiet sub-ceiling voice passes through unchanged -----------
    const quietPcm = run(() => [sine(440, 0.2, 1)], true, 1);
    const quietSt = analyze(quietPcm);
    const expectedQuiet = Math.round(0.2 * FULL_SCALE);
    console.log('Control — single 0.2 (sub-ceiling) sine should pass at unity:');
    console.log('   ' + row('limiter ON', quietSt));
    check(Math.abs(quietSt.peak - expectedQuiet) <= 3, `peak ≈ ${expectedQuiet} (unity, no gain reduction)`);
    console.log();

    console.log(failures === 0
        ? '✅ ALL LIMITER E2E CHECKS PASSED'
        : `❌ ${failures} CHECK(S) FAILED`);
    process.exit(failures === 0 ? 0 : 1);
}

main();
