import * as net from 'node:net';
import * as log from './file-log.js';
import { DiscordHost } from './discord-host.js';
import { PipeServer } from './pipe-server.js';
import { warmupDecoders } from './audio-decode.js';
import { initResampler } from './resample.js';

async function main(): Promise<void> {
    log.init();
    log.info(`argv: ${process.argv.slice(2).join(' ')}`);

    const args = process.argv.slice(2);
    const [pipeName] = args;
    if (!pipeName) {
        process.stderr.write('Usage: DiscordBridge <pipe-name>\n');
        log.error('missing pipe-name argument');
        process.exit(1);
    }
    const pipePath = `\\\\.\\pipe\\${pipeName}`;
    log.info(`creating pipe server '${pipePath}'`);

    const host = new DiscordHost();

    const server = net.createServer({ allowHalfOpen: false });

    // Startup watchdog. Once a client connects, teardown rides the socket's
    // close/error handlers; but before any client connects there is no such hook,
    // so if the plugin host is hard-killed during the spawn -> connect window the
    // bridge would be orphaned with nothing to close. Reap ourselves if no client
    // shows up in time. Overridable via ACT_DT_STARTUP_TIMEOUT_MS (used by tests).
    const startupTimeoutMs = Number(process.env.ACT_DT_STARTUP_TIMEOUT_MS) || 30000;
    let startupTimer: ReturnType<typeof setTimeout> | null = null;

    server.on('error', (err: Error) => {
        log.error('pipe server error', err);
        process.stderr.write(`BRIDGE_FATAL ${log.redact(err.message)}\n`);
        process.exit(2);
    });

    server.on('connection', (socket: net.Socket) => {
        if (startupTimer) { clearTimeout(startupTimer); startupTimer = null; }
        log.info('client connected');
        // Stop accepting new clients (one plugin per bridge)
        server.close();
        const pipe = new PipeServer(socket, host);
        pipe.run();
        // When the plugin disconnects (pipe close or pipe error), the bridge has
        // no purpose. Tear down discord.js (closes the gateway WebSocket) and exit.
        // Mirrors the .NET bridge which exits when ReadFrameAsync returns null.
        const onPeerGone = async (): Promise<void> => {
            log.info('peer gone; disconnect + exit');
            try { await host.disconnect(); } catch { /* ignore */ }
            // Give file-log a tick to flush, then exit.
            setImmediate(() => process.exit(0));
        };
        socket.once('close', () => { void onPeerGone(); });
        socket.once('error', () => { void onPeerGone(); });
    });

    // Accept exactly one client.
    server.maxConnections = 1;

    await new Promise<void>((resolve, reject) => {
        server.once('error', reject);
        server.listen(pipePath, () => {
            server.removeListener('error', reject);
            resolve();
        });
    });

    // Instantiate the audio decoders' and the resampler's WASM before announcing
    // readiness. This moves WASM compile cost off the first trigger's hot path,
    // and — because it runs before BRIDGE_READY — makes the build self-test a real
    // packaging gate: an unresolvable/unstaged WASM throws here, so no
    // BRIDGE_READY is printed and build.ps1 fails loudly instead of shipping a
    // broken bundle. initResampler() also primes the module the sync resample
    // functions assume is loaded.
    await Promise.all([warmupDecoders(), initResampler()]);

    // Plugin's BridgeProcess.cs scans stdout for line starting with "BRIDGE_READY".
    process.stdout.write(`BRIDGE_READY pipe=${pipeName}\n`);
    log.info('BRIDGE_READY printed; waiting for client');

    // Arm the orphan watchdog now that we're announced and listening. Cleared the
    // moment a client connects (see the connection handler above).
    startupTimer = setTimeout(() => {
        log.error(`no client connected within ${startupTimeoutMs}ms; exiting`);
        process.exit(0);
    }, startupTimeoutMs);

    const shutdown = async (sig: string): Promise<void> => {
        log.info(`signal ${sig}; shutting down`);
        try { await host.disconnect(); } catch { /* ignore */ }
        try { server.close(); } catch { /* ignore */ }
        process.exit(0);
    };
    process.on('SIGINT', () => { void shutdown('SIGINT'); });
    process.on('SIGTERM', () => { void shutdown('SIGTERM'); });
    process.on('SIGHUP', () => { void shutdown('SIGHUP'); });

    process.on('uncaughtException', (err: Error) => {
        // After an uncaughtException Node's state is undefined — limping along
        // produces incoherent IPC. Flush, write the fatal marker, and exit so
        // the plugin's BridgeProcess sees the OnExited event and tears down.
        log.error('uncaughtException', err);
        process.stderr.write(`BRIDGE_FATAL ${log.redact(err.stack || err.message)}\n`);
        process.exit(2);
    });
    process.on('unhandledRejection', (reason: unknown) => {
        // Log-only (no exit): unlike uncaughtException, a stray rejection doesn't
        // necessarily corrupt bridge state. Route through error(msg, err) so stack
        // formatting + secret redaction match every other error site.
        log.error('unhandledRejection', reason);
    });
}

main().catch((err: unknown) => {
    try { log.error('main crashed', err); } catch { /* ignore */ }
    const detail = err instanceof Error ? (err.stack || err.message) : String(err);
    process.stderr.write(`BRIDGE_FATAL ${log.redact(detail)}\n`);
    process.exit(2);
});
