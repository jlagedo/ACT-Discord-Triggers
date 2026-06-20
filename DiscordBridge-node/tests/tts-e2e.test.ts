// End-to-end ONNX confirmation: spawn the real bridge with the diagnostic audio
// sink enabled, push a real Piper voice via SetConfig's ttsParams, send SpeakText,
// and assert a 48 kHz/stereo, non-silent WAV lands on disk. This exercises the
// whole live path — pipe framing, config parse, synth, fx/normalize/declick, the
// sink — without needing a Discord voice channel.
//
// Auto-skips off-Windows (named pipes) or when the addon/models are absent.

import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import decode from 'audio-decode';

import { Op, PROTOCOL_VERSION } from '../src/protocol.js';
import { spawnBridge, killIfAlive, TestPipeClient, type Bridge } from './helpers/bridge-harness.js';
import { synthSkip, modelDir, rmsPcm16Stereo, PIPER_EN_US, KOKORO } from './helpers/synth-fixtures.js';

const SKIP_NON_WINDOWS = process.platform !== 'win32';
const skipReason = SKIP_NON_WINDOWS ? 'Windows-only (named pipes)' : synthSkip();
const skip = { skip: skipReason };

const LINE = 'Pull complete. The boss is at twelve percent. Stack for the raid wide.';
const PT_LINE = 'Cuidado, ataque pesado chegando. Use a mitigação agora e saia do fogo.';

function piperParams(dir: string): Record<string, string> {
    return { engine: 'onnx', family: 'piper', modelDir: dir, sid: '0', lang: '', speed: '10', threads: '1' };
}

// Kokoro pt-BR (pf_dora, sid 42) — drives the baked espeak `lang` ('pt-br') over
// the wire and through the kokoro synth path (different from Piper's).
function kokoroPtParams(dir: string): Record<string, string> {
    return { engine: 'onnx', family: 'kokoro', modelDir: dir, sid: '42', lang: 'pt-br', speed: '10', threads: '2' };
}

async function withBridge(
    suffix: string,
    sinkDir: string | null,
    fn: (c: TestPipeClient, sinkDir: string | null) => Promise<void>,
): Promise<void> {
    const env = sinkDir ? { ACT_DT_AUDIO_SINK: sinkDir } : undefined;
    const bridge: Bridge = await spawnBridge(suffix, env);
    let client: TestPipeClient | null = null;
    try {
        client = await TestPipeClient.connect(bridge.pipePath);
        const hello = await client.send(Op.Hello, { protocolVersion: PROTOCOL_VERSION });
        assert.equal(hello['ok'], true);
        await fn(client, sinkDir);
    } finally {
        client?.close();
        await killIfAlive(bridge);
    }
}

test('SpeakText through the bridge writes a non-silent 48k/stereo WAV', skip, async () => {
    const sinkDir = mkdtempSync(join(tmpdir(), 'act-e2e-'));
    try {
        await withBridge('onnx-ok', sinkDir, async (client) => {
            const cfg = await client.send(Op.SetConfig, { config: {}, ttsParams: piperParams(modelDir(PIPER_EN_US)!) });
            assert.equal(cfg['ok'], true);

            const spoke = await client.send(Op.SpeakText, { text: LINE });
            assert.equal(spoke['op'], Op.Result);
            assert.equal(spoke['ok'], true, `SpeakText failed: ${String(spoke['error'])}`);

            const wavs = readdirSync(sinkDir).filter((f) => f.endsWith('.wav'));
            assert.equal(wavs.length, 1, `expected one captured WAV, got ${wavs.join(',')}`);
            const { sampleRate, channelData } = await decode(readFileSync(join(sinkDir, wavs[0]!)));
            assert.equal(sampleRate, 48000, 'capture must be at the bridge target rate');
            assert.ok(channelData[0]!.length > 48000 * 0.5, 'captured clip too short');

            // Re-read as raw PCM (after the 44-byte WAV header) to confirm real signal.
            const pcm = readFileSync(join(sinkDir, wavs[0]!)).subarray(44);
            assert.ok(rmsPcm16Stereo(pcm) > 0.01, `captured audio ~silent (rms=${rmsPcm16Stereo(pcm)})`);
        });
    } finally {
        rmSync(sinkDir, { recursive: true, force: true });
    }
});

