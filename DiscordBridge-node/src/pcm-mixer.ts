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

// Tick jitter window. _read is called by the Opus encoder every 20 ms when
// playing; a window of 50 covers ~1 s of audio, which is short enough that
// short stalls show up clearly without a long stall hiding in the average.
const TICK_WINDOW = 50;
// Threshold for the "over budget" counter: a single _read taking longer
// than 30 ms means at least one Opus frame was late, which is audible.
const TICK_OVER_BUDGET_MS = 30;

interface Voice {
    reqId: number;
    pcm: Buffer;
    position: number;
    enqueueTs: number;
}

export interface AddVoiceResult {
    dropped: number;
}

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

    // Tick jitter tracking. lastReadTs is captured from performance.now() at
    // the end of every _read so the first sample after a quiet period
    // doesn't include the silence-to-playing transition delay.
    private lastReadTs = 0;
    private readonly tickSamples: number[] = [];

    addVoice(reqId: number, pcm: Buffer): AddVoiceResult {
        // Defensive: a trailing odd byte would let readInt16LE walk one
        // byte past the end. Callers always emit aligned s16le, so this
        // only fires on a malformed upstream.
        const aligned = pcm.length & ~1;
        if (aligned === 0) return { dropped: 0 };
        const safe = aligned === pcm.length ? pcm : pcm.subarray(0, aligned);
        this.voices.push({
            reqId,
            pcm: safe,
            position: 0,
            enqueueTs: performance.now(),
        });
        this.totalQueued += safe.length;

        let dropped = 0;
        while (
            this.voices.length > 1 &&
            (this.voices.length > MAX_VOICES || this.totalQueued > MAX_QUEUED_BYTES)
        ) {
            const oldest = this.voices.shift()!;
            this.totalQueued -= (oldest.pcm.length - oldest.position);
            // Per-voice drop log so we can tell which trigger got starved
            // out by a flood of newer ones.
            log.warn(
                `mixer drop reqId=${oldest.reqId} ` +
                `unplayedBytes=${oldest.pcm.length - oldest.position} ` +
                `ageMs=${(performance.now() - oldest.enqueueTs).toFixed(1)}`,
            );
            dropped++;
        }
        return { dropped };
    }

    clear(): void {
        this.voices.length = 0;
        this.totalQueued = 0;
        this.lastReadTs = 0;
        this.tickSamples.length = 0;
    }

    // Exposed for unit tests; not part of the AudioResource contract.
    get voiceCount(): number { return this.voices.length; }
    get queuedBytes(): number { return this.totalQueued; }

    // Exposed for unit tests so they can drive one chunk at a time without
    // wrestling with Readable buffering semantics.
    _mixOneChunk(): Buffer {
        if (this.voices.length === 0) return Buffer.alloc(CHUNK_BYTES);

        this.acc.fill(0);

        const now = performance.now();
        for (const v of this.voices) {
            const remaining = v.pcm.length - v.position;
            if (remaining <= 0) continue;
            // First time any byte of this voice is consumed, log how long
            // it sat in the queue. This is the bridge-side "audible delay"
            // for a trigger: time from addVoice() to first opus frame.
            if (v.position === 0) {
                log.debug(
                    `mixer voice reqId=${v.reqId} ` +
                    `firstChunk=${(now - v.enqueueTs).toFixed(1)}ms ` +
                    `pcmBytes=${v.pcm.length}`,
                );
            }
            const take = remaining < CHUNK_BYTES ? remaining : CHUNK_BYTES;
            const samples = take >>> 1;
            for (let i = 0; i < samples; i++) {
                this.acc[i]! += v.pcm.readInt16LE(v.position + i * 2);
            }
            v.position += take;
            this.totalQueued -= take;
        }

        // Compact in place: drop voices fully consumed this chunk.
        let write = 0;
        for (let read = 0; read < this.voices.length; read++) {
            const v = this.voices[read]!;
            if (v.position < v.pcm.length) {
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

    override _read(_size: number): void {
        const now = performance.now();
        // First _read after a silent period (lastReadTs unset, or gap > 1s)
        // doesn't represent encoder cadence — the encoder isn't pulling
        // when there's no resource demand. Skip those samples so the
        // jitter window only measures real playback.
        if (this.lastReadTs > 0 && now - this.lastReadTs <= 1000) {
            const dt = now - this.lastReadTs;
            this.tickSamples.push(dt);
            if (this.tickSamples.length >= TICK_WINDOW) {
                let min = this.tickSamples[0]!;
                let max = this.tickSamples[0]!;
                let sum = 0;
                let over = 0;
                for (const s of this.tickSamples) {
                    if (s < min) min = s;
                    if (s > max) max = s;
                    sum += s;
                    if (s > TICK_OVER_BUDGET_MS) over++;
                }
                log.debug(
                    `mixer tick dtMs ` +
                    `min=${min.toFixed(1)} ` +
                    `avg=${(sum / this.tickSamples.length).toFixed(1)} ` +
                    `max=${max.toFixed(1)} ` +
                    `over${TICK_OVER_BUDGET_MS}=${over} ` +
                    `voices=${this.voices.length} ` +
                    `queued=${this.totalQueued}`,
                );
                this.tickSamples.length = 0;
            }
        }
        try {
            this.push(this._mixOneChunk());
        } catch (e) {
            // An erroring Readable would tear down the AudioResource via
            // pipeline error propagation. Swallow + emit silence so the
            // player keeps running.
            log.error('PcmMixer mix error', e);
            this.push(Buffer.alloc(CHUNK_BYTES));
        }
        this.lastReadTs = performance.now();
    }
}
