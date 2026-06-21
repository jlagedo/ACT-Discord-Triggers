import { Readable } from 'node:stream';
import { performance } from 'node:perf_hooks';

import * as log from './file-log.js';

// Audio format invariants — the bridge is hard-wired to 48 kHz / 16-bit
// signed / stereo PCM end-to-end. CHUNK_BYTES is what prism-media's Opus
// encoder pulls per Opus packet (frameSize * channels * 2 = 3840), so
// producing exactly that many bytes per _read keeps the encoder fed for
// one packet per call.
const FRAME_SAMPLES = 960; // 20 ms at 48 kHz, per channel
const CHANNELS = 2;
const CHUNK_BYTES = FRAME_SAMPLES * CHANNELS * 2;

// Caps to bound worst-case memory if a buggy plugin spams SpeakPcm. 64 voices
// × ~200 KB typical ≈ 13 MB; 32 MiB queued is the hard byte ceiling. Eviction
// is FIFO (drop oldest) — newest triggers are usually what the user cares
// about. We never evict the only voice in the queue, so a single oversized
// buffer is still played rather than silently muted.
const MAX_VOICES = 64;
const MAX_QUEUED_BYTES = 32 * 1024 * 1024;

interface Voice {
    pcm: Buffer;
    position: number;
    // Latency tracing (optional; only set when a caller passes meta). id is the
    // trigger's reqId so the firstEmit marker correlates with the host's
    // recv->enqueue line; enqueueT is a performance.now() stamp; emitted guards
    // the one-shot firstEmit log.
    id?: number;
    enqueueT?: number;
    emitted?: boolean;
    // Streaming voice (open until closeVoice). While true the voice survives
    // compaction even when fully drained, so a producer can appendToVoice more
    // audio after the mixer has caught up. A drained-but-open voice contributes
    // silence (the _mixOneChunk `remaining <= 0` skip) — the streaming underrun
    // behavior. open voices are also pinned against FIFO eviction mid-stream.
    open?: boolean;
    // Queued appends for a streaming voice, merged into pcm by _mixOneChunk when
    // the contiguous buffer drains. Keeps appendToVoice O(1) — no per-append
    // recopy of the unconsumed tail. Unset for one-shot voices.
    pending?: Buffer[];
}

export interface AddVoiceMeta {
    id: number;
    enqueueT: number;
}

export interface AddVoiceResult {
    dropped: number;
}

// Opaque handle to a streaming voice returned by openVoice. It is the Voice
// object itself; appendToVoice/closeVoice operate on it and no-op safely if the
// voice has already drained and been compacted out.
export type VoiceHandle = Voice;

// Sums any number of int16 PCM voices into a single 48k/16/stereo stream.
// Implements Readable.read on demand: every _read pushes exactly one 20 ms
// chunk, summed across active voices and clipped to int16 range. When no
// voices are active the chunk is silence — the player stays in Playing
// state continuously, which is how concurrent overlap is achieved (a single
// long-lived AudioResource fed by this stream).
//
// Never push(null): once playStream.readable goes false on an AudioResource
// it permanently ends and player.play() can't revive the same resource.
// The mixer's lifetime matches the AudioPlayer's; on leaveChannel both go
// away together.
export class PcmMixer extends Readable {
    private voices: Voice[] = [];
    private totalQueued = 0;
    private readonly acc = new Int32Array(FRAME_SAMPLES * CHANNELS);

    addVoice(pcm: Buffer, meta?: AddVoiceMeta): AddVoiceResult {
        // Defensive: a trailing odd byte would let readInt16LE walk one
        // byte past the end. Callers always emit aligned s16le, so this
        // only fires on a malformed upstream.
        const aligned = pcm.length & ~1;
        if (aligned === 0) return { dropped: 0 };
        const safe = aligned === pcm.length ? pcm : pcm.subarray(0, aligned);
        const voice: Voice = { pcm: safe, position: 0 };
        if (meta) { voice.id = meta.id; voice.enqueueT = meta.enqueueT; }
        this.voices.push(voice);
        this.totalQueued += safe.length;
        return { dropped: this._evictToFit() };
    }

    // Open a streaming voice with no audio yet; feed it with appendToVoice and
    // end it with closeVoice. Returns a handle (the Voice). Like addVoice it runs
    // eviction, but an open voice is never the victim (see _evictToFit).
    openVoice(meta?: AddVoiceMeta): VoiceHandle {
        const voice: Voice = { pcm: Buffer.alloc(0), position: 0, open: true };
        if (meta) { voice.id = meta.id; voice.enqueueT = meta.enqueueT; }
        this.voices.push(voice);
        this._evictToFit();
        return voice;
    }

