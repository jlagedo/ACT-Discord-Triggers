import type { Socket } from 'node:net';
import { performance } from 'node:perf_hooks';

import * as log from './file-log.js';
import {
    Op,
    PROTOCOL_VERSION,
    MAX_FRAME_BYTES,
    FRAME_JSON_MARKER,
    FRAME_BINARY_SPEAK_PCM,
    BINARY_SPEAK_PCM_HEADER_BYTES,
    DEFAULT_CONFIG_VIEW,
    type BridgeConfigView,
    type Notification,
    type OutboundFrame,
    type ReqId,
    type ResultData,
} from './protocol.js';
import pkg from '../package.json' with { type: 'json' };

const BRIDGE_VERSION: string = pkg.version;

export { Op, PROTOCOL_VERSION };

export type Notifier = (n: Notification) => void;

export interface OpResult { ok: boolean; error: string }

// Per-trigger latency context handed to the host so it can stamp the local
// pipeline (recv -> enqueue) and snapshot voice RTT for that exact trigger.
// recvT is a monotonic performance.now() captured the moment the frame was read.
// Whether a random effect is applied is decided by the host from its config, not
// per trigger — so there is no fx flag here.
export interface SpeakMeta { reqId: number; recvT: number }

// Minimal surface PipeServer needs from the host. discord-host.ts implements this.
export interface Host {
    setNotifier(fn: Notifier): void;
    setConfig(config: BridgeConfigView, ttsParams?: Record<string, string>): void;
    // Whether local sound-device output is currently running (outputMode='local'
    // with the device successfully opened). Read right after setConfig so the
    // SetConfig reply can tell the plugin whether local output actually came up.
    isLocalOutputActive(): boolean;
    connect(): Promise<OpResult>;
    disconnect(): Promise<void>;
    isConnected(): boolean;
    getServers(): string[];
    getChannels(serverName: string): string[];
    joinChannel(serverName: string, channelName: string): Promise<OpResult>;
    leaveChannel(): Promise<void>;
    speakPcm(pcmBuffer: Buffer, meta?: SpeakMeta): OpResult;
    speakFile(path: string, meta?: SpeakMeta): Promise<OpResult>;
    speakText(text: string, meta?: SpeakMeta): Promise<OpResult>;
}

interface IncomingMessage {
    op: string;
    reqId?: unknown;
    [k: string]: unknown;
}

function isIncomingMessage(x: unknown): x is IncomingMessage {
    return typeof x === 'object' && x !== null && typeof (x as { op?: unknown }).op === 'string';
}

function asString(v: unknown, fallback = ''): string {
    return typeof v === 'string' ? v : fallback;
}

function asNumber(v: unknown): number | null {
    return typeof v === 'number' ? v : null;
}

function asBool(v: unknown, fallback: boolean): boolean {
    return typeof v === 'boolean' ? v : fallback;
}

// Coerce the raw incoming config object into the bridge's view. The plugin sends
// its whole settings POCO; we read the fields we care about and default the rest.
// Unknown fields (token persistence, TTS, autoConnect) are simply ignored.
function parseConfig(raw: Record<string, unknown>): BridgeConfigView {
    const d = DEFAULT_CONFIG_VIEW;
    return {
        botToken: asString(raw['botToken'], d.botToken),
        botStatus: asString(raw['botStatus'], d.botStatus),
        outputMode: asString(raw['outputMode'], d.outputMode),
        randomFx: asBool(raw['randomFx'], d.randomFx),
        fxChance: asNumber(raw['fxChance']) ?? d.fxChance,
        normalize: asBool(raw['normalize'], d.normalize),
        normalizeTarget: asNumber(raw['normalizeTarget']) ?? d.normalizeTarget,
        audioQualityIndex: asNumber(raw['audioQualityIndex']) ?? d.audioQualityIndex,
        limiterEnabled: asBool(raw['limiterEnabled'], d.limiterEnabled),
        limiterCeilingIndex: asNumber(raw['limiterCeilingIndex']) ?? d.limiterCeilingIndex,
    };
}

export class PipeServer {
    private readonly socket: Socket;
    private readonly host: Host;
    private readBuf: Buffer;
    private writeQueue: Promise<void>;
    private closed: boolean;

    constructor(socket: Socket, host: Host) {
        this.socket = socket;
        this.host = host;
        this.readBuf = Buffer.alloc(0);
        this.writeQueue = Promise.resolve();
        this.closed = false;
    }

    run(): void {
        this.host.setNotifier((notif: Notification) => { void this._sendFrame(notif); });

        this.socket.on('data', (chunk: Buffer) => {
            this.readBuf = Buffer.concat([this.readBuf, chunk]);
            this._tryReadFrames();
        });
        this.socket.on('error', (err: Error) => {
            log.error('pipe socket error', err);
            this.closed = true;
        });
        this.socket.on('close', () => {
            log.info('pipe closed by peer');
            this.closed = true;
        });
        this.socket.on('end', () => {
            log.info('pipe end (peer half-close)');
        });
    }

