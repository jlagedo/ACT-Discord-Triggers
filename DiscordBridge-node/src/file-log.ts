import * as fs from 'node:fs';
import * as path from 'node:path';
import * as os from 'node:os';

type FileLogLevel = 'INFO' | 'WARN' | 'ERROR';

let logPath: string | null = null;
const writeQueue: string[] = [];
let writing = false;

const MAX_BYTES = 5 * 1024 * 1024;

// Latch so a persistently unwritable log path is reported once (to stderr, which
// the plugin captures) instead of silently dropping every diagnostic line.
let writeFailed = false;

function exeDir(): string {
    // process.execPath is always node.exe (prod ships node.exe next to bundle.js,
    // dev runs via npm), so this falls back to the main script's cwd.
    try {
        const dir = path.dirname(process.execPath);
        if (process.execPath.toLowerCase().endsWith('node.exe')) {
            return process.cwd();
        }
        return dir;
    } catch {
        return process.cwd();
    }
}

export function init(): void {
    try {
        logPath = path.join(exeDir(), 'DiscordBridge.log');
        try {
            const st = fs.statSync(logPath);
            // Rotate (keep one prior generation) instead of deleting, so the
            // session that just hit the size cap isn't lost — that's often the
            // one a user is reporting. The plugin merges only the current file
            // into the unified diagnostics; .1 stays as a manual fallback.
            if (st.size > MAX_BYTES) {
                try { fs.renameSync(logPath, logPath + '.1'); }
                catch { try { fs.unlinkSync(logPath); } catch { /* ignore */ } }
            }
        } catch { /* file may not exist */ }
        info(`==== Bridge starting (pid=${process.pid}, node=${process.version}, os=${os.platform()} ${os.release()}, exe=${process.execPath}) ====`);
    } catch { /* swallow */ }
}

function ts(): string {
    const d = new Date();
    const pad = (n: number, w = 2) => String(n).padStart(w, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
           `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

// --- Secret redaction --------------------------------------------------------
// DiscordBridge.log (and the Log notifications pushed to the plugin) get attached
// to bug reports, so the bot token must never appear in either. Register it from
// SetConfig and run every logged message through redact(), so logging *any* error
// is safe no matter what threw — a discord.js login/REST error, an uncaught stack,
// etc. The exact-token registry is the real guarantee; the token-shape regex is a
// fallback for anything logged before a token is registered.
const secrets = new Set<string>();

// Discord bot tokens are three base64url segments joined by dots
// (id . timestamp . hmac). Specific enough not to hit model paths / op names.
const TOKEN_SHAPE = /[A-Za-z0-9_-]{23,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{27,}/g;

// Register a value to scrub from all future log output. Short/blank values are
// ignored so we never replace a trivial substring across every line.
export function redactSecret(value: string | null | undefined): void {
    if (typeof value === 'string' && value.trim().length >= 8) secrets.add(value);
}

// Replace registered secrets (exact, literal) and token-shaped runs with '***'.
export function redact(text: string): string {
    let out = text;
    for (const s of secrets) {
        if (out.includes(s)) out = out.split(s).join('***');
    }
    return out.replace(TOKEN_SHAPE, '***');
}

function write(level: FileLogLevel, msg: string): void {
    if (!logPath) return;
    const line = `${ts()} ${level} ${redact(msg)}${os.EOL}`;
    writeQueue.push(line);
    drain();
}

function drain(): void {
    if (writing || writeQueue.length === 0 || !logPath) return;
    writing = true;
    const batch = writeQueue.splice(0, writeQueue.length).join('');
    fs.appendFile(logPath, batch, { encoding: 'utf8' }, (err) => {
        writing = false;
        if (err && !writeFailed) {
            writeFailed = true;
            // stderr is allowed (only stdout is reserved for BRIDGE_READY). Report
            // once so a broken log path is visible without spamming every line.
            try { process.stderr.write(`BRIDGE_LOG_WRITE_FAILED ${redact(err.message)}\n`); }
            catch { /* nothing more we can do */ }
        }
        if (writeQueue.length > 0) drain();
    });
}

export function info(msg: string): void { write('INFO', msg); }
export function warn(msg: string): void { write('WARN', msg); }

export function error(msg: string, err?: unknown): void {
    if (err !== undefined) {
        if (err instanceof Error) {
            const name = err.name || 'Error';
            const message = err.message || String(err);
            write('ERROR', `${msg} :: ${name}: ${message}`);
            if (err.stack) write('ERROR', err.stack);
        } else {
            write('ERROR', `${msg} :: ${stringifyNonError(err)}`);
        }
    } else {
        write('ERROR', msg);
    }
}

export function errMsg(e: unknown): string {
    return e instanceof Error ? (e.message || String(e)) : String(e);
}

// Stringify a thrown value that isn't an Error. JSON-encodes objects so we log
// their shape instead of "[object Object]", and degrades safely for values that
// can't be serialized (circular refs, BigInt, etc.).
function stringifyNonError(value: unknown): string {
    if (typeof value === 'object' && value !== null) {
        try {
            return JSON.stringify(value) ?? '[unserializable value]';
        } catch {
            return '[unserializable value]';
        }
    }
    return String(value);
}
