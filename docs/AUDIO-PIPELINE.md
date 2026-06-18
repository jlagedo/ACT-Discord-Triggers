# Audio Pipeline — Format Widening & Pro-Audio Rework

Design + phased plan for two related goals:

1. **Play more than `.wav`** — accept MP3/OGG/FLAC/etc. so Triggernometry (and
   any ACT trigger) sounds route to Discord regardless of source format.
2. **Sound as good as the chain allows** — tune the Opus encoder, then move the
   internal pipeline to float32 end-to-end so mixing and effects stop
   round-tripping through 16-bit.

Legend — **Effort**: S (days) · M (1–2 weeks) · L (multi-week / architectural).
**Status**: 🔵 planned · 🟡 in progress · 🟢 done · ⚪ idea.

> Supersedes ROADMAP item **#4 "Extra audio formats"**. That entry described
> doing the work plugin-side via NAudio `MediaFoundationReader` with "no bridge
> changes". We are instead doing it **bridge-side** (the bridge already owns
> file decode), unifying *all* formats — including WAV — on one decoder.

---

## Background — why this is needed

- **The trigger handoff is format-limited.** Triggernometry's audio actions
  hand a file path to ACT's `PlaySoundMethod`, which the plugin hijacks and
  forwards to the bridge as a path (`SpeakFileRequest`). Triggernometry's file
  picker explicitly offers `*.wav;*.mp3` (and "All files"), so users routinely
  pick MP3/OGG. (Routing only reaches us when Triggernometry's audio routing is
  set to **ACT** — not its default; see README guidance.)
- **The bridge is WAV-only and strict.** `discord-host.ts` `speakFile` decodes
  with the `wav` package (a RIFF *demuxer*, not a codec) and hard-rejects
  anything that isn't uncompressed 16-bit PCM, mono/stereo, ≤192 kHz. MP3/OGG/
  FLAC have **no decode path at all** — they fail with `{ok:false,error}`, which
  is silent to the player and easy to misread as "the plugin is broken".
- **DSP already runs, but at 16-bit.** `effects.ts` decodes int16 → float32,
  applies an effect, then `clamp16`s back to int16 (hard clip, no dither). The
  mixer then sums in int32 and clamps to int16 again. A single effected trigger
  hits **three** quantization points before Opus. Chaining DSP compounds it.
- **Opus is untuned.** `createAudioResource(mixer, {inputType: StreamType.Raw})`
  sets no bitrate / signal type / complexity — we run `@discordjs/voice`
  defaults. This is the single highest-leverage, lowest-effort quality knob and
  it is currently untouched.

---

## Decisions (locked)

- **Unify on one decoder for every format, WAV included.** Replace the
  `wav`-package path with `audio-decode` (audiojs) for all files. One code path,
  less bespoke code, consistent float output. Trade-off accepted: the common
  WAV case now goes through WASM instead of the lightweight `wav` reader.
- **`audio-decode` (audiojs) umbrella is the decoder.** Most active (MIT,
  ~490k weekly downloads), pure JS/WASM — no native bindings, no `ffmpeg.exe`.
  We call the umbrella `decode(buf)` directly: it auto-detects the container via
  `audio-type` and dispatches to the right codec, so the bridge owns **no** sniff
  /dispatch logic of its own. esbuild folds the package's static
  `() => import('@audio/decode-*')` thunks into the bundle (WASM inlined), so it
  bundles with **no externals/staging** (Branch A) — cost is bundle size (~3.6 →
  9.0 MB, all ~13 codecs inlined). We gain mp3/ogg/flac/wav **plus** opus/m4a/
  aac/webm/etc. for free. (Earlier draft used the four per-codec subpackages +
  a hand-rolled `sniffCodec`; dropped in favor of letting the package own it.
  Runner-up library: `eshaz/wasm-audio-decoders`, same lineage/license.)
- **Final stage stays int16.** `StreamType.Raw` = `s16le`; Opus input is int16
  by definition. Float work is purely *internal*; we quantize **once** at the
  master-bus output.
- **Float internally is justified by the effects requirement.** With a real DSP
  chain + multi-source mixing, float32 throughout (one dithered quantization at
  the end) is correct, not gold-plating — it removes losses that exist today.
- **Effects are paid per-fire, never per-chunk.** `applyRandomEffect` randomizes
  the effect *and* its parameters each firing, so the result is uncacheable. The
  axis that matters is **per-fire (enqueue) vs per-chunk (real-time `_read`)**:
  render the effect once per trigger at enqueue, on a cached decoded+resampled
  dry clip; the 20 ms mixer loop stays trivial (sum → limiter → dither).
- **IPC/protocol unchanged for formats.** Phase 1 is bridge-only — no C# change,
  no wire change. Only Phase 2 (Opus config) touches the protocol.

---

## Target architecture (end state, after Phase 3)

