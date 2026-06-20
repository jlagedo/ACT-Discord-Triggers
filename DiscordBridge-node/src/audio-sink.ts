// Diagnostic audio sink: writes every clip the bridge plays to a WAV file.
//
// Enabled by the ACT_DT_AUDIO_SINK env var (a directory). It serves two purposes:
//   1. Diagnostics — capture exactly what the bot is sending to Discord as a file
//      a human can listen to, without recording the voice channel.
//   2. Test automation — when set, the host also lets SpeakText/SpeakFile/SpeakPcm
//      run *without* a joined voice channel (see DiscordHost._guardPlayback), so the
//      whole synthesis → fx → normalize → declick pipeline is observable offline.
//
// Off (null) unless the env var is set, so production behaviour is unchanged and
// the build self-test is unaffected. The captured PCM is the bridge's final
// 48 kHz / 16-bit / stereo buffer — the exact bytes that would feed the mixer.

import { existsSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import * as log from './file-log.js';
import { writeWav16 } from './wav-write.js';

const TARGET_SAMPLE_RATE = 48000;
const TARGET_CHANNELS = 2;

export interface AudioSink {
    // Write one prepared clip; returns the file path written (for logging/tests).
    write(label: string, pcm: Buffer): string;
    readonly dir: string;
}

class WavFileSink implements AudioSink {
    private seq = 0;
    constructor(public readonly dir: string) {}

    write(label: string, pcm: Buffer): string {
        this.seq += 1;
        const safe = label.replace(/[^A-Za-z0-9._-]/g, '_');
        const name = `${String(this.seq).padStart(4, '0')}-${safe}.wav`;
        const path = join(this.dir, name);
        writeWav16(path, pcm, { sampleRate: TARGET_SAMPLE_RATE, channels: TARGET_CHANNELS });
        return path;
    }
}

// Build a sink from ACT_DT_AUDIO_SINK, or null when the var is unset/blank.
// Creates the directory if missing; on any failure logs and returns null so a
// bad path never takes the bridge down.
export function createSinkFromEnv(): AudioSink | null {
    const dir = (process.env['ACT_DT_AUDIO_SINK'] ?? '').trim();
    if (!dir) return null;
    try {
        if (!existsSync(dir)) mkdirSync(dir, { recursive: true });
        log.info(`audio sink enabled: clips written to ${dir}`);
        return new WavFileSink(dir);
    } catch (e) {
        log.error(`audio sink disabled: cannot use '${dir}'`, e);
        return null;
    }
}
