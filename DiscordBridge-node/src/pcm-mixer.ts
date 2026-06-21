import { Readable } from 'node:stream';
import { performance } from 'node:perf_hooks';

import * as log from './file-log.js';
import { floatToInt16 } from './audio-format.js';

// Audio format invariants — the bridge is hard-wired to 48 kHz / stereo end-to-
// end. Voices are interleaved float32 (the pipeline's internal currency); the
// mixer sums them in float64 and performs the pipeline's single int16
// quantization at its output, since CHUNK_BYTES (int16) is what prism-media's
// Opus encoder pulls per Opus packet (frameSize * channels * 2 = 3840) — one
// packet per _read keeps the encoder fed.
const FRAME_SAMPLES = 960; // 20 ms at 48 kHz, per channel
const CHANNELS = 2;
const CHUNK_SAMPLES = FRAME_SAMPLES * CHANNELS; // 1920 float samples per chunk
const CHUNK_BYTES = CHUNK_SAMPLES * 2;          // 3840 int16 output bytes
const BYTES_PER_SAMPLE = 4;                     // float32 interleaved

// Caps to bound worst-case memory if a buggy plugin spams SpeakPcm. 64 MiB of
// float audio (≈ the same ~5.5 min of 48k stereo the old 32 MiB int16 cap held)
// is the hard byte ceiling. Eviction is FIFO (drop oldest) — newest triggers are
// usually what the user cares about. We never evict the only voice in the queue,
// so a single oversized buffer is still played rather than silently muted.
const MAX_VOICES = 64;
const MAX_QUEUED_BYTES = 64 * 1024 * 1024;

interface Voice {
    samples: Float32Array;
    position: number; // next sample index to read
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
    // Queued appends for a streaming voice, merged into samples by _mixOneChunk
    // when the contiguous buffer drains. Keeps appendToVoice O(1) — no per-append
    // recopy of the unconsumed tail. Unset for one-shot voices.
    pending?: Float32Array[];
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

// Sums any number of interleaved float32 voices into a single 48k/stereo stream,
// quantized to s16le at the output. Implements Readable.read on demand: every
// _read pushes exactly one 20 ms chunk, summed across active voices in float64
// and converted to int16 (the single clamp). When no voices are active the chunk
// is silence — the player stays in Playing state continuously, which is how
// concurrent overlap is achieved (a single long-lived AudioResource fed by this
// stream).
//
// Never push(null): once playStream.readable goes false on an AudioResource
// it permanently ends and player.play() can't revive the same resource.
// The mixer's lifetime matches the AudioPlayer's; on leaveChannel both go
// away together.
export class PcmMixer extends Readable {
    private voices: Voice[] = [];
    private totalQueued = 0; // float bytes of unconsumed audio across all voices
    private readonly acc = new Float64Array(CHUNK_SAMPLES);

    addVoice(samples: Float32Array, meta?: AddVoiceMeta): AddVoiceResult {
        // Defensive: an odd sample count would leave a dangling mono sample.
        // Callers always emit whole stereo frames, so this only fires on a
        // malformed upstream.
        const aligned = samples.length & ~1;
        if (aligned === 0) return { dropped: 0 };
        const safe = aligned === samples.length ? samples : samples.subarray(0, aligned);
        const voice: Voice = { samples: safe, position: 0 };
        if (meta) { voice.id = meta.id; voice.enqueueT = meta.enqueueT; }
        this.voices.push(voice);
        this.totalQueued += safe.length * BYTES_PER_SAMPLE;
        return { dropped: this._evictToFit() };
    }

    // Open a streaming voice with no audio yet; feed it with appendToVoice and
    // end it with closeVoice. Returns a handle (the Voice). Like addVoice it runs
    // eviction, but an open voice is never the victim (see _evictToFit).
    openVoice(meta?: AddVoiceMeta): VoiceHandle {
        const voice: Voice = { samples: new Float32Array(0), position: 0, open: true };
        if (meta) { voice.id = meta.id; voice.enqueueT = meta.enqueueT; }
        this.voices.push(voice);
        this._evictToFit();
        return voice;
    }

    // Append more audio to an open streaming voice as an O(1) enqueue: the chunk
    // is stashed on a pending list and merged into the contiguous read buffer by
    // _mixOneChunk only when the current one drains, so each sample is copied once
    // (no per-append recopy of the unconsumed tail). The unconsumed sample count
    // rises by exactly the appended samples, so totalQueued moves by safe.length*4
    // and nothing else. No-op if the handle has already drained + been removed.
    appendToVoice(handle: VoiceHandle, samples: Float32Array): AddVoiceResult {
        if (!this.voices.includes(handle)) return { dropped: 0 };
        const aligned = samples.length & ~1;
        if (aligned === 0) return { dropped: 0 };
        const safe = aligned === samples.length ? samples : samples.subarray(0, aligned);
        (handle.pending ??= []).push(safe);
        this.totalQueued += safe.length * BYTES_PER_SAMPLE;
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

    // Unconsumed float bytes for a voice: the tail of the contiguous read buffer
    // plus any not-yet-merged pending appends.
    private _unconsumed(v: Voice): number {
        let n = v.samples.length - v.position;
        if (v.pending) for (const b of v.pending) n += b.length;
        return n * BYTES_PER_SAMPLE;
    }

    clear(): void {
        this.voices.length = 0;
        this.totalQueued = 0;
    }

    // Exposed for unit tests; not part of the AudioResource contract.
    get voiceCount(): number { return this.voices.length; }
    get queuedBytes(): number { return this.totalQueued; }

    // Exposed for unit tests so they can drive one chunk at a time without
    // wrestling with Readable buffering semantics. Returns one s16le chunk.
    _mixOneChunk(): Buffer {
        if (this.voices.length === 0) return Buffer.alloc(CHUNK_BYTES);

        this.acc.fill(0);

        for (const v of this.voices) {
            // Fill up to one chunk from this voice, crossing pending-append
            // boundaries: when the contiguous buffer drains mid-chunk, merge the
            // queued appends in a single copy and keep going so a streamed voice
            // never underruns while it still has buffered audio.
            let need = CHUNK_SAMPLES; // samples still to fill for this voice
            let accIdx = 0;
            while (need > 0) {
                if (v.position >= v.samples.length) {
                    if (!v.pending || v.pending.length === 0) break;
                    v.samples = v.pending.length === 1 ? v.pending[0]! : concatF32(v.pending);
                    delete v.pending;
                    v.position = 0;
                }
                const remainingSamples = v.samples.length - v.position;
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
                    this.acc[accIdx + i]! += v.samples[v.position + i]!;
                }
                v.position += n;
                this.totalQueued -= n * BYTES_PER_SAMPLE;
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
            if (v.position < v.samples.length || hasPending || v.open) {
                if (write !== read) this.voices[write] = v;
                write++;
            }
        }
        this.voices.length = write;

        // The pipeline's single int16 quantization: sum bus (float64) -> s16le.
        return floatToInt16(this.acc, Buffer.allocUnsafe(CHUNK_BYTES));
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

function concatF32(parts: Float32Array[]): Float32Array {
    let total = 0;
    for (const p of parts) total += p.length;
    const out = new Float32Array(total);
    let off = 0;
    for (const p of parts) { out.set(p, off); off += p.length; }
    return out;
}
