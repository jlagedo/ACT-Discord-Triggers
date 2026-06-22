// Local sound-device output: plays the mixer's final 48 kHz / 16-bit / stereo
// audio on the host PC's default output device, the alternative to streaming it
// to a Discord voice channel. Used when outputMode === 'local'.
//
// The whole bridge pipeline (decode/synth -> resample -> fx -> normalize ->
// declick -> mix -> master limiter) is unchanged; this is just a second drain
// for the same PcmMixer. Where the Discord path lets prism-media's Opus encoder
// pull chunks off the mixer (a Readable), the local path pushes chunks into
// audify's RtAudio output queue and lets the device's frame-finished callback
// drive the 48 kHz clock — one mixer chunk per finished frame.
//
// audify (RtAudio) is loaded lazily, mirroring tts.ts: the bridge starts and
// prints BRIDGE_READY even if the addon is missing/broken — only local playback
// fails then, with a logged error, and the Discord path is untouched.

import * as log from './file-log.js';
import { requireExternal } from './native-require.js';
import type { PcmMixer } from './pcm-mixer.js';

// Mirror the mixer's chunk geometry exactly: 960 samples/channel @ 48 kHz is one
// 20 ms frame, and _mixOneChunk() returns 960 * 2ch * 2 bytes = 3840 s16le bytes,
// which is precisely what RtAudio.write expects for frameSize=960 stereo s16.
const SAMPLE_RATE = 48000;
const CHANNELS = 2;
const FRAME_SAMPLES = 960;

// Frames pre-queued before steady-state so playback starts without an immediate
// underrun and short event-loop stalls (GC, a synth burst) don't gap the audio.
// 4 frames ~= 80 ms of latency, imperceptible for callouts.
const PRIME_FRAMES = 4;

// Minimal typed surface over audify's RtAudio — the package ships full
// declarations, but we lazy-require (so a missing addon can't break startup),
// which means the static import types aren't in scope. Only the members used
// here are modelled; values like the format/api enums are read off the loaded
// module so no magic numbers are hard-coded.
interface RtAudioStreamParameters { deviceId?: number; nChannels: number; firstChannel?: number }
interface RtAudioInstance {
    getDefaultOutputDevice(): number;
    openStream(
        outputParameters: RtAudioStreamParameters | null,
        inputParameters: RtAudioStreamParameters | null,
        format: number,
        sampleRate: number,
        frameSize: number,
        streamName: string,
        inputCallback: ((pcm: Buffer) => void) | null,
        frameOutputCallback: (() => void) | null,
    ): number;
    start(): void;
    stop(): void;
    closeStream(): void;
    isStreamOpen(): boolean;
    write(pcm: Buffer): void;
}
interface AudifyModule {
    RtAudio: new (api?: number) => RtAudioInstance;
    RtAudioFormat: { RTAUDIO_SINT16: number };
    RtAudioApi: { WINDOWS_WASAPI: number };
}

let audifyMod: AudifyModule | null = null;

// Lazy-loaded so a missing/broken addon can't break bridge startup — see
// native-require.ts for why it's required this way.
function loadAudify(): AudifyModule {
    if (!audifyMod) audifyMod = requireExternal<AudifyModule>('audify');
    return audifyMod;
}

// The slice of LocalOutput that discord-host depends on: bring the device up and
// tear it down. discord-host takes a factory for it so the outputMode start/stop
// transition logic is unit-testable without the audify native addon.
export interface LocalSink {
    start(): void;
    stop(): void;
}

export class LocalOutput implements LocalSink {
    private rt: RtAudioInstance | null = null;

    constructor(private readonly mixer: PcmMixer) {}

    // Open the default output device and start pulling from the mixer. Throws if
    // the addon is missing or no device is available — the caller (discord-host)
    // catches and reports, leaving the bridge otherwise healthy.
    start(): void {
        if (this.rt) return;
        const audify = loadAudify();
        // Force WASAPI: the default (UNSPECIFIED) API picks ASIO on Windows, which
        // probes every installed ASIO driver and floods stderr. WASAPI is the modern
        // shared-mode path and resamples to the device's mix format internally, so a
        // device that isn't natively 48 kHz still plays.
        const rt = new audify.RtAudio(audify.RtAudioApi.WINDOWS_WASAPI);
        const deviceId = rt.getDefaultOutputDevice();
        rt.openStream(
            { deviceId, nChannels: CHANNELS, firstChannel: 0 },
            null,
            audify.RtAudioFormat.RTAUDIO_SINT16,
            SAMPLE_RATE,
            FRAME_SAMPLES,
            'ACT Discord Triggers',
            null,                 // output-only: no input callback
            () => this._feed(),   // each finished frame asks for the next one
        );
        this.rt = rt;
        // Prime the queue before starting the device so it begins from a small
        // filled buffer instead of an immediate underrun; steady-state is then one
        // write per finished frame.
        for (let i = 0; i < PRIME_FRAMES; i++) this._feed();
        rt.start();
        log.info(`local output started (WASAPI, device=${deviceId})`);
    }

    // Stop the device and release it. Idempotent.
    stop(): void {
        const rt = this.rt;
        this.rt = null;
        if (!rt) return;
        try { rt.stop(); } catch (e) { log.warn('local output stop failed: ' + log.errMsg(e)); }
        try { if (rt.isStreamOpen()) rt.closeStream(); } catch (e) { log.warn('local output close failed: ' + log.errMsg(e)); }
        log.info('local output stopped');
    }

    // Queue exactly one mixer chunk (one 20 ms frame). The mixer always returns a
    // full chunk — silence when idle — so the device never starves while we're up.
    private _feed(): void {
        const rt = this.rt;
        if (!rt) return;
        try {
            rt.write(this.mixer._mixOneChunk());
        } catch (e) {
            log.error('local output write failed', e);
        }
    }
}
