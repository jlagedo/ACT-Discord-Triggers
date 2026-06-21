// Real-synthesis tests: drive OnnxTts against the actual Piper + Kokoro models
// and confirm the bridge genuinely produces audio. Auto-skips unless the native
// addon and the voice models are both present (see synth-fixtures.ts) — the rest
// of the suite covers the wiring without them.
//
// Never feed an invalid espeak `lang` here: an unknown one hard-exit()s the whole
// process with no catchable error (the crash the static catalog `lang` prevents),
// which would take the test runner down with it. Only known-good ids are used.

import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { OnnxTts } from '../src/tts.js';
import { monoFloat32ToStereoF32, resampleStereoF32 } from '../src/discord-host.js';
import {
    synthSkip, modelDir, rms,
    PIPER_PT_BR, PIPER_EN_US, KOKORO,
} from './helpers/synth-fixtures.js';

const skip = { skip: synthSkip() };

const EN_LINE = 'Pull complete. The boss is at twelve percent. Stack for the raid wide.';
const PT_LINE = 'Cuidado, ataque pesado chegando. Use a mitigação agora e saia do fogo.';
const SILENCE_FLOOR = 0.01; // RMS well above digital silence => real speech

test('Piper en_US: synth yields non-silent 22.05 kHz mono audio', skip, async () => {
    const dir = modelDir(PIPER_EN_US)!;
    const tts = new OnnxTts();
    assert.equal(tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 10, threads: 1 }).ok, true);
    assert.equal(tts.isReady(), true);
    const audio = await tts.synth(EN_LINE);
    assert.ok(audio, 'expected audio');
    assert.equal(audio.sampleRate, 22050);
    assert.ok(audio.samples.length > 22050 * 0.5, `too short: ${audio.samples.length} samples`);
    assert.ok(rms(audio.samples) > SILENCE_FLOOR, `audio is ~silent (rms=${rms(audio.samples)})`);
});

test('Piper pt_BR: synth yields non-silent audio (model carries its own espeak voice)', skip, async () => {
    const dir = modelDir(PIPER_PT_BR)!;
    const tts = new OnnxTts();
    // Piper pt_BR uses lang:'' — each Piper model embeds its own espeak voice.
    assert.equal(tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 10, threads: 1 }).ok, true);
    const audio = await tts.synth(PT_LINE);
    assert.ok(audio);
    assert.equal(audio.sampleRate, 22050);
    assert.ok(rms(audio.samples) > SILENCE_FLOOR, `rms=${rms(audio.samples)}`);
});

test('Kokoro: one instance speaks pt-BR, en-GB and en-US via per-call lang', skip, async () => {
    const dir = modelDir(KOKORO)!;
    const tts = new OnnxTts();
    // pt-BR (pf_dora, sid 42, lang pt-br) — the baked-lang path that fixed the
    // "Portuguese phonemized as English" bug.
    assert.equal(tts.configure({ family: 'kokoro', modelDir: dir, sid: 42, lang: 'pt-br', speedSlider: 10, threads: 2 }).ok, true);
    const pt = await tts.synth(PT_LINE);
    assert.ok(pt);
    assert.equal(pt.sampleRate, 24000);
    assert.ok(rms(pt.samples) > SILENCE_FLOOR, `pt rms=${rms(pt.samples)}`);

    // en-GB (bf_alice, sid 20, lang en-gb-x-rp — NOT plain "en-gb", which crashes).
    assert.equal(tts.configure({ family: 'kokoro', modelDir: dir, sid: 20, lang: 'en-gb-x-rp', speedSlider: 10, threads: 2 }).ok, true);
    const gb = await tts.synth(EN_LINE);
    assert.ok(gb);
    assert.ok(rms(gb.samples) > SILENCE_FLOOR, `gb rms=${rms(gb.samples)}`);

    // en-US (af_alloy, sid 0, lang '' => English via lexicon).
    assert.equal(tts.configure({ family: 'kokoro', modelDir: dir, sid: 0, lang: '', speedSlider: 10, threads: 2 }).ok, true);
    const us = await tts.synth(EN_LINE);
    assert.ok(us);
    assert.ok(rms(us.samples) > SILENCE_FLOOR, `us rms=${rms(us.samples)}`);
});

test('Speed slider changes output length (slower => more samples)', skip, async () => {
    const dir = modelDir(PIPER_EN_US)!;
    const tts = new OnnxTts();
    // Slow (slider 5 => 0.75x): reconfigure keeps the same loaded model (key is
    // modelDir+threads), only the per-call speed changes.
    tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 5, threads: 1 });
    const slow = await tts.synth(EN_LINE);
    tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 15, threads: 1 });
    const fast = await tts.synth(EN_LINE);
    assert.ok(slow && fast);
    assert.ok(slow.samples.length > fast.samples.length * 1.2,
        `expected slower clip clearly longer: slow=${slow.samples.length} fast=${fast.samples.length}`);
});

test('Synth output converts cleanly to the bridge 48k float stereo format', skip, async () => {
    const dir = modelDir(PIPER_EN_US)!;
    const tts = new OnnxTts();
    tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 10, threads: 1 });
    const audio = await tts.synth(EN_LINE);
    assert.ok(audio);
    const stereoSrc = monoFloat32ToStereoF32(audio.samples);
    const final = resampleStereoF32(stereoSrc, audio.sampleRate, 48000);
    // Duration is preserved within a frame or two of resampling rounding.
    const srcMs = (audio.samples.length / audio.sampleRate) * 1000;
    const finalMs = (final.length / 2 / 48000) * 1000;
    assert.ok(Math.abs(srcMs - finalMs) < 5, `duration drift ${srcMs}ms -> ${finalMs}ms`);
    assert.ok(rms(final) > SILENCE_FLOOR, `converted audio ~silent (rms=${rms(final)})`);
});