    private _tryReadFrames(): void {
        while (this.readBuf.length >= 4) {
            const len = this.readBuf.readUInt32LE(0);
            if (len <= 0 || len > MAX_FRAME_BYTES) {
                log.error(`invalid frame length ${len}; closing pipe`);
                try { this.socket.destroy(); } catch { /* ignore */ }
                this.closed = true;
                return;
            }
            if (this.readBuf.length < 4 + len) return;
            const payload = this.readBuf.subarray(4, 4 + len);
            this.readBuf = this.readBuf.subarray(4 + len);
            const first = payload[0];
            // Fire-and-forget so a slow handler doesn't block reads.
            if (first === FRAME_JSON_MARKER) {
                this._handleJsonFrame(payload.toString('utf8'))
                    .catch((e: unknown) => log.error('json handler crash', e));
            } else if (first === FRAME_BINARY_SPEAK_PCM) {
                // Copy out of the read buffer slice so subsequent reads don't
                // overwrite the bytes the audio player is holding onto.
                const frame = Buffer.from(payload);
                this._handleBinarySpeakPcm(frame)
                    .catch((e: unknown) => log.error('binary handler crash', e));
            } else {
                log.error(`unknown frame marker 0x${(first ?? 0).toString(16)}; dropping`);
            }
        }
    }

    private async _handleBinarySpeakPcm(payload: Buffer): Promise<void> {
        const recvT = performance.now();
        if (payload.length < BINARY_SPEAK_PCM_HEADER_BYTES) {
            log.error(`binary SpeakPcm frame too short: ${payload.length} bytes`);
            return;
        }
        const reqId = payload.readUInt32LE(1);
        const sampleRate = payload.readUInt32LE(5);
        const bits = payload.readUInt8(9);
        const channels = payload.readUInt8(10);
        const pcm = payload.subarray(BINARY_SPEAK_PCM_HEADER_BYTES);
        log.info(`--> SpeakPcm reqId=${reqId} pcmBytes=${pcm.length} fmt=${sampleRate}/${bits}/${channels}`);
        // Bridge audio path is hard-wired to 48 kHz / 16-bit / stereo end-to-end
        // (see CLAUDE.md "Audio format constraint"). Reject mismatched payloads
        // up front rather than feeding the mixer something it would replay at
        // the wrong rate or with garbled framing.
        if (sampleRate !== 48000 || bits !== 16 || channels !== 2) {
            await this._result(reqId, false,
                `Unsupported PCM format: ${sampleRate}/${bits}/${channels}; expected 48000/16/2`);
            return;
        }
        try {
            const r = this.host.speakPcm(pcm, { reqId, recvT });
            await this._result(reqId, r.ok, r.error);
        } catch (e) {
            const message = e instanceof Error ? e.message : String(e);
            log.error(`SpeakPcm handler threw: ${message}`);
            await this._sendFrame({ op: Op.Log, level: 'Error', message: `Handler 'SpeakPcm' threw: ${message}` });
            await this._result(reqId, false, message);
        }
    }