    // Append more audio to an open streaming voice as an O(1) enqueue: the chunk
    // is stashed on a pending list and merged into the contiguous read buffer by
    // _mixOneChunk only when the current one drains, so each byte is copied once
    // (no per-append recopy of the unconsumed tail). The unconsumed byte count
    // rises by exactly the appended bytes, so totalQueued moves by safe.length and
    // nothing else. No-op if the handle has already drained + been removed.
    appendToVoice(handle: VoiceHandle, pcm: Buffer): AddVoiceResult {
        if (!this.voices.includes(handle)) return { dropped: 0 };
        const aligned = pcm.length & ~1;
        if (aligned === 0) return { dropped: 0 };
        const safe = aligned === pcm.length ? pcm : pcm.subarray(0, aligned);
        (handle.pending ??= []).push(safe);
        this.totalQueued += safe.length;
        return { dropped: this._evictToFit() };
    }

    // End a streaming voice. It now drains like any other and is compacted out
    // once fully played. No-op if already gone.
    closeVoice(handle: VoiceHandle): void {
        handle.open = false;
    }

    // Drop oldest non-open voices until under the caps. An open voice is pinned
    // (mid-stream — yanking it would cut off a callout), so it is never a victim;
    // with no open voices this picks index 0, matching the old FIFO behavior.
    private _evictToFit(): number {
        let dropped = 0;
        while (
            this.voices.length > 1 &&
            (this.voices.length > MAX_VOICES || this.totalQueued > MAX_QUEUED_BYTES)
        ) {
            const idx = this.voices.findIndex(v => !v.open);
            if (idx === -1) break; // all open; cannot evict
            const [victim] = this.voices.splice(idx, 1);
            this.totalQueued -= this._unconsumed(victim!);
            dropped++;
        }
        if (this.totalQueued < 0) this.totalQueued = 0;
        return dropped;
    }

    // Unconsumed bytes for a voice: the tail of the contiguous read buffer plus
    // any not-yet-merged pending appends.
    private _unconsumed(v: Voice): number {
        let n = v.pcm.length - v.position;
        if (v.pending) for (const b of v.pending) n += b.length;
        return n;
    }

    clear(): void {
        this.voices.length = 0;
        this.totalQueued = 0;
    }

    // Exposed for unit tests; not part of the AudioResource contract.
    get voiceCount(): number { return this.voices.length; }
    get queuedBytes(): number { return this.totalQueued; }

    // Exposed for unit tests so they can drive one chunk at a time without
    // wrestling with Readable buffering semantics.
    _mixOneChunk(): Buffer {
        if (this.voices.length === 0) return Buffer.alloc(CHUNK_BYTES);

        this.acc.fill(0);

        for (const v of this.voices) {
            // Fill up to one chunk from this voice, crossing pending-append
            // boundaries: when the contiguous buffer drains mid-chunk, merge the
            // queued appends in a single copy and keep going so a streamed voice
            // never underruns while it still has buffered audio.
            let need = FRAME_SAMPLES * CHANNELS; // samples still to fill for this voice
            let accIdx = 0;
            while (need > 0) {
                if (v.position >= v.pcm.length) {
                    if (!v.pending || v.pending.length === 0) break;
                    v.pcm = v.pending.length === 1 ? v.pending[0]! : Buffer.concat(v.pending);
                    delete v.pending;
                    v.position = 0;
                }
                const remainingSamples = (v.pcm.length - v.position) >>> 1;
                if (remainingSamples === 0) break;
                // First chunk this voice contributes to: the moment its audio
                // actually starts entering the encode/send path. enqueue->firstEmit
                // isolates mixer-side wait (event-loop starvation shows up here)
                // from the fixed downstream buffer + network that follow.
                if (!v.emitted) {
                    v.emitted = true;
                    if (v.enqueueT !== undefined) {
                        const waited = (performance.now() - v.enqueueT).toFixed(1);
                        log.info(`firstEmit reqId=${v.id} enqueue->firstEmit=${waited}ms`);
                    }
                }
                const n = remainingSamples < need ? remainingSamples : need;
                for (let i = 0; i < n; i++) {
                    this.acc[accIdx + i]! += v.pcm.readInt16LE(v.position + i * 2);
                }
                v.position += n * 2;
                this.totalQueued -= n * 2;
                accIdx += n;
                need -= n;
            }
        }

        // Compact in place: drop voices fully consumed this chunk. A still-open
        // streaming voice survives even when drained — it's awaiting more audio.
        let write = 0;
        for (let read = 0; read < this.voices.length; read++) {
            const v = this.voices[read]!;
            const hasPending = v.pending !== undefined && v.pending.length > 0;
            if (v.position < v.pcm.length || hasPending || v.open) {
                if (write !== read) this.voices[write] = v;
                write++;
            }
        }
        this.voices.length = write;

        const out = Buffer.allocUnsafe(CHUNK_BYTES);
        const total = FRAME_SAMPLES * CHANNELS;
        for (let i = 0; i < total; i++) {
            let s = this.acc[i]!;
            if (s > 32767) s = 32767;
            else if (s < -32768) s = -32768;
            out.writeInt16LE(s, i * 2);
        }
        return out;
    }

    override _read(): void {
        try {
            this.push(this._mixOneChunk());
        } catch (e) {
            // An erroring Readable would tear down the AudioResource via
            // pipeline error propagation. Swallow + emit silence so the
            // player keeps running.
            log.error('PcmMixer mix error', e);
            this.push(Buffer.alloc(CHUNK_BYTES));
        }
    }
}
