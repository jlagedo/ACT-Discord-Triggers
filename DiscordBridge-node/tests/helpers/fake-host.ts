import type { Host, Notifier, OpResult, SpeakMeta } from '../../src/pipe-server.js';
import type { BridgeConfigView, Notification } from '../../src/protocol.js';

export interface RecordedCall {
    method: string;
    args: unknown[];
}

// Host stub for PipeServer dispatch tests. Records each method call and lets
// tests script return values via `next*` setters. `notify` captures the
// notifier passed to setNotifier so tests can fire push notifications and
// observe them on the socket.
export class FakeHost implements Host {
    public readonly calls: RecordedCall[] = [];
    public notify: Notifier | null = null;

    private _nextConnect: OpResult = { ok: true, error: '' };
    private _nextJoinChannel: OpResult = { ok: true, error: '' };
    private _nextSpeakPcm: OpResult = { ok: true, error: '' };
    private _nextSpeakFile: OpResult = { ok: true, error: '' };
    private _nextSpeakText: OpResult = { ok: true, error: '' };
    private _nextIsConnected = false;
    private _nextServers: string[] = [];
    private _nextChannels: string[] = [];

    private _connectThrows: Error | null = null;
    private _joinThrows: Error | null = null;
    private _speakPcmThrows: Error | null = null;
    private _speakFileThrows: Error | null = null;
    private _speakTextThrows: Error | null = null;

    setNotifier(fn: Notifier): void {
        this.calls.push({ method: 'setNotifier', args: [] });
        this.notify = fn;
    }

    setConfig(config: BridgeConfigView, ttsParams?: Record<string, string>): void {
        this.calls.push({ method: 'setConfig', args: [config, ttsParams] });
    }

    connect(): Promise<OpResult> {
        this.calls.push({ method: 'connect', args: [] });
        if (this._connectThrows) return Promise.reject(this._connectThrows);
        return Promise.resolve(this._nextConnect);
    }

    disconnect(): Promise<void> {
        this.calls.push({ method: 'disconnect', args: [] });
        return Promise.resolve();
    }

    isConnected(): boolean {
        this.calls.push({ method: 'isConnected', args: [] });
        return this._nextIsConnected;
    }

    getServers(): string[] {
        this.calls.push({ method: 'getServers', args: [] });
        return this._nextServers;
    }

    getChannels(serverName: string): string[] {
        this.calls.push({ method: 'getChannels', args: [serverName] });
        return this._nextChannels;
    }

    joinChannel(serverName: string, channelName: string): Promise<OpResult> {
        this.calls.push({ method: 'joinChannel', args: [serverName, channelName] });
        if (this._joinThrows) return Promise.reject(this._joinThrows);
        return Promise.resolve(this._nextJoinChannel);
    }

    leaveChannel(): Promise<void> {
        this.calls.push({ method: 'leaveChannel', args: [] });
        return Promise.resolve();
    }

    speakPcm(pcmBuffer: Buffer, meta?: SpeakMeta): OpResult {
        this.calls.push({ method: 'speakPcm', args: [pcmBuffer, meta] });
        if (this._speakPcmThrows) throw this._speakPcmThrows;
        return this._nextSpeakPcm;
    }

    speakFile(path: string, meta?: SpeakMeta): Promise<OpResult> {
        this.calls.push({ method: 'speakFile', args: [path, meta] });
        if (this._speakFileThrows) return Promise.reject(this._speakFileThrows);
        return Promise.resolve(this._nextSpeakFile);
    }

    speakText(text: string, meta?: SpeakMeta): Promise<OpResult> {
        this.calls.push({ method: 'speakText', args: [text, meta] });
        if (this._speakTextThrows) return Promise.reject(this._speakTextThrows);
        return Promise.resolve(this._nextSpeakText);
    }

    nextConnect(r: OpResult): void { this._nextConnect = r; }
    nextJoinChannel(r: OpResult): void { this._nextJoinChannel = r; }
    nextSpeakPcm(r: OpResult): void { this._nextSpeakPcm = r; }
    nextSpeakFile(r: OpResult): void { this._nextSpeakFile = r; }
    nextSpeakText(r: OpResult): void { this._nextSpeakText = r; }
    nextIsConnected(v: boolean): void { this._nextIsConnected = v; }
    nextServers(s: string[]): void { this._nextServers = s; }
    nextChannels(c: string[]): void { this._nextChannels = c; }

    connectThrows(err: Error): void { this._connectThrows = err; }
    joinChannelThrows(err: Error): void { this._joinThrows = err; }
    speakPcmThrows(err: Error): void { this._speakPcmThrows = err; }
    speakFileThrows(err: Error): void { this._speakFileThrows = err; }
    speakTextThrows(err: Error): void { this._speakTextThrows = err; }

    fireNotification(n: Notification): void {
        if (this.notify) this.notify(n);
    }
}
