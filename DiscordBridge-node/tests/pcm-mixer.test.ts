import { test } from 'node:test';
import { strict as assert } from 'node:assert';

import { PcmMixer } from '../src/pcm-mixer.js';

const CHUNK_BYTES = 3840; // 960 samples * 2 channels * 2 bytes

function constStereo(int16Value: number, frames: number): Buffer {
    const buf = Buffer.alloc(frames * 4);
    for (let i = 0; i < frames; i++) {
        buf.writeInt16LE(int16Value, i * 4);
        buf.writeInt16LE(int16Value, i * 4 + 2);
    }
    return buf;
}

function allSamplesEqual(buf: Buffer, expected: number): boolean {
    for (let i = 0; i < buf.length; i += 2) {
        if (buf.readInt16LE(i) !== expected) return false;
    }
    return true;
}

test('empty mixer emits one silence chunk', () => {
    const m = new PcmMixer();
    const chunk = m._mixOneChunk();
    assert.equal(chunk.length, CHUNK_BYTES);
    assert.ok(allSamplesEqual(chunk, 0));
});

test('single voice exactly one chunk long: output equals input', () => {
    const m = new PcmMixer();
    const voice = constStereo(1234, 960);
    m.addVoice(voice);
    const chunk = m._mixOneChunk();
    assert.equal(Buffer.compare(chunk, voice), 0);
    // Voice consumed; next chunk is silence.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
});

test('two voices sum sample-by-sample', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(100, 960));
    m.addVoice(constStereo(200, 960));
    const chunk = m._mixOneChunk();
    assert.ok(allSamplesEqual(chunk, 300));
});

test('addVoice with latency meta mixes identically (instrumentation is side-effect-only)', () => {
    const m = new PcmMixer();
    const voice = constStereo(4321, 960);
    // meta only drives the firstEmit log; it must not alter mixing output.
    m.addVoice(voice, { id: 7, enqueueT: 0 });
    const chunk = m._mixOneChunk();
    assert.equal(Buffer.compare(chunk, voice), 0);
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
});

test('positive saturation clips to 32767', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(30000, 960));
    m.addVoice(constStereo(10000, 960));
    const chunk = m._mixOneChunk();
    assert.ok(allSamplesEqual(chunk, 32767));
});

test('negative saturation clips to -32768', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(-30000, 960));
    m.addVoice(constStereo(-10000, 960));
    const chunk = m._mixOneChunk();
    assert.ok(allSamplesEqual(chunk, -32768));
});

test('voice spanning 1.5 chunks: second chunk is half mixed, half silent', () => {
    const m = new PcmMixer();
    // 1440 frames = 1.5 chunks of stereo s16le (1440 * 4 = 5760 bytes).
    m.addVoice(constStereo(500, 1440));

    const c1 = m._mixOneChunk();
    assert.ok(allSamplesEqual(c1, 500));

    const c2 = m._mixOneChunk();
    // First 480 frames (1920 bytes) carry the voice's tail at 500.
    for (let i = 0; i < 1920; i += 2) {
        assert.equal(c2.readInt16LE(i), 500, `c2 first half at byte ${i}`);
    }
    // Remaining 480 frames are silence.
    for (let i = 1920; i < CHUNK_BYTES; i += 2) {
        assert.equal(c2.readInt16LE(i), 0, `c2 second half at byte ${i}`);
    }

    // Voice fully consumed; chunk 3 is pure silence.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
});

test('clear() drops in-flight voices', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(7777, 960 * 4)); // 4 chunks long
    const c1 = m._mixOneChunk();
    assert.ok(allSamplesEqual(c1, 7777));
    m.clear();
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
});

test('independent per-voice cursors: longer voice survives shorter voice', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(100, 960 * 4)); // A: 4 chunks @ 100
    m.addVoice(constStereo(200, 960 * 2)); // B: 2 chunks @ 200

    // Chunks 1-2: both active → 300.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 300));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 300));

    // Chunks 3-4: only A remains → 100.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 100));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 100));

    // Both consumed.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
});

test('addVoice tolerates trailing odd byte by truncating', () => {
    const m = new PcmMixer();
    // 960 frames worth (3840 bytes) plus one stray byte.
    const stray = Buffer.concat([constStereo(1234, 960), Buffer.from([0xff])]);
    m.addVoice(stray);
    const chunk = m._mixOneChunk();
    // The aligned 3840 bytes mix as if the stray byte didn't exist.
    assert.ok(allSamplesEqual(chunk, 1234));
});

test('addVoice ignores buffer that has no aligned bytes', () => {
    const m = new PcmMixer();
    m.addVoice(Buffer.from([0xff])); // 1 byte → truncates to 0
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
});

