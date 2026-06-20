// Minimal canonical WAV (PCM integer) writer. The bridge's audio is 16-bit
// signed little-endian, so only that case is modelled. Used by the diagnostic
// audio sink (audio-sink.ts) and by the synthesis tests/probe to dump a clip a
// human can listen to. Not on any hot path — keep it small and dependency-free.

import { writeFileSync } from 'node:fs';

export interface WavFormat {
    sampleRate: number;
    channels: number;
}

// Build the 44-byte canonical PCM WAV header for `dataBytes` of 16-bit samples.
export function wavHeader16(dataBytes: number, fmt: WavFormat): Buffer {
    const bitsPerSample = 16;
    const bytesPerSample = bitsPerSample / 8;
    const blockAlign = fmt.channels * bytesPerSample;
    const byteRate = fmt.sampleRate * blockAlign;
    const header = Buffer.alloc(44);
    header.write('RIFF', 0, 'ascii');
    header.writeUInt32LE(36 + dataBytes, 4); // RIFF chunk size = 36 + data
    header.write('WAVE', 8, 'ascii');
    header.write('fmt ', 12, 'ascii');
    header.writeUInt32LE(16, 16);            // fmt chunk size (PCM)
    header.writeUInt16LE(1, 20);             // audioFormat = 1 (PCM integer)
    header.writeUInt16LE(fmt.channels, 22);
    header.writeUInt32LE(fmt.sampleRate, 24);
    header.writeUInt32LE(byteRate, 28);
    header.writeUInt16LE(blockAlign, 32);
    header.writeUInt16LE(bitsPerSample, 34);
    header.write('data', 36, 'ascii');
    header.writeUInt32LE(dataBytes, 40);     // data chunk size
    return header;
}

// Wrap a 16-bit PCM payload in a WAV container and write it to `path`.
export function writeWav16(path: string, pcm: Buffer, fmt: WavFormat): void {
    writeFileSync(path, Buffer.concat([wavHeader16(pcm.length, fmt), pcm]));
}
