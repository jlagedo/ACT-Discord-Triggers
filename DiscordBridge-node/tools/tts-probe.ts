// Manual TTS probe: synthesize one line with an installed ONNX voice and write a
// WAV you can listen to. Exercises the exact bridge synthesis path (OnnxTts +
// the discord-host mono->stereo->48k conversion), so what you hear is what the
// bot would send to Discord.
//
//   npm run tts:probe -- --model "E:\...\vits-piper-pt_BR-faber-medium" --text "Cuidado!"
//   npm run tts:probe -- --model "E:\...\kokoro-multi-lang-v1_0" --family kokoro --sid 42 --lang pt-br
//
// Flags: --model <dir> (required) --family piper|kokoro --sid N --lang <espeak id>
//        --speed 0..20 --threads N --text "..." --out <file.wav>

import { OnnxTts, type OnnxSynthConfig } from '../src/tts.js';
import { monoFloat32ToStereoF32, resampleStereoF32 } from '../src/discord-host.js';
import { floatToInt16 } from '../src/audio-format.js';
import { writeWav16 } from '../src/wav-write.js';
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
        threads: Number(arg('threads', '1')) || 1,
    };
    const text = arg('text', 'Pull complete. The boss is at twelve percent. Stack for the raid wide.');
    const out = resolve(arg('out', 'tts-probe.wav'));

    const tts = new OnnxTts();
    const cfg = tts.configure(desc);
    if (!cfg.ok) {
        process.stderr.write(`Voice not usable: ${cfg.error}\n`);
        process.exitCode = 1;
        return;
    }
    process.stdout.write(`Synthesizing with ${tts.describe()} ...\n`);
    const t0 = process.hrtime.bigint();
    const audio = await tts.synth(text);
    const genMs = Number(process.hrtime.bigint() - t0) / 1e6;
    if (!audio) {
        process.stderr.write('Synthesis produced no audio.\n');
        process.exitCode = 1;
        return;
    }

    const stereo = monoFloat32ToStereoF32(audio.samples);
    const final = resampleStereoF32(stereo, audio.sampleRate, 48000);
    writeWav16(out, floatToInt16(final), { sampleRate: 48000, channels: 2 });

    const audioMs = (audio.samples.length / audio.sampleRate) * 1000;
    process.stdout.write(
        `Wrote ${out}\n` +
        `  model rate ${audio.sampleRate} Hz -> 48000 Hz / 16-bit / stereo\n` +
        `  audio ${audioMs.toFixed(0)} ms, gen ${genMs.toFixed(0)} ms (RTF ${(genMs / audioMs).toFixed(3)})\n`,
    );
}

main().catch((err: unknown) => {
    process.stderr.write(`tts-probe failed: ${err instanceof Error ? err.message : String(err)}\n`);
    process.exit(1);
});
