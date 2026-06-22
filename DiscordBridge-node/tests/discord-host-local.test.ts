import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { DiscordHost } from '../src/discord-host.js';
import type { LocalSink } from '../src/local-output.js';
import type { OpResult } from '../src/pipe-server.js';
import { DEFAULT_CONFIG_VIEW, type BridgeConfigView } from '../src/protocol.js';

// Exercises the outputMode start/stop transition dispatch in DiscordHost.setConfig
// and the local branch of _guardPlayback, with a fake LocalSink so the real audify
// native addon and a real sound device are never needed.

function cfg(overrides: Partial<BridgeConfigView>): BridgeConfigView {
    return { ...DEFAULT_CONFIG_VIEW, ...overrides };
}

// Records start/stop. `throwOnStart` simulates a device that won't open (missing
// addon / no output device) — the real LocalOutput.start() throws there too.
class FakeLocalSink implements LocalSink {
    started = 0;
    stopped = 0;
    constructor(private readonly throwOnStart = false) {}
    start(): void {
        if (this.throwOnStart) throw new Error('no output device');
        this.started++;
    }
    stop(): void { this.stopped++; }
}

// A host whose local sink factory records every sink it builds, so a test can
// assert how many were started/stopped across config transitions.
function hostWithSinks(throwOnStart = false): { host: DiscordHost; sinks: FakeLocalSink[] } {
    const sinks: FakeLocalSink[] = [];
    const host = new DiscordHost(() => {
        const s = new FakeLocalSink(throwOnStart);
        sinks.push(s);
        return s;
    });
    return { host, sinks };
}

// _guardPlayback is private; reach it through a narrow cast (the same shape the
// speak* paths call internally).
function guard(host: DiscordHost): OpResult {
    return (host as unknown as { _guardPlayback(): OpResult })._guardPlayback();
}

test('setConfig: bot->local opens the device once and reports it active', () => {
    const { host, sinks } = hostWithSinks();
    host.setConfig(cfg({ outputMode: 'local' }));
    assert.equal(sinks.length, 1);
    assert.equal(sinks[0]!.started, 1);
    assert.equal(host.isLocalOutputActive(), true);
});

test('setConfig: a second local config does not restart the device', () => {
    const { host, sinks } = hostWithSinks();
    host.setConfig(cfg({ outputMode: 'local' }));
    host.setConfig(cfg({ outputMode: 'local', botStatus: 'changed' }));
    assert.equal(sinks.length, 1);       // no second sink built
    assert.equal(sinks[0]!.started, 1);  // and the first never restarted
    assert.equal(host.isLocalOutputActive(), true);
});

test('setConfig: local->bot tears the device down', () => {
    const { host, sinks } = hostWithSinks();
    host.setConfig(cfg({ outputMode: 'local' }));
    host.setConfig(cfg({ outputMode: 'bot' }));
    assert.equal(sinks[0]!.stopped, 1);
    assert.equal(host.isLocalOutputActive(), false);
});

test('setConfig: a device that fails to open leaves local output inactive', () => {
    const { host } = hostWithSinks(/* throwOnStart */ true);
    host.setConfig(cfg({ outputMode: 'local' }));
    assert.equal(host.isLocalOutputActive(), false);
});

test('_guardPlayback: accepts playback when local output is live', () => {
    const { host } = hostWithSinks();
    host.setConfig(cfg({ outputMode: 'local' }));
    assert.deepEqual(guard(host), { ok: true, error: '' });
});

test('_guardPlayback: local mode with no device gives a clear local error', () => {
    const { host } = hostWithSinks(/* throwOnStart */ true);
    host.setConfig(cfg({ outputMode: 'local' }));
    const r = guard(host);
    assert.equal(r.ok, false);
    assert.match(r.error, /Local audio output is not running/);
});

test('_guardPlayback: bot mode while disconnected reports not-connected', () => {
    const { host } = hostWithSinks();
    host.setConfig(cfg({ outputMode: 'bot' }));
    const r = guard(host);
    assert.equal(r.ok, false);
    assert.match(r.error, /Not connected to a voice channel/);
});
