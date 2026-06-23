import { EventEmitter } from 'node:events';

// Minimal stub of net.Socket exposing only the surface PipeServer touches:
//   - 'data' / 'error' / 'close' / 'end' events
//   - write(buf, cb)
//   - destroy() / end()
//   - writable boolean
//
// Tests push bytes inbound via `sock.emit('data', chunk)` and read outbound
// frames from `sock.writes`.
export class FakeSocket extends EventEmitter {
    public writable = true;
    public destroyed = false;
    public ended = false;
    public readonly writes: Buffer[] = [];

    // Stall mode: model a peer that has stopped draining the pipe. While stalled,
    // write() records the bytes but withholds its completion callback, so the
    // PipeServer's write queue can't advance — exactly the backpressure condition.
    private stalled = false;
    private heldCallbacks: Array<(err?: Error | null) => void> = [];

    stall(): void { this.stalled = true; }

    // Peer catches up: stop stalling and fire every withheld callback so the queued
    // writes flush.
    drain(): void {
        this.stalled = false;
        const cbs = this.heldCallbacks;
        this.heldCallbacks = [];
        for (const cb of cbs) setImmediate(cb);
    }

    write(chunk: Buffer | string, cb?: (err?: Error | null) => void): boolean {
        if (!this.writable) {
            // Real net.Socket would emit 'error' here; tests don't exercise that path.
            if (cb) setImmediate(cb);
            return false;
        }
        const buf = typeof chunk === 'string' ? Buffer.from(chunk, 'utf8') : chunk;
        this.writes.push(buf);
        if (this.stalled) {
            // Withhold the callback (and signal backpressure like a real socket past
            // its high-water mark) until drain() is called.
            if (cb) this.heldCallbacks.push(cb);
            return false;
        }
        // Mimic Node's behavior: invoke the write callback on next tick.
        if (cb) setImmediate(cb);
        return true;
    }

    destroy(): void {
        this.destroyed = true;
        this.writable = false;
    }

    end(): void {
        this.ended = true;
        this.writable = false;
    }

    // Concatenated outbound bytes — convenience for decodeFrames callers.
    drainedWrites(): Buffer {
        return Buffer.concat(this.writes);
    }
}