```
audio-decode → Float32 (native rate, N channels)
   → downmix to stereo
   → cache the deterministic prefix:  decode + SRC-to-48k  (keyed by path+mtime)
   ── per fire (enqueue, off the real-time loop) ──
   → random effect (float) · gain / normalize (float)
   → addVoice (float32 voice)
   ── real-time mix (_read, every 20 ms) ──
   → sum voices in Float32 (headroom ~-6 dBFS)
   → master bus: soft/true-peak limiter
   → TPDF dither → quantize s16le      ← the ONLY quantization in the chain
   → Opus (tuned: bitrate — signal/complexity not reachable via prism, see Phase 2)
```

Heavy decode/DSP has a documented relief valve: offload to a `worker_thread`
(the WASM decoders ship worker support) if a long/heavy clip ever blocks the
single main thread and starves `_read`.

---

## Phase 1 — Unified decode via `audio-decode` 🟡 — Effort: S–M

> Implemented on `feature/audio-p1-formats`: `speakFile` calls the `audio-decode`
> umbrella `decode(buf)` directly — it auto-detects the format (wav/mp3/ogg/flac
> + opus/m4a/aac/…), so the bridge keeps no sniff/dispatch code. WASM is inlined,
> so esbuild bundles it with **no** externals/staging (Branch A; bundle ~9 MB).
> Warm-up (wav/mp3/oga/flac, concurrent) runs before `BRIDGE_READY`, so the build
> self-test gates the codec WASM. Remaining: live-channel smoke test.

**Goal:** accept MP3/OGG/FLAC/WAV/etc.; ship. Bridge-only.

**Design**

- Replace the `wav`-package decode in `speakFile` with the `audio-decode`
  umbrella `decode(buf)`. It owns format detection (`audio-type`) and dispatch,
  so all formats — including `.wav` — flow through one call with no bridge-side
  sniffing.
- `decode()` returns float32 planar at the file's native rate. Phase 1 adds a
  **temporary float32 → int16 shim** (which also downmixes mono/>2ch → stereo)
  so the existing int16 tail (`shim → resampleStereo16 → mixer`) is reused
  unchanged. The shim is explicitly throwaway — deleted in Phase 3 (the old
  `upmixMonoToStereo16` helper was removed; the shim subsumes it).
- Keep the graceful `{ok:false,error}` contract; extend messages
  ("unsupported / corrupt audio"). Warm the WASM decoder at init so the first
  trigger doesn't eat module-load latency.

**Gotchas**

- **Non-48k is now the common case.** MP3 is usually 44.1k → the existing
  *linear-interpolation* `resampleStereo16` runs on a non-integer ratio (its
  weakest case). Acceptable to ship; **QA Phase 1 specifically on 44.1k MP3s** —
  if it sounds rough, that's the trigger to pull the high-quality resampler
  (Phase 3b) forward.
- **Packaging is the #1 risk** (CLAUDE.md rule): WASM codecs must appear in
  **both** `esbuild.config.mjs` `external:` *and* `build.ps1` `$externals`,
  staged into `dist/node_modules/`, with `BRIDGE_READY` still asserted by the
  self-test. npm hoisting scatters transitive deps — audit both lists. Import
  only needed codecs (mp3, ogg/vorbis, flac, maybe m4a/aac) to keep
  `node_modules` lean.

**Tests / ship criteria**

- Small mp3/ogg/flac fixtures + decode unit tests; flip the existing
  "WAV rejection" assertions in `pipe-server.test.ts`.
- Integration test: `speakFile` accepts an mp3 against the built bridge.
- `build.ps1 -Zip` self-test green; manual smoke in a live channel.

---

## Phase 2 — Opus quality config 🟢 — Effort: S–M