test('SpeakText with a Kokoro pt-BR voice synthesizes through the bridge (baked lang over the wire)', skip, async () => {
    const sinkDir = mkdtempSync(join(tmpdir(), 'act-e2e-'));
    try {
        await withBridge('onnx-kokoro', sinkDir, async (client) => {
            const cfg = await client.send(Op.SetConfig, { config: {}, ttsParams: kokoroPtParams(modelDir(KOKORO)!) });
            assert.equal(cfg['ok'], true);

            const spoke = await client.send(Op.SpeakText, { text: PT_LINE });
            assert.equal(spoke['ok'], true, `SpeakText failed: ${String(spoke['error'])}`);

            const wavs = readdirSync(sinkDir).filter((f) => f.endsWith('.wav'));
            assert.equal(wavs.length, 1, `expected one captured WAV, got ${wavs.join(',')}`);
            const { sampleRate, channelData } = await decode(readFileSync(join(sinkDir, wavs[0]!)));
            assert.equal(sampleRate, 48000, 'capture must be at the bridge target rate');
            assert.ok(channelData[0]!.length > 48000 * 0.5, 'captured clip too short');
            const pcm = readFileSync(join(sinkDir, wavs[0]!)).subarray(44);
            assert.ok(rmsPcm16Stereo(pcm) > 0.01, `captured audio ~silent (rms=${rmsPcm16Stereo(pcm)})`);
        });
    } finally {
        rmSync(sinkDir, { recursive: true, force: true });
    }
});

test('SpeakText with no ONNX voice configured is skipped (not ready), bridge stays up', skip, async () => {
    const sinkDir = mkdtempSync(join(tmpdir(), 'act-e2e-'));
    try {
        await withBridge('onnx-novoice', sinkDir, async (client) => {
            // SetConfig without ttsParams => no voice loaded.
            await client.send(Op.SetConfig, { config: {} });
            const spoke = await client.send(Op.SpeakText, { text: LINE });
            assert.equal(spoke['ok'], false);
            assert.match(String(spoke['error']), /not ready/i);
            assert.equal(readdirSync(sinkDir).length, 0, 'no audio should be captured');
            // Bridge is still responsive.
            const ic = await client.send(Op.IsConnected);
            assert.equal((ic['data'] as Record<string, unknown>)['connected'], false);
        });
    } finally {
        rmSync(sinkDir, { recursive: true, force: true });
    }
});

test('SetConfig with a missing model dir warns + leaves no voice; SpeakText skips; bridge survives', skip, async () => {
    const sinkDir = mkdtempSync(join(tmpdir(), 'act-e2e-'));
    try {
        await withBridge('onnx-baddir', sinkDir, async (client) => {
            const bad = piperParams(join(tmpdir(), 'act-no-model-' + Date.now()));
            const cfg = await client.send(Op.SetConfig, { config: {}, ttsParams: bad });
            assert.equal(cfg['ok'], true, 'SetConfig itself always succeeds');

            const spoke = await client.send(Op.SpeakText, { text: LINE });
            assert.equal(spoke['ok'], false);
            assert.equal(readdirSync(sinkDir).length, 0);

            // A Warn log about the unavailable voice was pushed.
            const warned = client.logs.some((l) =>
                l['level'] === 'Warn' && /ONNX voice unavailable/i.test(String(l['message'])));
            assert.ok(warned, `expected an "ONNX voice unavailable" warning; logs=${JSON.stringify(client.logs)}`);

            const ic = await client.send(Op.IsConnected);
            assert.equal((ic['data'] as Record<string, unknown>)['connected'], false);
        });
    } finally {
        rmSync(sinkDir, { recursive: true, force: true });
    }
});
