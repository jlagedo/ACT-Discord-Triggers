// Master look-ahead brickwall limiter for the mixer bus. PcmMixer sums any
// number of voices in float64 and then quantizes to int16 once (floatToInt16).
// Per-clip normalize bounds each clip on its own, but N overlapping clips sum
// past full scale, so without a bus stage the summed signal hard-clips at the
// int16 edge — the harshest artifact in the chain, exactly during overlap. This
// catches the summed peaks and rides them down to a true-peak-safe ceiling
// smoothly instead.
//
// Look-ahead brickwall, channel-linked, operating on the interleaved float64
// stereo bus in place. It is stateful across the mixer's 20 ms chunks (the mixer
// pulls one chunk per _read), so the delay line, the detector window and the
// gain envelope all persist between process() calls.
//
// Why look-ahead: the output is delayed by LOOKAHEAD_FRAMES, and the detector is
// a running MINIMUM of the required gain over that same window, so the gain is
// already pulled down by the time a peak reaches the output — the attack is
// smooth and no transient slips through. The ~2 ms of added latency is
// negligible next to Discord voice RTT and the fixed 20 ms Opus framing.
//
// True-peak is handled by HEADROOM, not oversampled detection: the bus feeds a
// lossy Opus encoder that reshapes peaks downstream, so an exact inter-sample
// guarantee at the encoder input would be undone by the codec anyway. Callers
// pass a ceiling a touch under 0 dBFS (e.g. -1 dBTP ≈ 0.891) and that margin
// absorbs typical inter-sample overshoot at zero cost. floatToInt16's hard clamp
// remains the absolute backstop for float epsilon / a disabled limiter.
//
// This module is pure DSP: it takes a LINEAR ceiling and knows nothing about dB
// or config (discord-host maps the configured ceiling index -> dB -> linear).

const SR = 48000; // bridge is hard-wired to 48 kHz (see CLAUDE.md)

// Look-ahead window in stereo frames. Doubles as the attack window: a linear
// attack of 1/LOOKAHEAD_FRAMES per frame can traverse the full [0,1] gain range
// within the window, so the envelope always reaches the needed reduction before
// the peak it saw arrives at the output.
export const LOOKAHEAD_FRAMES = 96; // 2 ms at 48 kHz

// Release time: how fast the gain recovers after a peak passes. Long enough to
// avoid pumping on speech/SFX, short enough that one loud transient doesn't duck
// the following audio for an audible stretch.
const RELEASE_MS = 100;

export class LookaheadLimiter {
    private ceiling: number;
    private readonly attackStep = 1 / LOOKAHEAD_FRAMES;
    private readonly releaseCoef = Math.exp(-1 / (SR * (RELEASE_MS / 1000)));

    // Interleaved stereo delay ring of LOOKAHEAD_FRAMES frames. Output is the
    // frame written LOOKAHEAD_FRAMES ago; the first window's worth of output is
    // the zero-init silence (~2 ms) before real audio emerges.
    private readonly delay = new Float64Array(LOOKAHEAD_FRAMES * 2);
    private writePos = 0; // frame slot to read-then-overwrite next

    // Smoothed gain currently applied (1 = unity, ≤ 1 = reducing).
    private gain = 1;

    // Monotonic deque (values non-decreasing front→back) over the per-frame
    // target gains in the look-ahead window; the front is the window minimum.
    // Stored in fixed circular arrays keyed by absolute frame position so the
    // running min is O(1) amortized with no per-frame allocation.
    // Window holds at most LOOKAHEAD_FRAMES+1 entries (positions p-L..p); +4 is a
    // small safety margin over the transient pre-expiry size.
    private readonly dqCap = LOOKAHEAD_FRAMES + 4;
    private readonly dqVal = new Float64Array(LOOKAHEAD_FRAMES + 4);
    private readonly dqPos = new Float64Array(LOOKAHEAD_FRAMES + 4);
    private dqHead = 0;
    private dqSize = 0;
    private pos = 0; // absolute frame counter (JS number, exact past any session)

    constructor(ceilingLinear = 1) {
        this.ceiling = ceilingLinear;
    }

    // Update the ceiling (linear, 1.0 == 0 dBFS) without disturbing the running
    // state — a config change mid-stream just retargets the detector.
    setCeiling(ceilingLinear: number): void {
        this.ceiling = ceilingLinear;
    }

    // Drop all state: delay line, detector window and envelope. Called when the
    // limiter is (re)enabled so a fresh stream starts from unity + silence.
    reset(): void {
        this.delay.fill(0);
        this.writePos = 0;
        this.gain = 1;
        this.dqHead = 0;
        this.dqSize = 0;
        this.pos = 0;
    }

    // Limit one interleaved float64 stereo chunk in place. Channel-linked: the
    // same gain hits L and R so the stereo image never shifts. Output is the
    // input delayed by LOOKAHEAD_FRAMES with the brickwall gain applied.
    process(acc: Float64Array): void {
        const frames = acc.length >>> 1;
        const ceiling = this.ceiling;
        const L = LOOKAHEAD_FRAMES;
        for (let i = 0; i < frames; i++) {
            const xL = acc[i * 2]!;
            const xR = acc[i * 2 + 1]!;
            const absL = xL < 0 ? -xL : xL;
            const absR = xR < 0 ? -xR : xR;
            const peak = absL > absR ? absL : absR;
            const target = peak > ceiling ? ceiling / peak : 1;
            const p = this.pos;

            // Push target into the monotonic min-deque: pop larger tails, then
            // expire any front older than the look-ahead window. Keeping
            // positions ≥ p-L means the frame about to be output (position p-L)
            // is still in the window, so its own target bounds the gain → no
            // overshoot on the output sample itself.
            this._pushTarget(p, target);
            const cutoff = p - L;
            while (this.dqSize > 0 && this.dqPos[this.dqHead]! < cutoff) {
                this.dqHead = (this.dqHead + 1) % this.dqCap;
                this.dqSize--;
            }
            const windowMin = this.dqVal[this.dqHead]!;

            // Linear attack (reaches any target within L frames, so the envelope
            // is in place before the peak it saw reaches the output), exponential
            // release (smooth recovery, no pumping).
            let g = this.gain;
            if (windowMin < g) {
                g -= this.attackStep;
                if (g < windowMin) g = windowMin;
            } else {
                g = windowMin + (g - windowMin) * this.releaseCoef;
            }
            this.gain = g;

            // Read the delayed (output) frame, store the current input in its
            // slot, then write the gained output back into the bus.
            const slot = this.writePos * 2;
            const outL = this.delay[slot]!;
            const outR = this.delay[slot + 1]!;
            this.delay[slot] = xL;
            this.delay[slot + 1] = xR;
            this.writePos = this.writePos + 1 < L ? this.writePos + 1 : 0;

            acc[i * 2] = outL * g;
            acc[i * 2 + 1] = outR * g;
            this.pos = p + 1;
        }
    }

    // Append (pos, val) to the monotonic deque, discarding tail entries no
    // smaller than val (they can never be the window minimum again).
    private _pushTarget(pos: number, val: number): void {
        while (this.dqSize > 0) {
            const backIdx = (this.dqHead + this.dqSize - 1) % this.dqCap;
            if (this.dqVal[backIdx]! >= val) this.dqSize--;
            else break;
        }
        const writeIdx = (this.dqHead + this.dqSize) % this.dqCap;
        this.dqVal[writeIdx] = val;
        this.dqPos[writeIdx] = pos;
        this.dqSize++;
    }
}
