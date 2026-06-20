import { stat, readFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import {
    Client,
    GatewayIntentBits,
    ActivityType,
    ChannelType,
    type Guild,
} from 'discord.js';
import {
    joinVoiceChannel,
    createAudioPlayer,
    createAudioResource,
    StreamType,
    VoiceConnectionStatus,
    getVoiceConnection,
    entersState,
    type VoiceConnection,
    type AudioPlayer,
    type AudioResource,
} from '@discordjs/voice';
import * as log from './file-log.js';
import decode from 'audio-decode';
import { planarFloatToInterleavedInt16Stereo_PHASE1_SHIM } from './audio-decode.js';
import { applyRandomEffect } from './effects.js';
import { normalizePcm16 } from './normalize.js';
import { declick } from './declick.js';
import { DEFAULT_AUDIO_BITRATE, clampBitrate } from './audio-quality.js';
import { PcmMixer } from './pcm-mixer.js';
import type { Host, Notifier, OpResult, SpeakMeta } from './pipe-server.js';
import { DEFAULT_CONFIG_VIEW, type BridgeConfigView, type LogLevel } from './protocol.js';
import { WavCache } from './wav-cache.js';
import { OnnxTts, parseTtsParams } from './tts.js';
import { createSinkFromEnv, type AudioSink } from './audio-sink.js';

const TARGET_SAMPLE_RATE = 48000;
const TARGET_BITS = 16;

// Maps the plugin's audio-quality tier (config.audioQualityIndex) to an Opus
// bitrate. Owned here so retuning the tiers never touches the wire contract.
const AUDIO_QUALITY_BITRATES = [48000, 96000, 128000];

// Bound peak memory before decode: a compressed file expands roughly ~10x once
// turned into float PCM, so reject pathological input sizes up front.
const MAX_DECODE_INPUT_BYTES = 64 * 1024 * 1024;

// Linear-interpolation sample rate conversion for 16-bit signed LE stereo.
// Quality is fine for short trigger sounds going through Opus at 48k; do not
// reuse this for music/long-form audio without swapping to a polyphase filter.
// Exported for unit tests.
export function resampleStereo16(pcm: Buffer, srcRate: number, dstRate: number): Buffer {
    if (srcRate === dstRate) return pcm;
    const srcFrames = pcm.length >>> 2;
    const ratio = dstRate / srcRate;
    const dstFrames = Math.max(1, Math.floor(srcFrames * ratio));
    const out = Buffer.alloc(dstFrames * 4);
    const lastSrc = srcFrames - 1;
    for (let i = 0; i < dstFrames; i++) {
        const srcPos = i / ratio;
        const srcIdx = Math.floor(srcPos);
        const nextIdx = srcIdx < lastSrc ? srcIdx + 1 : lastSrc;
        const frac = srcPos - srcIdx;
        const l1 = pcm.readInt16LE(srcIdx * 4);
        const l2 = pcm.readInt16LE(nextIdx * 4);
        const r1 = pcm.readInt16LE(srcIdx * 4 + 2);
        const r2 = pcm.readInt16LE(nextIdx * 4 + 2);
        out.writeInt16LE(Math.round(l1 + (l2 - l1) * frac), i * 4);
        out.writeInt16LE(Math.round(r1 + (r2 - r1) * frac), i * 4 + 2);
    }
    return out;
}

// Read + decode a complete audio file (wav/mp3/ogg/flac) to the bridge's
// 48k / 16-bit / stereo interleaved PCM. Throws with a user-facing message on
// unreadable / unrecognized / undecodable input. Factored out of speakFile so
// fixture tests can exercise decode without a Discord connection, and so Phase
// 3 (float32 end-to-end) has a single clean swap point.
export async function decodeFileToFinalPcm(path: string): Promise<Buffer> {
    let input: Buffer;
    try {
        input = await readFile(path);
    } catch (e) {
        throw new Error(`Cannot read file: ${log.errMsg(e)}`, { cause: e });
    }

    // Self-enforce the memory bound here, not just in speakFile: this function is
    // exported (fixture tests, the Phase 3 swap point) and decode expands input
    // ~10x into float PCM, so any caller must be protected from a pathological
    // file. speakFile additionally checks the stat size to fail before reading.
    if (input.length > MAX_DECODE_INPUT_BYTES) {
        throw new Error(`Audio file too large: ${input.length} bytes (max ${MAX_DECODE_INPUT_BYTES})`);
    }

    // audio-decode auto-detects the container and decodes to planar Float32;
    // it throws on an unknown/unsupported format or a corrupt stream.
    let channelData: Float32Array[];
    let sampleRate: number;
    try {
        ({ channelData, sampleRate } = await decode(input));
    } catch (e) {
        throw new Error(`Failed to decode audio: ${log.errMsg(e)}`, { cause: e });
    }

    // A corrupt/truncated-but-detected stream comes back empty (audio-decode
    // returns channelData:[] , sampleRate:0) rather than throwing — treat that
    // as an empty decode before validating the rate.
    const stereoSrc = planarFloatToInterleavedInt16Stereo_PHASE1_SHIM(channelData);
    if (stereoSrc.length === 0) {
        throw new Error('Decoded audio is empty');
    }
    if (!Number.isFinite(sampleRate) || sampleRate <= 0) {
        throw new Error(`Decoded audio has an invalid sample rate (${sampleRate} Hz)`);
    }

    const finalPcm = resampleStereo16(stereoSrc, sampleRate, TARGET_SAMPLE_RATE);
    log.info(`SpeakFile decoded: srcRate=${sampleRate} ch=${channelData.length} outBytes=${finalPcm.length}`);
    return finalPcm;
}

// ONNX models output Float32 mono. Convert to the bridge's 16-bit signed LE stereo
// (duplicating the channel) at the model's native rate; the caller resamples to
// 48 kHz with resampleStereo16. Exported for unit tests.
export function monoFloat32ToStereoInt16(mono: Float32Array): Buffer {
    const out = Buffer.alloc(mono.length * 4); // 2 channels * 2 bytes
    for (let i = 0; i < mono.length; i++) {
        let s = mono[i]!;
        if (s > 1) s = 1; else if (s < -1) s = -1;
        const v = Math.round(s < 0 ? s * 0x8000 : s * 0x7fff);
        out.writeInt16LE(v, i * 4);
        out.writeInt16LE(v, i * 4 + 2);
    }
    return out;
}

export class DiscordHost implements Host {
    private client: Client | null = null;
    private notify: Notifier | null = null;
    private connection: VoiceConnection | null = null;
    private player: AudioPlayer | null = null;
    private mixer: PcmMixer | null = null;
    private currentGuildId: string | null = null;
    private pingTimer: NodeJS.Timeout | null = null;
    private readonly wavCache = new WavCache();
    private readonly onnxTts = new OnnxTts();
    // Diagnostic WAV sink (ACT_DT_AUDIO_SINK). null in normal operation; when set
    // it captures every played clip to a file and unlocks offline capture mode
    // (playback without a joined channel) — see _guardPlayback / _enqueue.
    private readonly sink: AudioSink | null = createSinkFromEnv();

    // The whole plugin config, pushed via SetConfig. Defaults apply until the
    // first config frame lands. The bridge owns all interpretation: it rolls the
    // fx dice (randomFx/fxChance), negates normalizeTarget to dBFS, and maps
    // audioQualityIndex to an Opus bitrate.
    private config: BridgeConfigView = DEFAULT_CONFIG_VIEW;

    // Live Opus encoder from the current resource (StreamType.Raw). Bitrate is an
    // encoder CTL, so we hold it and apply setBitrate on join and on every config
    // change.
    private encoder: AudioResource['encoder'] = undefined;

    setNotifier(fn: Notifier): void { this.notify = fn; }

    setConfig(config: BridgeConfigView, ttsParams?: Record<string, string>): void {
        const prev = this.config;
        this.config = config;
        // Register the token so it's masked everywhere it could otherwise surface
        // (a later login/REST error, an uncaught stack). Must happen before connect.
        log.redactSecret(config.botToken);
        log.info(`setConfig: status='${config.botStatus}' fx=${config.randomFx}/${config.fxChance} `
            + `normalize=${config.normalize}/${config.normalizeTarget} audioQ=${config.audioQualityIndex}`);
        // Presence and bitrate are stateful on Discord's side (a gateway presence
        // update and an Opus encoder CTL). The plugin pushes the whole config on
        // every UI change, so only re-apply each when its field actually changed —
        // otherwise moving e.g. the FX slider would spam needless setActivity calls.
        // fx/normalize are read per-clip in _enqueue, so there's nothing to apply
        // eagerly for them.
        if (config.botStatus !== prev.botStatus) this._applyStatus();
        if (config.audioQualityIndex !== prev.audioQualityIndex) this._applyBitrate();
        this._applyOnnxVoice(ttsParams);
    }

    // Apply the ONNX synth descriptor from SetConfig's ttsParams. Validation
    // failures (missing model files) are reported to the user and leave the bridge
    // with no ready voice; a SpeakText then logs + skips rather than crashing.
    private _applyOnnxVoice(ttsParams?: Record<string, string>): void {
        const desc = parseTtsParams(ttsParams);
        const r = this.onnxTts.configure(desc);
        if (!r.ok) {
            log.warn(`ONNX voice unavailable: ${r.error}`);
            this._sendLog('Warn', `ONNX voice unavailable: ${r.error}`);
        } else if (desc) {
            log.info(`ONNX voice set: ${this.onnxTts.describe()} dir=${desc.modelDir}`);
        }
    }

    // Bitrate derived from the current audio-quality tier, clamped to prism's range.
    private _currentBitrate(): number {
        const b = AUDIO_QUALITY_BITRATES[this.config.audioQualityIndex] ?? DEFAULT_AUDIO_BITRATE;
        return clampBitrate(b);
    }

    // Push the current bitrate onto the live Opus encoder. No-op when not
    // connected or when the resource has no encoder (e.g. a non-Raw input).
    private _applyBitrate(): void {
        if (!this.encoder) return;
        const bitrate = this._currentBitrate();
        try {
            this.encoder.setBitrate(bitrate);
            log.info(`opus setBitrate=${bitrate}`);
        } catch (e) {
            log.error('opus setBitrate failed', e);
        }
    }

    async connect(): Promise<OpResult> {
        if (this.client) {
            log.info('connect: client already created, returning ok');
            return { ok: true, error: '' };
        }
        try {
            log.info('connect: creating Client');
            this.client = new Client({
                intents: [GatewayIntentBits.Guilds, GatewayIntentBits.GuildVoiceStates],
            });

            this.client.on('error', (err: Error) => {
                log.error('client error', err);
                this._sendLog('Error', `client error: ${err.message}`);
            });

            this.client.on('warn', (msg: string) => {
                log.warn('client warn: ' + msg);
                this._sendLog('Warn', msg);
            });

            this.client.on('shardDisconnect', (event: { code?: number }, shardId: number) => {
                const reason = `shard ${shardId} disconnected (code ${event?.code ?? '?'})`;
                log.info(reason);
                if (this.notify) this.notify({ op: 'Disconnected', reason });
            });

            this.client.once('clientReady', (client: Client<true>) => {
                this._applyStatus();
                if (this.notify) this.notify({ op: 'BotReady' });
                log.info(`clientReady: logged in as ${client.user.tag}`);
            });

            log.info('connect: login starting');
            await this.client.login(this.config.botToken);
            log.info('connect: login ok');
            return { ok: true, error: '' };
        } catch (e) {
            log.error('connect failed', e);
            try { await this.client?.destroy(); } catch { /* ignore */ }
            this.client = null;
            return { ok: false, error: log.errMsg(e) };
        }
    }

    async disconnect(): Promise<void> {
        log.info('disconnect');
        try { await this.leaveChannel(); } catch { /* ignore */ }
        try { await this.client?.destroy(); } catch { /* ignore */ }
        this.client = null;
    }

    isConnected(): boolean {
        try { return this.client?.isReady() ?? false; } catch { return false; }
    }

    getServers(): string[] {
        if (!this.client) return [];
        return [...this.client.guilds.cache.values()].map(g => g.name);
    }

    getChannels(serverName: string): string[] {
        if (!this.client) return [];
        const guild = this._findGuild(serverName);
        if (!guild) return [];
        return [...guild.channels.cache.values()]
            .filter(c => c.isVoiceBased() && c.type !== ChannelType.GuildStageVoice)
            .sort((a, b) => (a.position ?? 0) - (b.position ?? 0))
            .map(c => c.name);
    }

    private _applyStatus(): void {
        if (!this.client?.user) return;
        const text = this.config.botStatus && this.config.botStatus.trim().length > 0
            ? this.config.botStatus.trim()
            : 'Playing with ACT Triggers';
        try {
            this.client.user.setActivity(text, { type: ActivityType.Custom });
        } catch (e) {
            log.warn('setActivity failed: ' + log.errMsg(e));
        }
    }

    async joinChannel(serverName: string, channelName: string): Promise<OpResult> {
        log.info(`joinChannel: server='${serverName}' channel='${channelName}'`);
        const guild = this._findGuild(serverName);
        if (!guild) return { ok: false, error: `Server '${serverName}' not found.` };
        const channel = [...guild.channels.cache.values()].find(c =>
            c.isVoiceBased() && c.name === channelName);
        if (!channel) return { ok: false, error: `Voice channel '${channelName}' not found in server '${serverName}'.` };

        try {
            const existing = getVoiceConnection(guild.id);
            if (existing) {
                log.info('joinChannel: leaving existing connection first');
                await this.leaveChannel();
            }

            log.info('joinChannel: joinVoiceChannel + DAVE handshake');
            this.connection = joinVoiceChannel({
                channelId: channel.id,
                guildId: guild.id,
                adapterCreator: guild.voiceAdapterCreator,
                selfDeaf: true,
                selfMute: false,
            });
            this.currentGuildId = guild.id;

            this.connection.on('stateChange', (oldS, newS) => {
                log.info(`voice ${oldS.status} -> ${newS.status}`);
            });
            this.connection.on('error', (err: Error) => {
                log.error('voice connection error', err);
                this._sendLog('Error', `voice: ${err.message}`);
            });

            await entersState(this.connection, VoiceConnectionStatus.Ready, 30_000);
            log.info('joinChannel: voice Ready');
            this._startPingLog();

            this.mixer = new PcmMixer();
            // maxMissedFrames: with the mixer's pull-based _read producing
            // a chunk per call, the encoder should never see null. But a
            // GC pause that delays our _read by >100 ms (default tolerance)
            // would otherwise stop the player permanently. Disable the
            // missed-frame stop so transient delays just emit silence.
            this.player = createAudioPlayer({
                behaviors: { maxMissedFrames: Number.MAX_SAFE_INTEGER },
            });
            this.player.on('stateChange', (oldS, newS) => {
                log.info(`player ${oldS.status} -> ${newS.status}`);
            });
            this.player.on('error', (err: Error) => {
                log.error('player error', err);
                this._sendLog('Error', `player: ${err.message}`);
            });
            this.connection.subscribe(this.player);

            // One long-lived resource fed by the mixer. The mixer never
            // ends, so this single play() call drives all subsequent audio
            // (each speakPcm/speakFile just adds a voice into the mixer).
            const resource = createAudioResource(this.mixer, { inputType: StreamType.Raw });
            this.player.play(resource);

            // StreamType.Raw makes @discordjs/voice insert a prism Opus encoder
            // into the pipeline and expose it as resource.encoder. Hold it so we
            // can tune the bitrate now and on later SetConfig pushes that
            // change audioQualityIndex.
            this.encoder = resource.encoder;
            if (!this.encoder) log.warn('joinChannel: resource has no Opus encoder; bitrate control unavailable');
            this._applyBitrate();

            // Warm the ONNX model once on join (off the critical path) so the first
            // real callout isn't paying cold-start latency. No-op for SAPI / when no
            // ONNX voice is configured.
            if (this.onnxTts.isReady()) {
                void this.onnxTts.synth('Discord triggers ready.').then(
                    () => log.info('ONNX warm-up done'),
                    (e: unknown) => log.warn('ONNX warm-up failed: ' + log.errMsg(e)),
                );
            }

            return { ok: true, error: '' };
        } catch (e) {
            log.error('joinChannel failed', e);
            // entersState timeout (or any partial-init throw) leaves a live
            // VoiceConnection registered with @discordjs/voice. leaveChannel
            // is idempotent and tolerates partial state — it'll find the
            // orphan via getVoiceConnection(currentGuildId) and destroy it.
            try { await this.leaveChannel(); } catch { /* ignore */ }
            return { ok: false, error: log.errMsg(e) };
        }
    }

    // Synchronous in practice (VoiceConnection.destroy() returns void), but the
    // Host contract is async, so return a resolved promise rather than marking
    // the method `async` (which would have no `await`).
    leaveChannel(): Promise<void> {
        log.info('leaveChannel');
        this._stopPingLog();
        this.mixer?.clear();
        this.mixer = null;
        this.encoder = undefined;
        try { this.player?.stop(true); } catch { /* ignore */ }
        this.player = null;
        try {
            if (this.currentGuildId) {
                const conn = getVoiceConnection(this.currentGuildId);
                conn?.destroy();
            } else if (this.connection) {
                this.connection.destroy();
            }
        } catch { /* ignore */ }
        this.connection = null;
        this.currentGuildId = null;
        return Promise.resolve();
    }

    speakPcm(pcmBuffer: Buffer, meta?: SpeakMeta): OpResult {
        const guard = this._guardPlayback();
        if (!guard.ok) return guard;
        return this._enqueue('SpeakPcm', pcmBuffer, meta);
    }

    // Enqueue a fully-prepared 48k/16/stereo buffer into the mixer and, when a
    // per-trigger meta is present, stamp the local pipeline: recv->enqueue ms
    // (this much was pure program time) plus a voice-RTT snapshot taken at the
    // exact moment of this trigger (#2). The mixer later logs enqueue->firstEmit
    // for the same reqId (#1), closing the gap between "queued" and "on the wire".
    private _enqueue(kind: string, pcm: Buffer, meta?: SpeakMeta): OpResult {
        const reqId = meta?.reqId ?? 0;
        // Optional random sound effect. The bridge owns the whole decision: roll
        // the dice from the current config (randomFx + fxChance), then let
        // applyRandomEffect pick which effect. Applied on the complete buffer
        // before it enters the mixer, so the recv->enqueue stamp below includes
        // the DSP time as the program cost it is.
        let buf = pcm;
        if (this.config.randomFx && this._rollFx()) {
            try {
                const fx = applyRandomEffect(buf);
                buf = fx.pcm;
                log.info(`fx reqId=${reqId} effect=${fx.name} ` +
                    `inMs=${this._pcmDurationMs(pcm.length)} outMs=${this._pcmDurationMs(buf.length)}`);
            } catch (e) {
                log.error('random effect failed; playing dry', e);
            }
        }
        // Auto-level AFTER the effect so the effect's own level change is what we
        // correct — a distortion hit that came out hot, or an echo tail that
        // dropped the average, both land near the target loudness. The config
        // carries a positive magnitude; negate it to a dBFS RMS target.
        if (this.config.normalize) {
            const targetDb = -Math.abs(this.config.normalizeTarget);
            try {
                const norm = normalizePcm16(buf, targetDb);
                if (norm.applied) {
                    buf = norm.pcm;
                    log.info(`normalize reqId=${reqId} gain=${norm.gain.toFixed(3)} target=${targetDb}dBFS`);
                }
            } catch (e) {
                log.error('normalize failed; playing un-leveled', e);
            }
        }
        // Declick last: fade the clip in/out so its edge samples ramp from/to
        // zero instead of stepping against the mixer's digital silence (the step
        // is an audible click). Runs after effects + normalize so the final
        // samples reach zero whatever those stages did to the level.
        buf = declick(buf);
        // Diagnostic tap: write the final, exactly-as-played buffer to the WAV
        // sink when enabled. Fires whether or not a live mixer exists, so it both
        // records a live bot and captures audio in offline capture mode.
        if (this.sink) {
            try {
                const path = this.sink.write(`${kind}-${reqId}`, buf);
                log.info(`audio sink: ${kind} reqId=${reqId} -> ${path}`);
            } catch (e) {
                log.error('audio sink write failed', e);
            }
        }
        const enqueueT = performance.now();
        // In offline capture mode there is no mixer; the sink write above is the
        // whole delivery. When live, feed the mixer as usual.
        if (this.mixer) {
            const r = this.mixer.addVoice(buf, { id: reqId, enqueueT });
            if (r.dropped > 0) this._sendLog('Warn', `Mixer overflow: dropped ${r.dropped} voice(s)`);
        }
        if (meta) {
            const recvToEnqueue = (enqueueT - meta.recvT).toFixed(1);
            log.info(`${kind} reqId=${reqId} pcmMs=${this._pcmDurationMs(buf.length)} ` +
                `recv->enqueue=${recvToEnqueue}ms ${this._pingStr()}`);
        }
        return { ok: true, error: '' };
    }

    // Bytes of 48k/16-bit/stereo PCM -> clip duration in ms (192 bytes per ms).
    private _pcmDurationMs(bytes: number): number {
        return Math.round(bytes / (TARGET_SAMPLE_RATE * 2 * (TARGET_BITS / 8) / 1000));
    }

    // Roll the per-clip random-effect dice from config.fxChance (0..100).
    private _rollFx(): boolean {
        const chance = this.config.fxChance;
        if (chance <= 0) return false;
        if (chance >= 100) return true;
        return Math.random() * 100 < chance;
    }

    // Voice RTT snapshot for the current connection. udp is the true media-path
    // RTT but is often undefined under DAVE; ws (voice gateway heartbeat) is the
    // fallback network-health signal. A late trigger with healthy rtt points at
    // program/buffering; a late trigger with a spiking rtt points at the bot
    // host's link to Discord. (Listener-side internet stays unobservable here.)
    private _pingStr(): string {
        try {
            const p = this.connection?.ping;
            if (!p) return 'rtt=n/a';
            const parts: string[] = [];
            if (typeof p.udp === 'number') parts.push(`udp=${p.udp}ms`);
            if (typeof p.ws === 'number') parts.push(`ws=${p.ws}ms`);
            return parts.length > 0 ? `rtt[${parts.join(' ')}]` : 'rtt=n/a';
        } catch { return 'rtt=n/a'; }
    }

    async speakFile(path: string, meta?: SpeakMeta): Promise<OpResult> {
        const guard = this._guardPlayback();
        if (!guard.ok) return guard;

        // stat first so we can short-circuit on a cache hit and let mtime
        // invalidate stale entries when the user edits the file in place.
        let mtimeMs: number;
        let size: number;
        try {
            const st = await stat(path);
            mtimeMs = st.mtimeMs;
            size = st.size;
        } catch (e) {
            return { ok: false, error: `Cannot read file: ${log.errMsg(e)}` };
        }

        const cachedPcm = this.wavCache.get(path, mtimeMs);
        if (cachedPcm) {
            log.info(`SpeakFile cache hit: ${path} (${cachedPcm.length} bytes)`);
            return this._enqueue('SpeakFile', cachedPcm, meta);
        }

        if (size > MAX_DECODE_INPUT_BYTES) {
            return { ok: false, error: `Audio file too large: ${size} bytes (max ${MAX_DECODE_INPUT_BYTES})` };
        }

        let finalPcm: Buffer;
        try {
            finalPcm = await decodeFileToFinalPcm(path);
        } catch (e) {
            return { ok: false, error: log.errMsg(e) };
        }

        this.wavCache.set(path, mtimeMs, finalPcm);
        return this._enqueue('SpeakFile', finalPcm, meta);
    }

    // ONNX TTS: synthesize the text with the voice learned from SetConfig, convert
    // the model's Float32 mono output to 48k/16/stereo, then rejoin the same
    // fx/normalize/declick path as SpeakPcm/SpeakFile via _enqueue. A not-ready or
    // empty-synthesis case logs + skips so a bad voice never drops the connection.
    async speakText(text: string, meta?: SpeakMeta): Promise<OpResult> {
        const guard = this._guardPlayback();
        if (!guard.ok) return guard;
        if (!this.onnxTts.isReady()) {
            this._sendLog('Warn', 'ONNX voice not ready; skipped this callout.');
            return { ok: false, error: 'ONNX voice not ready' };
        }
        let audio;
        try {
            audio = await this.onnxTts.synth(text);
        } catch (e) {
            return { ok: false, error: log.errMsg(e) };
        }
        if (!audio || audio.samples.length === 0) {
            this._sendLog('Warn', 'ONNX synthesis produced no audio; skipped.');
            return { ok: false, error: 'ONNX synthesis produced no audio' };
        }
        const stereoSrc = monoFloat32ToStereoInt16(audio.samples);
        const finalPcm = resampleStereo16(stereoSrc, audio.sampleRate, TARGET_SAMPLE_RATE);
        log.info(`SpeakText synth: voice=${this.onnxTts.describe()} srcRate=${audio.sampleRate} outBytes=${finalPcm.length}`);
        return this._enqueue('SpeakText', finalPcm, meta);
    }

    private _startPingLog(): void {
        this._stopPingLog();
        const tick = (): void => {
            if (!this.connection) return;
            try {
                // VoiceConnection.ping is { ws, udp } in @discordjs/voice 0.18+.
                // ws = voice gateway heartbeat RTT. udp may be undefined for
                // DAVE-encrypted connections — omit it from the log when so.
                const p = this.connection.ping;
                const parts: string[] = [];
                if (typeof p.ws === 'number') parts.push(`ws=${p.ws}ms`);
                if (typeof p.udp === 'number') parts.push(`udp=${p.udp}ms`);
                if (parts.length > 0) log.info(`Discord voice RTT: ${parts.join(' ')}`);
            } catch (e) {
                log.warn('voice ping unavailable: ' + log.errMsg(e));
            }
        };
        // Wait 5s for the first WS heartbeat to populate before logging, then
        // sample every 60s. Both timers go through `pingTimer` so _stopPingLog
        // cancels whichever is currently scheduled.
        this.pingTimer = setTimeout(() => {
            tick();
            this.pingTimer = setInterval(tick, 60_000);
            if (this.pingTimer.unref) this.pingTimer.unref();
        }, 5_000);
        if (this.pingTimer.unref) this.pingTimer.unref();
    }

    private _stopPingLog(): void {
        if (this.pingTimer) {
            // Node's Timer object accepts either clearTimeout or clearInterval
            // regardless of which scheduled it, so calling both is safe.
            clearTimeout(this.pingTimer);
            clearInterval(this.pingTimer);
            this.pingTimer = null;
        }
    }

    private _liveReady(): boolean {
        return this.connection !== null
            && this.connection.state.status === VoiceConnectionStatus.Ready
            && this.player !== null && this.mixer !== null;
    }

    private _guardPlayback(): OpResult {
        // Offline capture mode: with the WAV sink enabled, deliver to a file even
        // without a live voice channel so the pipeline is testable end-to-end.
        if (this._liveReady() || this.sink) return { ok: true, error: '' };
        const connected = this.connection?.state.status === VoiceConnectionStatus.Ready;
        return { ok: false, error: connected ? 'Audio player not ready.' : 'Not connected to a voice channel.' };
    }

    private _findGuild(name: string): Guild | null {
        if (!this.client) return null;
        for (const g of this.client.guilds.cache.values()) {
            if (g.name === name) return g;
        }
        return null;
    }

    private _sendLog(level: LogLevel, message: string): void {
        if (this.notify) {
            try { this.notify({ op: 'Log', level, message }); } catch { /* ignore */ }
        }
    }
}
