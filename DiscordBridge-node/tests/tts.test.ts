// Pure-unit tests for the ONNX TTS plumbing that needs neither the native addon
// nor a real model: the ttsParams parser, the speed mapping, the mono->stereo
// converter, the WAV writer, the diagnostic audio sink, and OnnxTts.configure's
// model-file validation (fs-only — the native runtime is loaded lazily in synth).
// Real synthesis is exercised separately in tts-synth.test.ts (gated on a model).

import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import { mkdtempSync, writeFileSync, mkdirSync, readdirSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import decode from 'audio-decode';

import { parseTtsParams, sliderToSpeed, OnnxTts } from '../src/tts.js';
import { monoFloat32ToStereoInt16 } from '../src/discord-host.js';
import { wavHeader16, writeWav16 } from '../src/wav-write.js';
import { createSinkFromEnv } from '../src/audio-sink.js';

function tempDir(prefix: string): string {
    return mkdtempSync(join(tmpdir(), prefix));
}

// ----------------------------------------------------------------------------
// parseTtsParams
// ----------------------------------------------------------------------------

test('parseTtsParams: undefined / non-onnx engine / missing modelDir -> null', () => {
    assert.equal(parseTtsParams(undefined), null);
    assert.equal(parseTtsParams({}), null);
    assert.equal(parseTtsParams({ engine: 'sapi', modelDir: 'C:\\x' }), null);
    assert.equal(parseTtsParams({ engine: 'onnx' }), null); // no modelDir
    assert.equal(parseTtsParams({ engine: 'onnx', modelDir: '' }), null);
});

test('parseTtsParams: piper descriptor with defaults', () => {
    const d = parseTtsParams({ engine: 'onnx', family: 'piper', modelDir: 'C:\\m\\faber' });
    assert.ok(d);
    assert.equal(d.family, 'piper');
    assert.equal(d.modelDir, 'C:\\m\\faber');
    assert.equal(d.sid, 0);
    assert.equal(d.lang, '');
    assert.equal(d.speedSlider, 10); // default mid slider
    assert.equal(d.threads, 1);
});

test('parseTtsParams: kokoro descriptor carries sid/lang/speed/threads', () => {
    const d = parseTtsParams({
        engine: 'onnx', family: 'kokoro', modelDir: 'C:\\m\\kokoro',
        sid: '42', lang: 'pt-br', speed: '15', threads: '2',
    });
    assert.ok(d);
    assert.equal(d.family, 'kokoro');
    assert.equal(d.sid, 42);
    assert.equal(d.lang, 'pt-br');
    assert.equal(d.speedSlider, 15);
    assert.equal(d.threads, 2);
});

test('parseTtsParams: unknown family falls back to piper; junk numbers use fallbacks', () => {
    const d = parseTtsParams({
        engine: 'onnx', family: 'whatever', modelDir: 'C:\\m',
        sid: 'NaN', speed: 'x', threads: 'abc',
    });
    assert.ok(d);
    assert.equal(d.family, 'piper');
    assert.equal(d.sid, 0);
    assert.equal(d.speedSlider, 10);
    assert.equal(d.threads, 1);
});

// ----------------------------------------------------------------------------
// sliderToSpeed
// ----------------------------------------------------------------------------

test('sliderToSpeed: 0..20 maps to 0.5..1.5, 10 = 1.0', () => {
    assert.equal(sliderToSpeed(0), 0.5);
    assert.equal(sliderToSpeed(10), 1.0);
    assert.equal(sliderToSpeed(20), 1.5);
    assert.equal(sliderToSpeed(5), 0.75);
});

test('sliderToSpeed: a non-positive result degrades to 1.0', () => {
    // 0.5 + slider/20 <= 0 only for slider <= -10; guard returns 1.0.
    assert.equal(sliderToSpeed(-20), 1.0);
});

// ----------------------------------------------------------------------------
// monoFloat32ToStereoInt16
// ----------------------------------------------------------------------------

test('monoFloat32ToStereoInt16: duplicates the channel and is 4 bytes/sample', () => {
    const mono = Float32Array.from([0, 0.5, -0.5]);
    const buf = monoFloat32ToStereoInt16(mono);
    assert.equal(buf.length, mono.length * 4);
    for (let i = 0; i < mono.length; i++) {
        const l = buf.readInt16LE(i * 4);
        const r = buf.readInt16LE(i * 4 + 2);
        assert.equal(l, r, `frame ${i}: L should equal R`);
    }
    assert.equal(buf.readInt16LE(0), 0);
    assert.equal(buf.readInt16LE(4), Math.round(0.5 * 0x7fff));
    assert.equal(buf.readInt16LE(8), Math.round(-0.5 * 0x8000));
});

test('monoFloat32ToStereoInt16: clamps out-of-range samples to full scale', () => {
    const mono = Float32Array.from([2.0, -2.0]);
    const buf = monoFloat32ToStereoInt16(mono);
    assert.equal(buf.readInt16LE(0), 0x7fff);   // +full scale
    assert.equal(buf.readInt16LE(4), -0x8000);  // -full scale
});

// ----------------------------------------------------------------------------
// wav-write
// ----------------------------------------------------------------------------

test('wavHeader16: declares RIFF/WAVE/fmt/data and correct sizes', () => {
    const dataBytes = 192; // 1 ms of 48k/16/stereo
    const h = wavHeader16(dataBytes, { sampleRate: 48000, channels: 2 });
    assert.equal(h.length, 44);
    assert.equal(h.toString('ascii', 0, 4), 'RIFF');
    assert.equal(h.readUInt32LE(4), 36 + dataBytes);
    assert.equal(h.toString('ascii', 8, 12), 'WAVE');
    assert.equal(h.readUInt16LE(20), 1);        // PCM
    assert.equal(h.readUInt16LE(22), 2);        // channels
    assert.equal(h.readUInt32LE(24), 48000);    // sample rate
    assert.equal(h.readUInt32LE(28), 48000 * 4); // byte rate
    assert.equal(h.readUInt16LE(32), 4);        // block align
    assert.equal(h.readUInt16LE(34), 16);       // bits
    assert.equal(h.toString('ascii', 36, 40), 'data');
    assert.equal(h.readUInt32LE(40), dataBytes);
});

test('writeWav16: round-trips through audio-decode preserving both channels', async () => {
    const dir = tempDir('act-wav-');
    try {
        // Distinct L/R so the decoder keeps two channels (audio-decode folds
        // identical channels to mono — see the sink test below).
        const frames = 240;
        const pcm = Buffer.alloc(frames * 4);
        for (let i = 0; i < frames; i++) {
            pcm.writeInt16LE(Math.round(Math.sin((i / frames) * Math.PI * 2) * 10000), i * 4);
            pcm.writeInt16LE(-3000, i * 4 + 2);
        }
        const path = join(dir, 'tone.wav');
        writeWav16(path, pcm, { sampleRate: 48000, channels: 2 });

        const { channelData, sampleRate } = await decode(readFileSync(path));
        assert.equal(sampleRate, 48000);
        assert.equal(channelData.length, 2);
        assert.equal(channelData[0]!.length, frames);
        // Spot-check samples survive the round trip (within int16 quantization).
        const backL = Math.round(channelData[0]![60]! * 0x8000);
        assert.ok(Math.abs(backL - pcm.readInt16LE(60 * 4)) <= 2, `L decoded ${backL}`);
        const backR = Math.round(channelData[1]![60]! * 0x8000);
        assert.ok(Math.abs(backR - (-3000)) <= 2, `R decoded ${backR}`);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

// ----------------------------------------------------------------------------
// audio-sink (createSinkFromEnv)
// ----------------------------------------------------------------------------

test('createSinkFromEnv: returns null when ACT_DT_AUDIO_SINK is unset', () => {
    const prev = process.env['ACT_DT_AUDIO_SINK'];
    delete process.env['ACT_DT_AUDIO_SINK'];
    try {
        assert.equal(createSinkFromEnv(), null);
    } finally {
        if (prev !== undefined) process.env['ACT_DT_AUDIO_SINK'] = prev;
    }
});

test('createSinkFromEnv: writes counted, sanitized WAV files into the dir', async () => {
    const dir = tempDir('act-sink-');
    const prev = process.env['ACT_DT_AUDIO_SINK'];
    process.env['ACT_DT_AUDIO_SINK'] = dir;
    try {
        const sink = createSinkFromEnv();
        assert.ok(sink);
        const pcm = Buffer.alloc(192); // 1 ms of silence is enough to write a valid WAV
        const p1 = sink.write('SpeakText-7', pcm);
        const p2 = sink.write('SpeakFile-8', pcm);
        const files = readdirSync(dir).sort();
        assert.deepEqual(files, ['0001-SpeakText-7.wav', '0002-SpeakFile-8.wav']);
        assert.ok(p1.endsWith('0001-SpeakText-7.wav'));
        assert.ok(p2.endsWith('0002-SpeakFile-8.wav'));
        // Written files decode as valid WAV (silence is L==R, so audio-decode
        // folds it to a single channel — assert validity, not channel count).
        const { sampleRate, channelData } = await decode(readFileSync(p1));
        assert.equal(sampleRate, 48000);
        assert.ok(channelData.length >= 1);
    } finally {
        if (prev !== undefined) process.env['ACT_DT_AUDIO_SINK'] = prev;
        else delete process.env['ACT_DT_AUDIO_SINK'];
        rmSync(dir, { recursive: true, force: true });
    }
});

// ----------------------------------------------------------------------------
// OnnxTts.configure — model-file validation (no native addon needed)
// ----------------------------------------------------------------------------

function fakePiper(dir: string): void {
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'tokens.txt'), 'x');
    writeFileSync(join(dir, 'model.onnx'), 'x');
}

function fakeKokoro(dir: string): void {
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'tokens.txt'), 'x');
    writeFileSync(join(dir, 'model.onnx'), 'x');
    writeFileSync(join(dir, 'voices.bin'), 'x');
}

