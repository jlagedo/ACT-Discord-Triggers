import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import {
    Op, PROTOCOL_VERSION, MAX_FRAME_BYTES, BINARY_SPEAK_PCM_HEADER_BYTES,
    DEFAULT_CONFIG_VIEW,
} from '../src/protocol.js';

test('PROTOCOL_VERSION is a positive integer', () => {
    assert.equal(typeof PROTOCOL_VERSION, 'number');
    assert.ok(Number.isInteger(PROTOCOL_VERSION));
    assert.ok(PROTOCOL_VERSION > 0);
});

test('PROTOCOL_VERSION matches the C# side (bump both together)', () => {
    assert.equal(PROTOCOL_VERSION, 6);
});

test('SpeakText is a command op (ONNX neural TTS; only text crosses the wire)', () => {
    assert.equal(Op.SpeakText, 'SpeakText');
});

test('binary SpeakPcm header is 11 bytes (matches C# PipeClient, no flags byte)', () => {
    assert.equal(BINARY_SPEAK_PCM_HEADER_BYTES, 11);
});

test('MAX_FRAME_BYTES is 64 MiB (matches C# Protocol.cs)', () => {
    assert.equal(MAX_FRAME_BYTES, 64 * 1024 * 1024);
});

test('all Op values are distinct strings', () => {
    const values = Object.values(Op);
    for (const v of values) assert.equal(typeof v, 'string');
    assert.equal(new Set(values).size, values.length);
});

test('there is exactly one response op and it is the single Result envelope', () => {
    // Every command/config reply is `Result`, correlated by reqId. The only op
    // that ends with "Result" must be `Result` itself — catches an accidental
    // reintroduction of per-op *Result names.
    assert.equal(Op.Result, 'Result');
    for (const v of Object.values(Op)) {
        if (v.endsWith('Result')) assert.equal(v, 'Result', `unexpected *Result op: ${v}`);
    }
});

test('DEFAULT_CONFIG_VIEW carries the master limiter defaults (match PluginSettings)', () => {
    // These must mirror the C# PluginSettings defaults: limiter on, -1 dBTP tier.
    assert.equal(DEFAULT_CONFIG_VIEW.limiterEnabled, true);
    assert.equal(DEFAULT_CONFIG_VIEW.limiterCeilingIndex, 1);
});

test('DEFAULT_CONFIG_VIEW defaults outputMode to bot (match PluginSettings)', () => {
    // Older saved settings carry no outputMode; the bridge must default to the
    // Discord-bot path so they behave exactly as before.
    assert.equal(DEFAULT_CONFIG_VIEW.outputMode, 'bot');
});

test('DEFAULT_CONFIG_VIEW defaults localOutputVolume to 100 (match PluginSettings)', () => {
    // Additive field: older configs carry no localOutputVolume, so the bridge must
    // default to 100 % (unity gain) — local playback unchanged from full scale.
    assert.equal(DEFAULT_CONFIG_VIEW.localOutputVolume, 100);
});

test('config is a single op, not per-knob setters', () => {
    assert.equal(Op.SetConfig, 'SetConfig');
    const values = new Set<string>(Object.values(Op));
    for (const removed of ['SetGame', 'SetNormalization', 'SetAudioQuality', 'Init', 'Deinit']) {
        assert.ok(!values.has(removed), `${removed} should no longer exist`);
    }
});

test('notification ops have no Result suffix', () => {
    for (const notifOp of [Op.BotReady, Op.Log, Op.Disconnected]) {
        assert.ok(!notifOp.endsWith('Result'), `${notifOp} should not end with Result`);
    }
});

test('Op constants match their key names verbatim', () => {
    // Catches typos like Op.Result = 'result'. C# side uses PascalCase string
    // constants too, so case mismatches break the wire silently.
    for (const [key, value] of Object.entries(Op)) {
        assert.equal(value, key, `Op.${key} value drift: ${value}`);
    }
});
