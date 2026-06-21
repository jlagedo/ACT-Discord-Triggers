import { test, before } from 'node:test';
import { strict as assert } from 'node:assert';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';

import { decodeFileToFinalPcm } from '../src/discord-host.js';
import { initResampler } from '../src/resample.js';

// decodeFileToFinalPcm resamples to 48k via the r8brain WASM module; load it
// first (the real bridge does this before BRIDGE_READY).
before(async () => { await initResampler(); });

// Real public-domain / CC0 clips (see fixtures/audio/README.md). They cover the
// formats Triggernometry hands to PlaySoundMethod plus edge cases (8-bit PCM, a
// truncated OGG). Output is always interleaved float32 stereo @48k regardless of
// the source rate/channels/bit-depth, so length (in samples) is a multiple of 2
// and the duration is preserved across decode + resample.
const FIX = join(dirname(fileURLToPath(import.meta.url)), 'fixtures', 'audio');

// expSeconds from the README; tolerance absorbs lossy-codec encoder padding.
const cases: Array<{ file: string; expSeconds: number; note: string }> = [
    { file: 'mp3-44100-mono.mp3', expSeconds: 4.99, note: '44.1k mono -> resample + upmix (QA target)' },
    { file: 'mp3-44100-stereo.mp3', expSeconds: 5.03, note: '44.1k stereo -> non-integer resample' },
    { file: 'vorbis-44100-mono.ogg', expSeconds: 4.99, note: 'ogg/vorbis 44.1k mono' },
    { file: 'vorbis-48000-stereo-bell.ogg', expSeconds: 1.30, note: 'ogg/vorbis 48k stereo (no resample)' },
    { file: 'vorbis-48000-stereo-explosion.ogg', expSeconds: 0.56, note: 'short 48k stereo SFX' },
    { file: 'vorbis-48000-stereo-glass.ogg', expSeconds: 0.39, note: 'short 48k stereo SFX' },
    { file: 'flac-44100-mono.flac', expSeconds: 5.00, note: 'flac 44.1k mono' },
    { file: 'wav-44100-mono-s16.wav', expSeconds: 3.00, note: 'wav 16-bit (common case)' },
    { file: 'wav-44100-mono-u8.wav', expSeconds: 3.07, note: 'wav 8-bit unsigned (unusual case)' },
];

for (const { file, expSeconds, note } of cases) {
    test(`decode fixture: ${file} (${note})`, async () => {
        const pcm = await decodeFileToFinalPcm(join(FIX, file));
        assert.ok(pcm.length > 0, 'non-empty');
        assert.equal(pcm.length % 2, 0, 'interleaved float32 stereo (2 samples/frame)');
        const seconds = pcm.length / 2 / 48000;
        const lo = expSeconds * 0.95 - 0.06;
        const hi = expSeconds * 1.05 + 0.06;
        assert.ok(seconds > lo && seconds < hi, `~${expSeconds}s expected, got ${seconds.toFixed(3)}s`);
    });
}

test('decode: truncated/corrupt OGG is rejected, not crashed', async () => {
    // First 2 KiB of a valid OGG — looks like OGG but ends mid-stream.
    await assert.rejects(
        decodeFileToFinalPcm(join(FIX, 'corrupt-truncated.ogg')),
        /Failed to decode audio|Decoded audio is empty/,
    );
});

test('decode: unrecognized data is rejected, not crashed', async () => {
    const p = join(tmpdir(), `act-decode-test-${process.pid}.dat`);
    await writeFile(p, Buffer.from('this is not audio at all', 'utf8'));
    try {
        // audio-decode's audio-type detection throws "Unknown audio format".
        await assert.rejects(decodeFileToFinalPcm(p), /Failed to decode audio/);
    } finally {
        await rm(p, { force: true });
    }
});

test('decode: missing file throws Cannot read file', async () => {
    await assert.rejects(
        decodeFileToFinalPcm(join(FIX, 'does-not-exist.wav')),
        /Cannot read file/,
    );
});