test('OnnxTts.configure(null): unloads and reports not-ready', () => {
    const tts = new OnnxTts();
    const r = tts.configure(null);
    assert.equal(r.ok, true);
    assert.equal(tts.isReady(), false);
    assert.equal(tts.describe(), 'none');
});

test('OnnxTts.configure: missing model folder -> not ok, not ready', () => {
    const tts = new OnnxTts();
    const r = tts.configure({
        family: 'piper', modelDir: join(tmpdir(), 'act-no-such-' + Date.now()),
        sid: 0, lang: '', speedSlider: 10, threads: 1,
    });
    assert.equal(r.ok, false);
    assert.match(r.error, /not found/);
    assert.equal(tts.isReady(), false);
});

test('OnnxTts.configure: piper dir with .onnx + tokens.txt -> ready', () => {
    const dir = tempDir('act-piper-');
    try {
        fakePiper(dir);
        const tts = new OnnxTts();
        const r = tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 10, threads: 1 });
        assert.equal(r.ok, true, r.error);
        assert.equal(tts.isReady(), true);
        assert.match(tts.describe(), /piper/);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('OnnxTts.configure: piper dir missing tokens.txt -> not ok', () => {
    const dir = tempDir('act-piper-notok-');
    try {
        mkdirSync(dir, { recursive: true });
        writeFileSync(join(dir, 'model.onnx'), 'x'); // no tokens.txt
        const tts = new OnnxTts();
        const r = tts.configure({ family: 'piper', modelDir: dir, sid: 0, lang: '', speedSlider: 10, threads: 1 });
        assert.equal(r.ok, false);
        assert.match(r.error, /tokens\.txt/);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

test('OnnxTts.configure: kokoro requires model.onnx + voices.bin', () => {
    const noVoices = tempDir('act-kok-novoices-');
    const full = tempDir('act-kok-full-');
    try {
        mkdirSync(noVoices, { recursive: true });
        writeFileSync(join(noVoices, 'tokens.txt'), 'x');
        writeFileSync(join(noVoices, 'model.onnx'), 'x'); // no voices.bin
        const a = new OnnxTts();
        const ra = a.configure({ family: 'kokoro', modelDir: noVoices, sid: 0, lang: '', speedSlider: 10, threads: 1 });
        assert.equal(ra.ok, false);
        assert.match(ra.error, /voices\.bin/);

        fakeKokoro(full);
        const b = new OnnxTts();
        const rb = b.configure({ family: 'kokoro', modelDir: full, sid: 42, lang: 'pt-br', speedSlider: 10, threads: 1 });
        assert.equal(rb.ok, true, rb.error);
        assert.equal(b.isReady(), true);
        assert.match(b.describe(), /kokoro sid=42 lang='pt-br'/);
    } finally {
        rmSync(noVoices, { recursive: true, force: true });
        rmSync(full, { recursive: true, force: true });
    }
});