test('voice cap: 65th voice causes one FIFO drop, 64 survive', () => {
    const m = new PcmMixer();
    // 64 voices fit; the 65th evicts the oldest. Use distinct sample values
    // so we can prove order if we want to extend later.
    for (let i = 0; i < 64; i++) m.addVoice(constStereo(i + 1, 1));
    assert.equal(m.voiceCount, 64);
    const r = m.addVoice(constStereo(99, 1));
    assert.deepEqual(r, { dropped: 1 });
    assert.equal(m.voiceCount, 64);
});

test('voice cap: addVoice returns {dropped:0} below the cap', () => {
    const m = new PcmMixer();
    const r = m.addVoice(constStereo(1, 100));
    assert.deepEqual(r, { dropped: 0 });
});

test('voice cap: FIFO eviction — oldest dropped, newest survives', () => {
    const m = new PcmMixer();
    // Add 64 voices each carrying a 1-frame buffer of value 1.
    for (let i = 0; i < 64; i++) m.addVoice(constStereo(1, 1));
    // The 65th carries a distinct value 1000. After eviction the queue holds
    // 63 voices @ value=1 and 1 voice @ value=1000 → first chunk sample[0] = 63 + 1000.
    m.addVoice(constStereo(1000, 1));
    assert.equal(m.voiceCount, 64);
    const chunk = m._mixOneChunk();
    // Each 1-frame voice contributes only to sample[0] (1 stereo frame = 2 samples).
    assert.equal(chunk.readInt16LE(0), 63 + 1000);
});

test('byte cap: large queued bytes evict oldest until under MAX_QUEUED_BYTES', () => {
    const m = new PcmMixer();
    // 5 buffers of 8 MiB each = 40 MiB > 32 MiB cap. Adding the 5th should
    // evict the 1st. We use real stereo s16 buffers (constStereo) so the
    // cap math is on real consumed bytes.
    const big = constStereo(1, 8 * 1024 * 1024 / 4); // 8 MiB worth of stereo s16
    assert.equal(big.length, 8 * 1024 * 1024);
    for (let i = 0; i < 4; i++) {
        const r = m.addVoice(big);
        assert.equal(r.dropped, 0, `voice ${i + 1} should fit`);
    }
    const r5 = m.addVoice(big);
    assert.equal(r5.dropped, 1, 'fifth 8 MiB voice should evict the first');
    assert.ok(m.queuedBytes <= 32 * 1024 * 1024);
});

test('byte cap: a single oversized voice is preserved (never the only voice)', () => {
    const m = new PcmMixer();
    const huge = constStereo(1, (40 * 1024 * 1024) / 4); // 40 MiB > cap
    const r = m.addVoice(huge);
    assert.deepEqual(r, { dropped: 0 });
    assert.equal(m.voiceCount, 1);
});

test('byte cap: queuedBytes decrements as chunks are consumed', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(1, 960)); // exactly one chunk (3840 bytes)
    assert.equal(m.queuedBytes, 3840);
    m._mixOneChunk();
    assert.equal(m.queuedBytes, 0);
});

test('clear() resets queuedBytes', () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(1, 1000));
    assert.ok(m.queuedBytes > 0);
    m.clear();
    assert.equal(m.queuedBytes, 0);
    assert.equal(m.voiceCount, 0);
});

// ----------------------------------------------------------------------------
// Open (streaming) voices: openVoice / appendToVoice / closeVoice
// ----------------------------------------------------------------------------

test('open voice survives while drained, plays appended audio, drops after close', () => {
    const m = new PcmMixer();
    const h = m.openVoice({ id: 1, enqueueT: 0 });
    // No audio yet: mixer emits silence but keeps the open voice alive.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
    assert.equal(m.voiceCount, 1);

    // Append one chunk worth; it plays.
    m.appendToVoice(h, constStereo(500, 960));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 500));
    // Drained but still open → silence, voice retained.
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
    assert.equal(m.voiceCount, 1);

    // More audio arrives after a drain gap; still plays on the same voice.
    m.appendToVoice(h, constStereo(700, 960));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 700));

    // Close: once drained it is compacted out.
    m.closeVoice(h);
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
    assert.equal(m.voiceCount, 0);
});

test('appendToVoice across a chunk boundary plays seamlessly (two halves == one full)', () => {
    // Append two 480-frame halves of a 960-frame voice; the first chunk must mix
    // exactly the same as a single 960-frame addVoice would.
    const m = new PcmMixer();
    const h = m.openVoice();
    m.appendToVoice(h, constStereo(333, 480));
    m.appendToVoice(h, constStereo(333, 480));
    const chunk = m._mixOneChunk();
    assert.equal(Buffer.compare(chunk, constStereo(333, 960)), 0);
});

