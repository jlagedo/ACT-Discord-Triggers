import { test } from 'node:test';
import { strict as assert } from 'node:assert';
import { errMsg, redact, redactSecret } from '../src/file-log.js';

test('errMsg(Error) returns the message', () => {
    assert.equal(errMsg(new Error('boom')), 'boom');
});

test('errMsg(Error) with empty message falls back to String(e)', () => {
    const e = new Error('');
    // Error('').toString() => 'Error', so the helper returns 'Error'.
    assert.equal(errMsg(e), 'Error');
});

test('errMsg(string) returns the string verbatim', () => {
    assert.equal(errMsg('plain string'), 'plain string');
});

test('errMsg(undefined) returns "undefined"', () => {
    assert.equal(errMsg(undefined), 'undefined');
});

test('errMsg(null) returns "null"', () => {
    assert.equal(errMsg(null), 'null');
});

test('errMsg(number) coerces via String()', () => {
    assert.equal(errMsg(42), '42');
});

test('errMsg(plain object) returns a non-empty string', () => {
    const out = errMsg({ foo: 1 });
    assert.equal(typeof out, 'string');
    assert.ok(out.length > 0);
});

test('errMsg(Error subclass) reads the message', () => {
    class MyError extends Error {
        constructor() { super('subclass msg'); this.name = 'MyError'; }
    }
    assert.equal(errMsg(new MyError()), 'subclass msg');
});

// --- Secret redaction --------------------------------------------------------

// A fake token-shaped string (id . timestamp . hmac) that matches TOKEN_SHAPE but
// is deliberately NOT a real Discord token — the hyphenated first segment can't
// decode to a snowflake, so it won't trip secret scanners.
const TOKEN_SHAPED = 'not-a-real-token-only-shaped-like-one.abcdef.notarealhmacnotarealhmacnotareal';

test('redact: a token-shaped string is masked even without registration', () => {
    const out = redact(`connect failed: ${TOKEN_SHAPED} rejected`);
    assert.ok(!out.includes(TOKEN_SHAPED), `token leaked: ${out}`);
    assert.match(out, /connect failed: \*\*\* rejected/);
});

test('redact: registered exact secret is masked at every occurrence', () => {
    // An arbitrary token that is NOT token-shaped, so only registration can catch it.
    const tok = 'plain-but-secret-abcdef123456';
    assert.equal(redact(tok), tok, 'not masked before registration');
    redactSecret(tok);
    assert.equal(redact(`a ${tok} b ${tok} c`), 'a *** b *** c');
});

test('redactSecret: ignores blank/too-short values (never mass-replaces)', () => {
    redactSecret('');
    redactSecret('   ');
    redactSecret('short'); // < 8 chars
    // None registered => an unrelated line is untouched (and not blown away by '').
    assert.equal(redact('short message with short word'), 'short message with short word');
});

test('redact: leaves ordinary diagnostics untouched (no false positives)', () => {
    const line = "SpeakText reqId=7 voice=piper sid=0 lang='' dir=C:\\models\\vits-piper-en_US-amy-medium.onnx";
    assert.equal(redact(line), line);
});