> **Done** — shipped on `feature/audio-quality-bitrate` (merged to `master`,
> released as `v2.1.0-pre.1`). The spike confirmed prism exposes **only**
> `resource.encoder.setBitrate` through a supported public API; Opus **signal
> type and complexity are not reachable** without reaching into opusscript
> internals, so we shipped **bitrate-only** (the design's documented degrade
> path) and put the others out of scope (see `src/audio-quality.ts`).
> What shipped:
> - New `SetAudioQuality` op (bitrate, bits/sec) in `Protocol.cs` + `protocol.ts`
>   + `pipe-server` dispatch; **`PROTOCOL_VERSION` bumped 3 → 4**; both test
>     suites extended (`ProtocolTests.cs`, `protocol.test.ts`, `pipe-server.test.ts`).
> - Config pushed C# → bridge exactly like `SetNormalization`: on (re)join after
>   `createAudioResource` (holding `resource.encoder`) and re-applied on change.
> - UI dropdown **Low / Medium / High → 48k / 96k / 128k**, default Medium; an
>   inline warning on High (may exceed an unboosted channel's cap). Bitrate is
>   clamped to prism's `[16000, 128000]` on both sides; default `96000` matches
>   between C# and the bridge.
>
> Deviation from the original sketch: presets are bitrate-only and named
> Low/Medium/High (not Voice/Balanced/High with signal=music), and the clamp is
> prism's `[16000, 128000]` rather than Discord's per-channel 64–384 kbps cap
> (Discord still enforces its own channel cap on top).

**Goal:** expose an audio-quality setting in the plugin UI; ship. Highest
audible ROI per effort — but mostly *plumbing*, not audio.

**Design**

- **Spike first:** confirm the encoder handle is reachable. `resource.encoder`
  (prism-media Opus) should exist with `StreamType.Raw`. `setBitrate` is
  reliable; **signal type (music) and complexity require an Opus CTL** — verify
  prism exposes it cleanly *before* committing the UI, so we don't ship a
  "quality" control that only moves bitrate. (Open question, below.)
- **Presets, not a raw kbps slider:** `Voice / Balanced / High` → fixed
  `{bitrate, signal, complexity}`. Discord clamps bitrate to the channel max
  (boost-tier dependent, 64–384 kbps), so "High = up to channel max" is honest.
- **Config plumbing** mirrors the existing `SetNormalization` op (config pushed
  C# → bridge). New `SetAudioQuality` op (or fields on `JoinChannel`): update
  `Protocol.cs` **and** `protocol.ts` + `pipe-server` dispatch; bump
  `PROTOCOL_VERSION` if the shape is incompatible; extend `ProtocolTests.cs` and
  `tests/protocol.test.ts`. Apply on (re)join after `createAudioResource`;
  re-apply on change.
- Plugin UI: a dropdown on the config tab with a sensible default (e.g.
  Balanced ≈ 96 kbps, signal=music).

**Tests / ship criteria**

- Unit tests assert the `setBitrate`/CTL calls fire with preset values; protocol
  round-trip tests both sides. Audible validation is a manual A/B in a live
  channel.

---

## Phase 3 — Float32 end-to-end (pro pipeline) 🔵 — Effort: L

**Goal:** one float bus from decode to the master output; a single dithered
quantization before Opus. Split into two shippable sub-phases.

**3a — Float master bus**

- Mixer (`pcm-mixer.ts`) sums in **Float32** (replacing the int32 accumulator),
  runs with headroom, then **soft/true-peak limiter** → **TPDF dither** →
  `s16le`. Removes the mixer's hard-clip.
- `effects.ts`: drop the per-effect int16 `decode`/`encode` round-trip; operate
  directly on float bus buffers. Net code *reduction* in the DSP path.
- Delete Phase 1's float→int16 shim — decoded float rides straight into the bus.
- Revisit `MAX_QUEUED_BYTES` (float voices are 2× int16). Rewrite the
  `pcm-mixer` tests (the int32-sum / hard-clamp contract changes); add limiter +
  dither tests.

**3b — High-quality resampler**

- Replace linear-interp `resampleStereo16` with a windowed-sinc / polyphase SRC
  (soxr or libsamplerate via WASM). **This is half the "pro" —** float
  everywhere with a linear resampler still has an audible weak stage. Another
  externals/packaging round (same checklist as Phase 1).
- Resample the dry clip once, cached (deterministic); only the random effect
  runs live per fire.

**Why last:** biggest blast radius, zero external surface, and it depends on the
format work (Phase 1) being in place to be worth it.

---

## Risk register

| Risk | Phase | Mitigation |
|------|-------|------------|
| WASM externals break the bundle / self-test | 1, 3b | Sync both externals lists; keep `BRIDGE_READY` assert; import only needed codecs |
| Linear resampler audible on 44.1k MP3 | 1 | QA on 44.1k; pull 3b forward if needed |
| ~~Opus signal/complexity CTL not reachable via prism~~ (materialized) | 2 | ✅ Resolved: not reachable via prism's public API; shipped **bitrate-only** as planned |
| Heavy per-fire DSP blocks the single thread → mixer underrun | 3 | Keep render fast; `worker_thread` offload as relief valve |
| Float voices 2× memory vs int16 | 3 | Recompute `MAX_QUEUED_BYTES`; clips are short |
| Protocol drift (C# vs TS) | 2 | Update both sides + version bump + tests, per CLAUDE.md |

## Open questions

- ~~Exact `@discordjs/voice` / prism-media API to set Opus **signal type** and
  **complexity** (not just bitrate).~~ **Resolved (Phase 2):** prism exposes only
  `setBitrate` publicly; signal/complexity would require reaching into opusscript
  internals, so they're out of scope and Phase 2 shipped bitrate-only.
- Resampler library choice for 3b (soxr vs libsamplerate WASM): quality vs
  bundle size vs maintenance.
- WAV unification perf: confirm routing the common WAV case through WASM decode
  doesn't regress first-trigger latency (warm-start should cover it).

---

## Carry-forward architecture rules (from CLAUDE.md)

- Output is hard-wired **48 kHz / 16-bit signed / stereo PCM** to Opus; float is
  internal only, quantized once at the master bus.
- Wire protocol lives in **two places** (`Protocol.cs` + `protocol.ts` +
  `pipe-server` dispatch); new ops update both, bump `PROTOCOL_VERSION` on
  incompatible changes, and extend both test suites.
- esbuild `external:` and `build.ps1 $externals` **must agree**; staged WASM goes
  in `dist/node_modules/`; never weaken the build self-test.
- Don't reintroduce a launcher process; keep the single-`node.exe` lifecycle.