test('appendToVoice mid-chunk (partial then top-up) fills the same chunk', () => {
    const m = new PcmMixer();
    const h = m.openVoice();
    m.appendToVoice(h, constStereo(111, 480)); // half a chunk
    m.appendToVoice(h, constStereo(111, 480)); // top up to a full chunk before _read
    const c = m._mixOneChunk();
    assert.ok(allSamplesEqual(c, 111));
});

test('two concurrent open voices mix (sum) and drain independently', () => {
    // Models overlapping callouts: two streaming voices open at once, fed on
    // interleaved schedules, must sum sample-by-sample and close independently.
    const m = new PcmMixer();
    const a = m.openVoice({ id: 1, enqueueT: 0 });
    const b = m.openVoice({ id: 2, enqueueT: 0 });
    assert.equal(m.voiceCount, 2);

    m.appendToVoice(a, constStereo(100, 960));
    m.appendToVoice(b, constStereo(200, 960));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 300)); // both contribute

    // A is starved this round but still open; B gets more → only B plays.
    m.appendToVoice(b, constStereo(200, 960));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 200));

    // A resumes; B starved → only A.
    m.appendToVoice(a, constStereo(100, 960));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 100));

    // Close A; B keeps playing its remaining audio independently.
    m.closeVoice(a);
    m.appendToVoice(b, constStereo(200, 960));
    assert.ok(allSamplesEqual(m._mixOneChunk(), 200));
    m.closeVoice(b);
    assert.ok(allSamplesEqual(m._mixOneChunk(), 0));
    assert.equal(m.voiceCount, 0);
});

test('open and one-shot voices coexist and sum', () => {
    // A streaming callout overlapping a fully-buffered SpeakFile/SpeakPcm voice.
    const m = new PcmMixer();
    const stream = m.openVoice({ id: 1, enqueueT: 0 });
    m.appendToVoice(stream, constStereo(100, 960));
    m.addVoice(constStereo(50, 960)); // a one-shot voice in parallel
    assert.ok(allSamplesEqual(m._mixOneChunk(), 150));
});

test('open voice is not evicted by a flood of one-shot voices', () => {
    const m = new PcmMixer();
    const h = m.openVoice({ id: 42, enqueueT: 0 });
    m.appendToVoice(h, constStereo(1000, 1)); // 1 stereo frame at sample[0]
    // Flood well past the 64-voice cap with one-shot voices.
    for (let i = 0; i < 200; i++) m.addVoice(constStereo(1, 1));
    assert.ok(m.voiceCount <= 64);
    // The open voice must still be present and contribute its 1000 to sample[0].
    const chunk = m._mixOneChunk();
    assert.ok(chunk.readInt16LE(0) >= 1000, `open voice survived: sample0=${chunk.readInt16LE(0)}`);
});

test('queuedBytes tracks appends and drains; never negative across close', () => {
    const m = new PcmMixer();
    const h = m.openVoice();
    m.appendToVoice(h, constStereo(1, 960)); // 3840 bytes
    assert.equal(m.queuedBytes, 3840);
    m.appendToVoice(h, constStereo(1, 960)); // +3840
    assert.equal(m.queuedBytes, 7680);
    m._mixOneChunk(); // consume one chunk
    assert.equal(m.queuedBytes, 3840);
    m.closeVoice(h);
    m._mixOneChunk(); // drain the rest
    assert.equal(m.queuedBytes, 0);
    assert.ok(m.queuedBytes >= 0);
});

test('appendToVoice on a closed/drained handle is a safe no-op', () => {
    const m = new PcmMixer();
    const h = m.openVoice();
    m.appendToVoice(h, constStereo(5, 960));
    m.closeVoice(h);
    m._mixOneChunk(); // drains + compacts the voice out
    assert.equal(m.voiceCount, 0);
    // Handle is stale now; appending must not throw or resurrect a voice.
    const r = m.appendToVoice(h, constStereo(9, 960));
    assert.deepEqual(r, { dropped: 0 });
    assert.equal(m.voiceCount, 0);
});

test('Readable plumbing: _read pushes one chunk per call', async () => {
    const m = new PcmMixer();
    m.addVoice(constStereo(42, 960 * 3)); // 3 chunks long
    // Drive the public Readable API. Wait for 'readable' so the internal
    // buffer has been primed.
    await new Promise<void>((resolve) => m.once('readable', () => resolve()));
    const chunk = m.read(CHUNK_BYTES) as Buffer;
    assert.equal(chunk.length, CHUNK_BYTES);
    assert.ok(allSamplesEqual(chunk, 42));
});
