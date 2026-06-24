# Audio Pipeline — Current State & Pro-Audio Roadmap

How the bridge's audio pipeline works today, and the ranked work left to take it
from "clean" to genuinely pro-grade for the realtime path.

This is a **node-bridge-only** document. The C# side just hands the bridge PCM
(`SpeakPcm`), a file path (`SpeakFile`), or text (`SpeakText`); the bridge owns
every decode/DSP/mix/encode decision below.

Legend — **Effort**: S (days) · M (1–2 weeks) · L (multi-week / architectural).
**Status**: 🔵 planned · 🟡 in progress · 🟢 done · ⚪ idea.

---

## Format invariants (hard-wired)

Discord voice is 48 kHz / stereo end-to-end; the Opus encoder input is 16-bit
signed PCM. That pins the sample rate / channel count in several places — change
all of them together, never add a one-off conversion:

- `DiscordClient.formatInfo` (C# TTS synthesis format, 48k/16/stereo).
- the `48000/16/2` in `DiscordClient`'s PCM sends + the matching check in
  `pipe-server.ts`.
- `discord-host.ts` — `TARGET_SAMPLE_RATE` + `StreamType.Raw`; the 48k resample
  target in `resample.ts`.
- `effects.ts` `SR`.

**Sample format:** the interior pipeline (decode → resample → fx → normalize →
declick → mix → master limiter) is **interleaved float32 stereo**, nominal [-1, 1]
but allowed to exceed it *between* stages so headroom is preserved (an fx tail or a
hot mix sum is pulled back down later, not clipped mid-chain). int16 lives only at
the two edges, both in `audio-format.ts`:

- **ingest:** the `SpeakPcm` wire frame arrives s16le and is widened once
  (`int16ToFloat32`).
- **exit:** `PcmMixer` sums voices in float64, the master limiter rides the bus to
  a true-peak ceiling, then the pipeline's single int16 quantization happens at its
  output (`floatToInt16`) right before the Opus encoder.

`floatToInt16` is the single quantization point (round + hard clamp, retained as
the limiter's backstop). Don't reintroduce per-stage int16 round-trips.

---

## Current signal chain

```
ingest                                    per-clip (_enqueue)           mix bus                    encode
────────────────────────────────────      ─────────────────────────     ──────────────────────     ──────
SpeakPcm  s16→f32 ─────────────────┐
SpeakFile decode→resample→condition├──▶ FX → normalize → declick ──▶  PcmMixer sum → limiter  ──▶ Opus
SpeakText ONNX→resample────────────┘             (LUFS)    (lin fade)   (float64) → s16            (bitrate)
                          ▲
                   file path only (untrusted source)
```

**Ingest → 48k float stereo**
- `SpeakPcm`: `int16ToFloat32` at the wire edge (`discord-host.ts` `speakPcm`).
- `SpeakFile`: `audio-decode` (WASM, auto-detects wav/mp3/ogg/flac/opus/m4a/…) →
  `planarFloatToInterleavedStereoF32` downmix → `resampleStereoF32` (r8brain, true
  stereo) to 48k → **`conditionSource`** (`source-conditioning.ts`: sanitize →
  DC-block → trim silence → edge-fade — **file path only**, since user files are
  untrusted). The conditioned buffer is cached by path+mtime (`WavCache`), so
  decode + resample + condition are paid once per file on first fire.
- `SpeakText` (ONNX/sherpa): mono float → resampled to 48k → `monoFloat32ToStereoF32`.
  Streams chunk-by-chunk through `MonoStreamResampler` (r8brain, cross-chunk
  continuous) so audio starts before synthesis finishes; baked per-voice loudness
  folds a fixed gain into the mono→stereo step so leveling needs no whole-buffer
  scan. (The resampler runs on mono — half the work of two identical channels.)

**Per-clip processing** (`_enqueue`, off the realtime loop, once per fire):
- **Random FX** (`effects.ts`) — opt-in, dice-rolled per clip; applied to
  `SpeakPcm`/`SpeakFile` only (TTS is skipped). Output stays float, may run hot.
- **Normalize** (`normalize.ts`) — per-clip K-weighted (BS.1770 LUFS) auto-level
  toward a LUFS target, bounded by a broadband peak ceiling (no clip) and a
  max-boost cap (don't amplify silence). Runs *after* FX so the effect's own level
  change is what gets corrected.
- **Declick** (`declick.ts`) — short linear fade in (2 ms) / out (5 ms) so edge
  samples ramp from/to zero instead of stepping against the mixer's silence.

**Realtime mix** (`pcm-mixer.ts` `_read`, every 20 ms):
- Sum all active voices into a float64 accumulator; the **master limiter**
  (`limiter.ts`, see Roadmap P1.2) rides the summed bus to a true-peak ceiling;
  quantize once via `floatToInt16` (round + hard clamp, now the limiter's backstop
  rather than the only ceiling). Pull-based: every `_read` emits exactly one 20 ms
  chunk, silence when idle — the player never ends, which is how concurrent callouts
  overlap (each opens its own mixer voice). FIFO eviction caps worst-case memory;
  open streaming voices are pinned against eviction.

**Encode**
- One long-lived `AudioResource` (`StreamType.Raw`) fed by the mixer; prism's Opus
  encoder. Only **bitrate** is set, from the quality tier (Low/Med/High →
  48k/96k/128k, `audio-quality.ts`). Signal type and complexity are not reachable
  through prism's public API (see Roadmap P3).

---

## Good practices already in place

- **One float currency, single quantization point.** No per-stage int16 round-trips.
- **Headroom preserved between stages.** Hot fx tails / mix sums are pulled back,
  not clipped mid-chain.
- **float64 mix accumulator.** Summation precision loss is a non-issue.
- **Cross-chunk-continuous streaming resampler.** Concatenated streamed output is
  sample-identical to resampling the whole utterance at once — no per-chunk seam click.
- **Declick edges.** No onset/tail clicks from stepping against digital silence.
- **Source conditioning on file ingest.** Untrusted user files are sanitized
  (NaN/Inf→0), DC-blocked (~19 Hz HPF), silence-trimmed and edge-faded once at
  ingest, so a length-extending FX tail can't relocate a hot/junk source edge into
  the buffer interior and fire as a "gunshot" pop (`source-conditioning.ts`).
- **Pull-based mixer that degrades to silence.** A mix error or underrun emits a
  silent frame instead of tearing down the resource.
- **Master look-ahead limiter on the bus.** Overlapping voices that sum past full
  scale are ridden down to a true-peak ceiling instead of hard-clipping (P1.2).
- **Off-thread synth.** sherpa `generateAsync` runs on a libuv worker; the 20 ms
  frame delivery and Discord keepalives keep running during synthesis.
- **Decode/resample cache** keyed by path+mtime; edits invalidate naturally.

---

## Roadmap — ranked by perceived-quality impact

The architecture is sound; the items below are about the **DSP quality** inside it.

### P1 — biggest wins

#### 1. High-quality resampler 🟢 — Effort: M
**Linear interpolation replaced with a polyphase / windowed-sinc resampler.**

The former `resampleStereoF32` and `StreamingResampler` were both linear
interpolation, sitting in the path of **every** file and **all** TTS (Piper
22.05k→48k, Kokoro 24k→48k — ~2× upsampling on every callout):

- **Upsampling:** linear interp is a weak triangular lowpass — dulls the top
  octave *and* leaves spectral images above the source Nyquist (≈11 kHz for
  Piper). TTS ended up both muffled and slightly grainy.
- **Downsampling** (96k/88.2k sources): no anti-alias pre-filter → HF folds back
  as aliasing.
- 44.1k→48k (the common MP3 ratio) is linear interp's worst non-integer case.

This was the single change that most audibly lifts quality.

**Library: `r8brain-wasm`** — r8brain-free-src (Aleksey Vaneev / Voxengo, MIT)
compiled to WebAssembly, vendored at `github.com/jlagedo/r8brain-wasm` (pinned by
commit SHA in `package.json`; `dist/` is committed, so no build step). Validated:
SINAD **184–238 dB** (vs linear's ~9–30), streaming output sample-identical to a
whole-buffer resample, 500–2000× realtime, ~18 ms first-output latency at the
streaming settings. All resampling lives in **`src/resample.ts`**:

- `resampleStereoF32(samples, src, dst)` — file path; **true stereo** (one
  `Resampler` per channel, since file L≠R), `transBand 2.0` (full passband;
  latency irrelevant — files are decoded/resampled once and cached).
- `resampleMono(mono, src, dst)` — buffered TTS / probe; one `Resampler`,
  `transBand 2.0`. The mono synth output is resampled, then duplicated to stereo
  (half the work of converting two identical channels).
- `MonoStreamResampler` — streaming TTS; `transBand 6.0` (shorter filter → ~18 ms
  first-output latency, well inside the send buffer; speech sits inside the
  resulting ~10 kHz+ passband). r8brain keeps cross-chunk continuity internally,
  so `push()`/`flush()` concatenated output is sample-exact. `flush()` drains the
  latency tail to exactly `floor(totalIn · ratio)` samples (the length contract
  the duration math + WAV sink depend on).

`R24` resolution throughout (136 dB+ headroom over the 16-bit Opus sink). r8brain
is mono-per-object and works in float64, so `resample.ts` owns the
de-interleave / channel-split and the float32↔float64 conversion at its edges; the
pipeline currency stays interleaved float32 stereo. `initResampler()` instantiates
the WASM module once at bridge startup (alongside `warmupDecoders`, before
`BRIDGE_READY`), so the sync resample functions can assume it's loaded.

*Rejected:* `@alexanderolsen/libsamplerate-js` (its wrapper hardwires
`end_of_input=0`, so the streaming tail never flushes), `node-soxr` (native +
LGPL, breaks no-native/single-`node.exe`), `wasm-audio-resampler` (dormant).

#### 2. Master limiter on the mix bus 🟢 — Effort: M
**Look-ahead brickwall limiter on the float64 bus before `floatToInt16`.**

The mixer summed voices straight, with the hard clamp in `floatToInt16` as the only
ceiling. Each voice normalizes up to a ~0.97 peak, so **two overlapping callouts
summed to ~1.94 → brick-wall clip.** Overlap is a designed feature (each callout
opens its own voice), so this was the normal multi-trigger case, not an edge.

**`limiter.ts` `LookaheadLimiter`** rides the summed bus to a true-peak-safe ceiling
before quantization (the `floatToInt16` clamp stays as the backstop):

- **Look-ahead brickwall, channel-linked.** ~2 ms look-ahead delay
  (`LOOKAHEAD_FRAMES = 96`) carried across the 20 ms chunks; the same gain hits L+R
  so the stereo image never shifts. The detector is a running **minimum** of the
  required gain over the look-ahead window (monotonic deque, O(1) amortized), so the
  gain is already down before a peak reaches the output — the attack is smooth and
  no transient slips through (overshoot-free, verified in the e2e harness).
- **Linear attack** (reaches any target within the window) + **exponential release**
  (~100 ms, no pumping). Silence recovers to unity.
- **Bypassed by default** on a bare `PcmMixer` (a look-ahead limiter delays even at
  unity, so an unconfigured mixer stays a pure, delay-free summing bus — the
  sample-exact mix-math tests are unaffected). `discord-host` arms it from the live
  config on join and on every change via `PcmMixer.configureLimiter`.
- **Configurable + user-facing.** `limiterEnabled` + `limiterCeilingIndex` (0..3 →
  `LIMITER_CEILINGS_DB` `[-0.5,-1,-2,-3]` dBTP, default index 1 = −1 dBTP) ride the
  existing `SetConfig` POCO (additive, no `PROTOCOL_VERSION` bump) with an enable
  toggle + ceiling dropdown in the WPF Sound tab. **Independent of `normalize`** —
  it catches inter-voice sum clipping even with normalize off.
- **True-peak via headroom** (folds in P2.4), not oversampled detection: the bus
  feeds lossy Opus, which reshapes peaks downstream, so an exact inter-sample
  guarantee at the encoder input would be undone by the codec; the −1 dBTP default
  ceiling leaves margin to absorb inter-sample overshoot at zero cost.

Verify end-to-end with **`npm run limiter:e2e`** (drives the real mixer with
overlapping / DC / Nyquist / transient signals, writes WAVs, asserts zero clipping
+ the ceiling held). Unit coverage in `tests/limiter.test.ts` + `tests/pcm-mixer.test.ts`.

### P2 — pro-level correctness

#### 3. K-weighted (LUFS) loudness 🟢 — Effort: S–M
**Measure loudness with ITU-R BS.1770 K-weighting instead of broadband RMS.**

Done. `k-weighting.ts` implements the BS.1770-4 cascade (a high-shelf "head"
filter + a ~38 Hz RLB high-pass biquad per channel at 48 kHz, then a
channel-summed mean square with the −0.691 LUFS offset, ungated — the right fit
for short per-clip leveling). `measureLevel` (`normalize.ts`) now returns that
K-weighted loudness as a linear full-scale-equivalent (its dB value is LUFS) in
the `rms` field, with `peak` kept as the unchanged broadband sample peak; so
`computeGain`, the silence gate, and the streaming fixed-gain path are untouched —
`dbToLinear(target)/rms` lands the clip on a LUFS target directly. Bass-heavy SFX
and speech callouts that read equal now *sound* equal.

The target is user-facing LUFS: the auto-level slider is labeled LUFS, the default
re-tuned to −17 LUFS (≈ the old −20 dBFS for speech), and a `PluginSettings`
v2→v3 migration shifts existing saved targets down by the calibration offset so an
upgrade keeps the same speech level while non-speech clips get correctly
re-weighted. Per-voice neural-TTS levels are re-baked under the new metric via
`npm run tts:rms -- --bake` (only `rmsDbfs` shifts; `peakDbfs` is
metric-independent). Coverage: `tests/k-weighting.test.ts` (tonal invariants — LF
reads quieter, HF louder, 1 kHz at the calibration point) + the retuned
`tests/normalize.test.ts`; C# `Migrator_V2ToV3_*` tests.

#### 4. True-peak ceiling 🟢 — Effort: S (with P1.2)
**Headroom-based inter-sample-peak margin on the bus limiter.**

Done as part of P1.2, via headroom rather than oversampled detection. The bus
limiter's ceiling defaults to **−1 dBTP** (configurable −0.5/−1/−2/−3 via
`limiterCeilingIndex`), leaving margin for inter-sample peaks that emerge after Opus
decode + the listener's DAC reconstruction. Oversampled true-peak detection
(BS.1770-4, 4×) was deliberately **not** done: the bus feeds lossy Opus, which
reshapes peaks downstream, so an exact inter-sample guarantee at the encoder input
would be undone by the codec — the headroom margin captures the protection at zero
CPU cost. (Per-clip `normalize` still uses `PEAK_CEILING = 0.97` as a clip-safe
boost bound; the bus limiter is the true-peak stage.)

#### 5. Anti-alias the nonlinear effects 🔵 — Effort: M
**Oversample `distortion` and `pitch`.**

`distortion` (tanh waveshaper, `effects.ts`) and `pitch` (linear-resample) both
generate content above Nyquist that folds back as inharmonic aliasing. For a "pro"
FX catalog, oversample the nonlinearity 4–8× → process → band-limit down. Prefer
**local** oversampling around the nonlinear effect over a global rate change. Lower
priority — FX are opt-in novelty — but the difference between lo-fi on purpose and
lo-fi by accident. `pitch` can route through the P1.1 sinc resampler instead.

### P3 — polish

- **6. Equal-power declick fade** 🔵 — Effort: S. A linear ramp has a slope
  discontinuity at the ramp ends that can still tick faintly. A raised-cosine /
  half-Hann ramp is smoother for the same 2/5 ms — a one-line gain-formula change.
- **7. DC-blocking high-pass on ingest** 🟢 — done (`source-conditioning.ts`). A
  1-pole HPF (`R=0.9975`, ~19 Hz) runs as part of file-ingest source conditioning —
  removes DC bias / subsonic rumble that waste headroom and click on edges. Bundled
  with sanitize + silence-trim + edge-fade; the `SpeakPcm`/TTS paths (controlled
  producers) skip it.
- **8. Proper >2-channel fold-down** ⚪ — Effort: S. `planarFloatToInterleavedStereoF32`
  keeps the first two channels, so a 5.1 source loses its center (dialogue). A
  Lo/Ro matrix (`L + 0.707·C + 0.707·Ls`…) is correct; rare for ACT sounds.
- **9. Opus encoder tuning** ⚪ — Effort: S. If reachable, set signal type = VOICE
  and complexity = 10 for TTS-dominated output. Currently bitrate-only: prism
  exposes only `setBitrate` publicly; signal/complexity need opusscript internals.
  Re-check against the current prism surface — free quality if it ever opens up.

### Explicitly not planned

- **TPDF dither before Opus.** 16-bit quantization noise (~−96 dBFS) sits far
  below the noise Opus immediately adds at 48–128 kbps; dithering before a lossy
  codec spends effort on noise the codec masks. The straight round + clamp in
  `floatToInt16` is correct — the *limiter* sits before it (P1.2, done), not dither.
- **Global oversampling (96 kHz internal).** Only `distortion` clearly benefits,
  sources are ≤22 kHz band-limited, and the sink is lossy 48 kHz Opus. If ever
  revisited, prefer local oversampling around the nonlinear effect (P2.5).

---

## Suggested sequencing

1. **High-quality resampler** (P1.1) 🟢 — touches every callout, biggest single
   win; done. Lives in `resample.ts`: `resampleStereoF32` (file, true stereo) +
   `MonoStreamResampler` (streaming TTS, mono).
2. **Master limiter** (P1.2) 🟢 + **true-peak ceiling** (P2.4) 🟢 — self-contained,
   kills overlap clipping; done in `limiter.ts`, true-peak folded in via the −1 dBTP
   default ceiling.
3. **K-weighted loudness** (P2.3) 🟢 — done in `k-weighting.ts` + `normalize.ts`;
   target relabeled LUFS, voice catalog re-baked via `tts:rms -- --bake`.
4. **DC-block** (P3.7) 🟢 — done as part of file-ingest source conditioning
   (`source-conditioning.ts`). **Cosine declick** (P3.6) — still open; cheap polish.
5. **FX anti-aliasing / Opus CTLs** (P2.5 / P3.9) as time allows.

P1.1 + P1.2 + P2.3 together moved the realtime path from clean-amateur to pro-grade.

---

## Risk register

| Risk | Item | Mitigation |
|------|------|-----------|
| Heavy per-fire DSP blocks the single thread → mixer underrun | P1.1 | r8brain runs 500–2000× realtime; resample at enqueue not `_read`; `worker_thread` relief valve |
| Limiter adds latency to the realtime path | P1.2 🟢 | Resolved: ~2 ms look-ahead (`LOOKAHEAD_FRAMES = 96`) buffered across 20 ms chunks; negligible next to voice RTT + the 20 ms Opus framing |
| Re-baking voice levels under LUFS drifts existing loudness | P2.3 🟢 | Resolved: `tts:rms -- --bake` re-measures installed voices in one pass; the v2→v3 settings migration shifts the target by the same calibration offset so speech level is preserved; only installed voices re-bake (run reports updated vs. skipped) |
| r8brain `.wasm` not inlined → unstaged/unloaded at runtime | P1.1 | Pin SHA in `package.json`; esbuild `external` + `build.ps1 $externals` must agree; `initResampler()` before `BRIDGE_READY` makes the self-test the gate |
| Protocol drift (C# vs TS) if any item adds an op | any | Update both sides + version bump + tests, per CLAUDE.md |

---

## Carry-forward architecture rules (from CLAUDE.md)

- Output is hard-wired **48 kHz / 16-bit signed / stereo PCM** to Opus; float is
  internal only, quantized once at the master bus (`floatToInt16`).
- The wire protocol lives in **two places** (`Protocol.cs` + `protocol.ts` +
  `pipe-server` dispatch); new ops update both, bump `PROTOCOL_VERSION` on
  incompatible changes, and extend both `ProtocolTests.cs` and `protocol.test.ts`.
  Adding a config field is additive (no version bump).
- esbuild `external:` and `build.ps1 $externals` **must agree**; staged native/WASM
  goes in `dist/node_modules/`; never weaken the build self-test. `audio-decode`
  inlines its WASM (no externals, exempt). **`r8brain-wasm` does NOT inline** — its
  `r8brain.wasm` is a separate file located at runtime via
  `new URL('r8brain.wasm', import.meta.url)`, so it must stay external + staged
  (`dist/node_modules/r8brain-wasm/`) and be loaded as on-disk ESM (a genuine
  dynamic `import()` in `resample.ts`) for `import.meta.url` to resolve the file.
- Don't reintroduce a launcher process; keep the single-`node.exe` lifecycle.
