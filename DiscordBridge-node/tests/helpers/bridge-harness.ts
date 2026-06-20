// Spawns the real bridge (via tsx) and talks to it over the named pipe, with an
// env overlay so a test can switch on the diagnostic audio sink. Mirrors the
// minimal client in lifecycle.test.ts but adds env support and a Log-notification
// collector, which the ONNX capture test needs.

import { spawn, type ChildProcess } from 'node:child_process';
import * as net from 'node:net';
import * as path from 'node:path';
import * as readline from 'node:readline';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pkgRoot = path.resolve(__dirname, '..', '..');
const BRIDGE_ENTRY = path.join('src', 'bridge.ts');

export interface Bridge {
    proc: ChildProcess;
    pipeName: string;
    pipePath: string;
    stdout: string[];
    stderr: string[];
    exited: Promise<number | null>;
}

export async function spawnBridge(suffix: string, env?: Record<string, string>): Promise<Bridge> {
    const pipeName = `bridge-test-${process.pid}-${Date.now()}-${suffix}`;
    const proc = spawn(
        process.execPath,
        ['--import', 'tsx', BRIDGE_ENTRY, pipeName],
        { cwd: pkgRoot, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, ...env } },
    );
    const stdout: string[] = [];
    const stderr: string[] = [];
    if (proc.stderr) {
        readline.createInterface({ input: proc.stderr }).on('line', (l) => stderr.push(l));
    }
    const exited = new Promise<number | null>((resolve) => proc.once('exit', (c) => resolve(c)));

    await new Promise<void>((resolve, reject) => {
        const timer = setTimeout(
            () => reject(new Error(`BRIDGE_READY timeout (15s). stderr=${stderr.join('\n')}`)),
            15_000,
        );
        if (!proc.stdout) { clearTimeout(timer); reject(new Error('proc.stdout missing')); return; }
        readline.createInterface({ input: proc.stdout }).on('line', (line) => {
            stdout.push(line);
            if (line.startsWith('BRIDGE_READY')) { clearTimeout(timer); resolve(); }
        });
        proc.once('error', (err) => { clearTimeout(timer); reject(err); });
        proc.once('exit', (code) => {
            clearTimeout(timer);
            reject(new Error(`bridge exited (code=${code}) before BRIDGE_READY. stderr=${stderr.join('\n')}`));
        });
    });

    return { proc, pipeName, pipePath: `\\\\.\\pipe\\${pipeName}`, stdout, stderr, exited };
}

export async function killIfAlive(bridge: Bridge): Promise<void> {
    if (bridge.proc.exitCode === null && bridge.proc.signalCode === null) {
        try { bridge.proc.kill(); } catch { /* ignore */ }
        await Promise.race([bridge.exited, new Promise<void>((r) => setTimeout(r, 3000))]);
    }
}

export class TestPipeClient {
    private buf = Buffer.alloc(0);
    private waiters = new Map<number, (msg: Record<string, unknown>) => void>();
    private nextReqId = 1;
    public readonly logs: Array<Record<string, unknown>> = [];

    private constructor(public readonly socket: net.Socket) {
        socket.on('data', (chunk: Buffer) => this._onData(chunk));
    }

    static async connect(pipePath: string): Promise<TestPipeClient> {
        const socket = net.createConnection(pipePath);
        await new Promise<void>((resolve, reject) => {
            socket.once('connect', () => resolve());
            socket.once('error', reject);
        });
        return new TestPipeClient(socket);
    }

    private _onData(chunk: Buffer): void {
        this.buf = Buffer.concat([this.buf, chunk]);
        while (this.buf.length >= 4) {
            const len = this.buf.readUInt32LE(0);
            if (this.buf.length < 4 + len) break;
            const json = this.buf.subarray(4, 4 + len).toString('utf8');
            this.buf = this.buf.subarray(4 + len);
            let obj: Record<string, unknown>;
            try { obj = JSON.parse(json) as Record<string, unknown>; } catch { continue; }
            const reqId = obj['reqId'];
            if (typeof reqId === 'number') {
                const w = this.waiters.get(reqId);
                if (w) { this.waiters.delete(reqId); w(obj); }
            } else if (obj['op'] === 'Log') {
                this.logs.push(obj);
            }
        }
    }

    send(op: string, fields: Record<string, unknown> = {}): Promise<Record<string, unknown>> {
        const reqId = this.nextReqId++;
        const json = Buffer.from(JSON.stringify({ op, reqId, ...fields }), 'utf8');
        const len = Buffer.alloc(4);
        len.writeUInt32LE(json.length, 0);
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                this.waiters.delete(reqId);
                reject(new Error(`response timeout for ${op} reqId=${reqId}`));
            }, 30_000);
            this.waiters.set(reqId, (m) => { clearTimeout(timer); resolve(m); });
            this.socket.write(len);
            this.socket.write(json);
        });
    }

    close(): void { this.socket.destroy(); }
}
