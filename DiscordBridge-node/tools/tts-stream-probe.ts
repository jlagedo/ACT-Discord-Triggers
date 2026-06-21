// Streaming-synthesis probe: synthesize one line and report how sherpa's
// onProgress fires — chunk count, time-to-first-chunk, inter-chunk wall-clock
// deltas, and total gen time vs audio duration. This answers the question the
// streaming TTS path hinges on: does the model stream partial audio during a
// single utterance (so playback can start early), or emit it all at the end?
//
// It also assembles the streamed chunks through the exact bridge streaming path
// (monoFloat32ToStereoInt16 + StreamingResampler) and writes a WAV, so you can
// confirm the streamed-and-resampled audio is clean.
//
//   npm run tts:stream-probe -- --model "E:\ai\vits-piper-en_US-amy-medium" --text "Stack."
//   npm run tts:stream-probe -- --model "E:\ai\kokoro-multi-lang-v1_0" --family kokoro --sid 42 --lang pt-br
//
// Flags mirror tts:probe: --model <dir> (required) --family piper|kokoro --sid N
//   --lang <espeak> --speed 0..20 --threads N --text "..." --out <file.wav>

import { OnnxTts, type OnnxSynthConfig } from '../src/tts.js';
import { monoFloat32ToStereoF32 } from '../src/discord-host.js';
import { floatToInt16 } from '../src/audio-format.js';
import { StreamingResampler } from '../src/stream-resampler.js';
import { writeWav16 } from '../src/wav-write.js';
import { performance } from 'node:perf_hooks';
import { resolve } from 'node:path';
import { arg } from './args.js';

async function main(): Promise<void> {
    const modelDir = arg('model', '');
    if (!modelDir) {
        process.stderr.write('Required: --model <modelDir>. See header for usage.\n');
        process.exitCode = 1;
        return;
    }
    const desc: OnnxSynthConfig = {
        family: arg('family', 'piper') === 'kokoro' ? 'kokoro' : 'piper',
        modelDir: resolve(modelDir),
        sid: Number(arg('sid', '0')) || 0,
        lang: arg('lang', ''),
        speedSlider: Number(arg('speed', '10')) || 10,
        threads: Number(arg('threads', '4')) || 4,
    };
    const text = arg('text', 'Stack now. The boss is at twelve percent.');
    const out = resolve(arg('out', 'tts-stream-probe.wav'));

    const tts = new OnnxTts();
    const cfg = tts.configure(desc);
    if (!cfg.ok) {
        process.stderr.write(`Voice not usable: ${cfg.error}\n`);
        process.exitCode = 1;
        return;
    }
    // Warm-up so the timing reflects steady-state, not the cold model load.
    await tts.synth('Ready.');

    const rs = { r: null as StreamingResampler | null };
    const outParts: Float32Array[] = [];
    let srcRate = 0;
    let count = 0;
    let totalSamples = 0;
    let firstChunkMs = -1;
    let lastT = 0;
    const deltas: number[] = [];

    process.stdout.write(`Streaming "${text}" through ${tts.describe()} ...\n`);
    const t0 = performance.now();
    await tts.synth(text, (samples, rate) => {
        const now = performance.now();
        if (count === 0) { firstChunkMs = now - t0; srcRate = rate; }
        else deltas.push(now - lastT);
        lastT = now;
        count++;
        totalSamples += samples.length;
        if (!rs.r) rs.r = new StreamingResampler(rate, 48000);
        outParts.push(rs.r.push(monoFloat32ToStereoF32(samples)));
    });
    if (rs.r) outParts.push(rs.r.flush());
    const genMs = performance.now() - t0;

    if (count === 0) {
        process.stderr.write('No onProgress chunks fired — model did not stream this utterance.\n');
        process.exitCode = 1;
        return;
    }

    let total = 0;
    for (const p of outParts) total += p.length;
    const final = new Float32Array(total);
    let off = 0;
    for (const p of outParts) { final.set(p, off); off += p.length; }
    writeWav16(out, floatToInt16(final), { sampleRate: 48000, channels: 2 });

    const audioMs = (totalSamples / srcRate) * 1000;
    const avgDelta = deltas.length ? deltas.reduce((a, b) => a + b, 0) / deltas.length : 0;
    const maxDelta = deltas.length ? Math.max(...deltas) : 0;
    process.stdout.write(
        `\nchunks:            ${count}\n` +
        `time-to-first:     ${firstChunkMs.toFixed(1)} ms  (vs ${genMs.toFixed(1)} ms total gen)\n` +
        `first-chunk share: ${(100 * firstChunkMs / genMs).toFixed(0)}% of gen time\n` +
        `inter-chunk delta: avg ${avgDelta.toFixed(1)} ms, max ${maxDelta.toFixed(1)} ms\n` +
        `audio duration:    ${audioMs.toFixed(0)} ms (RTF ${(genMs / audioMs).toFixed(3)})\n` +
        `verdict:           ${count > 1 ? 'STREAMS intra-utterance (chunks > 1)' : 'single-shot (1 chunk) — needs sentence-split for short lines'}\n` +
        `wrote:             ${out}\n`,
    );
}

main().catch((err: unknown) => {
    process.stderr.write(`tts-stream-probe failed: ${err instanceof Error ? err.message : String(err)}\n`);
    process.exit(1);
});