    private async _handleJsonFrame(json: string): Promise<void> {
        let op = '?';
        let reqId: ReqId = null;
        try {
            const parsed: unknown = JSON.parse(json);
            // Pull reqId opportunistically *before* shape validation so a
            // malformed-but-reqId-bearing frame can get a synthesized error
            // response via the catch path.
            reqId = asNumber((parsed as { reqId?: unknown } | null)?.reqId);
            if (!isIncomingMessage(parsed)) {
                throw new Error('frame is not an object with op:string');
            }
            op = parsed.op;
            log.info(`--> ${op} reqId=${reqId} bytes=${json.length}`);

            switch (op) {
                case Op.Hello: {
                    const protocolVersion = asNumber(parsed['protocolVersion']);
                    const ok = protocolVersion === PROTOCOL_VERSION;
                    await this._result(reqId, ok,
                        ok ? '' : `Protocol version mismatch: bridge=${PROTOCOL_VERSION}, plugin=${protocolVersion}`,
                        { bridgeVersion: BRIDGE_VERSION });
                    break;
                }
                case Op.SetConfig: {
                    const raw = parsed['config'];
                    const cfg = (typeof raw === 'object' && raw !== null)
                        ? parseConfig(raw as Record<string, unknown>)
                        : DEFAULT_CONFIG_VIEW;
                    // ttsParams is an extensible string-map riding alongside the
                    // settings: the resolved ONNX synth descriptor (engine/family/
                    // modelDir/sid/lang/speed/threads). Pass it through verbatim;
                    // the host parses the keys it knows.
                    const rawParams = parsed['ttsParams'];
                    const ttsParams = (typeof rawParams === 'object' && rawParams !== null)
                        ? rawParams as Record<string, string>
                        : undefined;
                    this.host.setConfig(cfg, ttsParams);
                    // In local-output mode the device is brought up synchronously
                    // inside setConfig and can fail (no device, addon missing). Fail
                    // the SetConfig result via the existing ok/error envelope so the
                    // plugin — which only knows it *asked* for local mode — learns
                    // whether the device actually opened and can decide whether to
                    // route ACT's audio. Bot mode is always ok here.
                    if (cfg.outputMode === 'local' && !this.host.isLocalOutputActive()) {
                        await this._result(reqId, false,
                            'Local audio output failed to start — no output device, or the audio addon is missing.');
                    } else {
                        await this._result(reqId, true, '');
                    }
                    break;
                }
                case Op.Connect: {
                    const r = await this.host.connect();
                    await this._result(reqId, r.ok, r.error);
                    break;
                }
                case Op.IsConnected: {
                    await this._result(reqId, true, '', { connected: this.host.isConnected() });
                    break;
                }
                case Op.GetServers: {
                    await this._result(reqId, true, '', { servers: this.host.getServers() });
                    break;
                }
                case Op.GetChannels: {
                    await this._result(reqId, true, '', { channels: this.host.getChannels(asString(parsed['server'])) });
                    break;
                }
                case Op.JoinChannel: {
                    const r = await this.host.joinChannel(asString(parsed['server']), asString(parsed['channel']));
                    await this._result(reqId, r.ok, r.error);
                    break;
                }
                case Op.LeaveChannel: {
                    await this.host.leaveChannel();
                    await this._result(reqId, true, '');
                    break;
                }
                case Op.SpeakFile: {
                    const recvT = performance.now();
                    const r = await this.host.speakFile(asString(parsed['path']), { reqId: reqId ?? 0, recvT });
                    await this._result(reqId, r.ok, r.error);
                    break;
                }
                case Op.SpeakText: {
                    const recvT = performance.now();
                    const r = await this.host.speakText(asString(parsed['text']), { reqId: reqId ?? 0, recvT });
                    await this._result(reqId, r.ok, r.error);
                    break;
                }
                case Op.Shutdown: {
                    log.info('Shutdown requested');
                    try { await this.host.disconnect(); } catch { /* ignore */ }
                    try { this.socket.end(); } catch { /* ignore */ }
                    setImmediate(() => process.exit(0));
                    break;
                }
                default: {
                    await this._sendFrame({ op: Op.Log, level: 'Error', message: `Unknown op: ${op}` });
                    break;
                }
            }
        } catch (e) {
            log.error(`handler '${op}' threw`, e);
            try {
                const message = e instanceof Error ? e.message : String(e);
                // Queue both error frames synchronously so a concurrently-
                // dispatched next handler can't interleave its response
                // between our Log and synthesized Result.
                const pending: Promise<void>[] = [
                    this._sendFrame({ op: Op.Log, level: 'Error', message: `Handler '${op}' threw: ${message}` }),
                ];
                if (reqId !== null) {
                    pending.push(this._result(reqId, false, message));
                }
                await Promise.all(pending);
            } catch { /* ignore */ }
        }
    }

    // The single response envelope: every command/config reply is `Result`,
    // correlated by reqId. `data` carries op-specific payload for queries.
    private _result(reqId: ReqId, ok: boolean, error: string, data?: ResultData): Promise<void> {
        return this._sendFrame(
            data !== undefined
                ? { op: Op.Result, reqId, ok, error, data }
                : { op: Op.Result, reqId, ok, error },
        );
    }

    private _sendFrame(obj: OutboundFrame): Promise<void> {
        // Scrub Log notifications before they leave the bridge: their message can
        // carry a discord.js error string, and these end up in the plugin's
        // shared diagnostics. Other frames (Result, BotReady, Disconnected) never
        // carry the token. This is the wire-side twin of file-log's redaction.
        const outbound: OutboundFrame = obj.op === Op.Log
            ? { ...obj, message: log.redact(obj.message) }
            : obj;
        // Serialize writes so frames can't interleave. Length + body go in a
        // single socket.write so the kernel either flushes both or fails both —
        // a torn frame (header without body) is impossible with one syscall.
        this.writeQueue = this.writeQueue.then(() => new Promise<void>((resolve) => {
            if (this.closed || !this.socket.writable) { resolve(); return; }
            try {
                const json = Buffer.from(JSON.stringify(outbound), 'utf8');
                const frame = Buffer.alloc(4 + json.length);
                frame.writeUInt32LE(json.length, 0);
                json.copy(frame, 4);
                this.socket.write(frame, () => resolve());
            } catch (e) {
                log.error('sendFrame failed', e);
                resolve();
            }
        }));
        return this.writeQueue;
    }
}
