import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { DiscordHost } from '../src/discord-host.js';
import type { OpResult } from '../src/pipe-server.js';
import { DEFAULT_CONFIG_VIEW, type BridgeConfigView } from '../src/protocol.js';

// Locks in the cleanup-on-throw contract of DiscordHost._streamSpeakText: the
// mixer voice it opens must be closed even when finalizing the stream throws,
// so a synth/declick failure can't leak one of the mixer's finite voice slots.
// Drives the private method directly with a stub OnnxTts (no native sherpa) and a
// fake mixer (no real Opus stream); a 48 kHz mono chunk keeps the resampler in
// identity mode so no WASM resampler is needed either.

function cfg(overrides: Partial<BridgeConfigView>): BridgeConfigView {
    return { ...DEFAULT_CONFIG_VIEW, ...overrides };
}

interface FakeMixer {
    opened: number;
    closed: number;
    openVoice(): object;
    appendToVoice(): void;
    closeVoice(): void;
}

// Records openVoice/closeVoice. `throwOnAppend` makes the final declicked buffer's
// append throw, simulating a failure inside the finalize block (after the voice is
// already open) — the case the try/finally guards.
function fakeMixer(throwOnAppend: boolean): FakeMixer {
    return {
        opened: 0,
        closed: 0,
        openVoice() { this.opened++; return {}; },
        appendToVoice() { if (throwOnAppend) throw new Error('append boom'); },
        closeVoice() { this.closed++; },
    };
}

// A ready voice that streams one 48 kHz mono chunk through onProgress, then
// resolves. 48 kHz == the bridge target, so MonoStreamResampler stays identity.
const fakeTts = {
    isReady: () => true,
    bakedLevel: () => null,
    describe: () => 'fake-voice',
    synth: (
        _text: string,
        onProgress?: (samples: Float32Array, srcRate: number) => void,
    ): Promise<{ samples: Float32Array; sampleRate: number }> => {
        if (onProgress) onProgress(new Float32Array(480).fill(0.2), 48000);
        return Promise.resolve({ samples: new Float32Array(0), sampleRate: 48000 });
    },
};

// Reach the private bits the same way the existing local test reaches _guardPlayback.
function inject(host: DiscordHost, mixer: FakeMixer): void {
    const h = host as unknown as { mixer: unknown; onnxTts: unknown };
    h.mixer = mixer;
    h.onnxTts = fakeTts;
}

function streamSpeak(host: DiscordHost): Promise<OpResult> {
    const h = host as unknown as {
        _streamSpeakText(text: string, meta: unknown, baked: unknown): Promise<OpResult>;
    };
    return h._streamSpeakText('hello', { reqId: 1, recvT: performance.now() }, null);
}

test('_streamSpeakText: closes the mixer voice on the happy path', async () => {
    const host = new DiscordHost();
    host.setConfig(cfg({}));
    const mixer = fakeMixer(/* throwOnAppend */ false);
    inject(host, mixer);

    const r = await streamSpeak(host);
    assert.equal(r.ok, true);
    assert.equal(mixer.opened, 1);
    assert.equal(mixer.closed, 1); // opened voices are always balanced by a close
});

test('_streamSpeakText: a throw while finalizing still closes the voice', async () => {
    const host = new DiscordHost();
    host.setConfig(cfg({}));
    const mixer = fakeMixer(/* throwOnAppend */ true);
    inject(host, mixer);

    await assert.rejects(streamSpeak(host), /append boom/);
    assert.equal(mixer.opened, 1);
    assert.equal(mixer.closed, 1); // the finally released the slot despite the throw
});
