# Design: Pluggable TTS Providers

Status: **proposed** · Owner: TBD · Last updated: 2026-06-18

Implements roadmap feature #1. This document is the single source of truth for
the pluggable-TTS work; the roadmap entry only links here.

> **Decisions so far (2026-06-18):**
> 1. Synthesis lives in the **Node bridge**, not the .NET plugin (§2).
> 2. `System.Speech` stays in .NET as the zero-config default + fallback (§2).
> 3. **First provider to build: ElevenLabs** (premium, flagship quality, simplest
>    auth). (§4, §10)
> 4. **Second: one local/offline provider** — **Piper vs Kokoro is still open**,
>    to be decided on maturity/safety for the end user (§7).
> 5. **Dropped:** "Polly-first / five-provider" plan — Polly's callout-community
>    evidence turned out weak (it's a streamer-donation / chat-TTS artifact, not a
>    raid-callout favorite), and AWS key setup is the highest-friction option (§4,
>    §11).

---

## 1. Goal & why this is needed at all

Let users pick the voice engine that synthesizes ACT trigger callouts, instead of
being stuck with the robotic Windows `System.Speech` voices (David/Zira/Elsa) the
plugin uses today — the #1 quality complaint across the FFXIV/ACT community.

**Why we can't just lean on Windows for good voices.** This was researched
thoroughly; the short version:

- The callout community overwhelmingly runs the **default classic SAPI voices**,
  and most users don't even know how to change them. The plugin already has a
  voice picker (`DiscordPlugin.cs` → `SpeechSynthesizer.GetInstalledVoices()`),
  so that pain is *selection*, not provider choice — and it's already solved.
- Windows 11 **does** have good on-device **neural "Natural" voices**
  (Aria/Jenny/Guy), powered by the same engine as Azure AI — but Microsoft
  **deliberately walls them off to Narrator**. They are not exposed to
  third-party apps through *any* official API (not `System.Speech`, not the WinRT
  `Windows.Media.SpeechSynthesis`). Confirmed in Microsoft Q&A and Windows docs.
- The only third-party route (NaturalVoiceSAPIAdapter) is a **self-described hack**
  that extracts system keys, scrapes an unofficial Edge endpoint, runs native code
  inside ACT, and has already broken on a Windows update. **Rejected** — too
  fragile/untrusted to put in our setup docs (§11).
- Microsoft's **official** recommended path for neural TTS in an app is **Azure AI
  Speech (cloud)**. So building cloud/local providers isn't us inventing a need —
  it's the supported architecture, because Windows refuses to lend apps its local
  neural voices.

Non-goals: streaming/partial synthesis (callouts are short, full-buffer is fine),
voice cloning UI, and reading arbitrary game text (this is callouts only).

---

## 2. Decision: synthesize in the bridge (Node), not the plugin (.NET)

**New TTS providers are implemented in the Node bridge. `System.Speech` stays in
.NET as the zero-config default and offline fallback.**

### Why the bridge

1. **The bridge is already the audio engine.** `discord-host.ts` already does
   sample-rate conversion (`resampleStereo16`), channel up-mix
   (`upmixMonoToStereo16`), loudness normalization (`normalizePcm16`), effects
   (`applyRandomEffect`), mixing (`PcmMixer`), and caching (`WavCache`). The
   `SpeakFile` op already sends only a *path* and lets the bridge open, decode,
   resample, normalize, and mix the audio. A `SpeakText` op that sends *text* and
   lets the bridge synthesize is the identical pattern — finishing a migration
   that is already ~90% done, not a new architectural burden.

2. **Dependencies are safe in the bridge; they are not in net48.** This is the
   real driver. **.NET 4.8 is legacy** — packages are dropping net48/netstandard2.0
   support, SDKs ship native DLLs that fight Costura, and we'd need TLS-1.2
   workarounds. We deliberately **freeze the .NET surface**. Node, by contrast, is
   actively maintained and everything is supported. So the **"no new dependency"
   rule applies only to the .NET side** — the bridge is free to take dependencies
   **where they earn their keep** (e.g. a local-TTS runtime). Cloud providers stay
   lean anyway: Node 24 ships global `fetch`, so REST providers (ElevenLabs, Azure,
   OpenAI-compatible) need no SDK at all.

3. **No new trust boundary.** API keys cross the named pipe and live in Node
   memory — but the **Discord bot token already does exactly that**
   (`InitRequest.token`). The pipe is local and per-user. An API key is no
   different from the token already trusted to it.

4. **Less IPC.** Today `System.Speech` ships a full base64 PCM blob per callout.
   `SpeakText` ships a short string; the bridge produces the audio. Smaller
   frames, one less synth→serialize→deserialize hop.

### Why `System.Speech` stays in .NET

It is Windows SAPI, in-process, and cannot move to Node. It is also the only
zero-download, zero-config option, and it's already wired into the voice picker.
Keep it exactly as-is on the existing binary `SpeakPcm` path. It doubles as the
**fallback**: if a bridge-side provider errors or times out, the bridge returns
`SpeakResult { ok:false }` and the plugin re-synthesizes the same text via
`System.Speech` and sends `SpeakPcm`. A raid callout is never silently lost.

### Audio-format preference (was a hard rule, now a preference)

> **Prefer providers that return 16-bit PCM or WAV** — it lets the audio drop
> straight into the existing pipeline with no decode step and lowest latency. All
> the cloud providers we care about support a PCM/WAV output, so request it.
> Because the bridge may now take dependencies (point 2), a provider that only
> emits mp3/opus is **acceptable** if it justifies adding a decoder — it's a
> trade-off to weigh per provider, not a blanket ban.

---

## 3. What users have today (baseline we're beating)

| Tier | Examples | Accessible to the plugin? | Quality |
|---|---|---|---|
| Classic SAPI | David, Zira, Hazel | ✅ in use now | robotic |
| OneCore "mobile" | Mark, Eva | ✅ via WinRT (not worth a dep) | mediocre, still not neural |
| Win11 Narrator **Natural** (neural) | Aria/Jenny/Guy | ❌ Narrator-only, walled off | good, but unusable by us |
| Azure neural (cloud) | — | ✅ with a key | great (Microsoft's official path) |

The whole point of this feature: deliver neural-grade voices into a context where
Windows otherwise refuses to.

---

## 4. Provider lineup — a three-rung ladder

Reframed from the old "five cloud providers" list into tiers by what the user has
to do. Build order is driven by the §-top decisions: **ElevenLabs first, then one
local provider.**

**Rung 1 — Free / built-in / offline**
- **System.Speech** *(shipped)* — zero-download baseline + fallback.
- **Embedded local neural** *(build #2)* — **Piper or Kokoro, TBD (§7)**. Bundled/
  downloaded voice, runs inside the bridge or as a shipped exe. No key, no server.

**Rung 2 — Free / bring-your-own-server**
- **OpenAI-compatible** connector (configurable base URL). One connector hits
  OpenAI cloud *or* a local **Kokoro-FastAPI** / **openedai-speech** server. For
  power users who already run a local TTS server; not the default free path
  (Docker/Python friction).

**Rung 3 — Paid / premium**
- **ElevenLabs** *(build #1)* — flagship quality, simplest auth (one `xi-api-key`
  header), cheap at callout volume. The "wow" voice; genuinely unreachable any
  other way (no SAPI path exists for it).
- **Azure Neural** *(later)* — Microsoft's official neural path; worthwhile for
  users who already have an Azure key. REST, can emit 48k PCM directly.

**Deferred / dropped:** Amazon **Polly** (weak callout evidence + AWS-key friction;
see §11), Google Cloud TTS.

### Per-provider request shapes

| Provider | Transport | Output requested | Notes |
|---|---|---|---|
| ElevenLabs | REST (`fetch`) | `output_format=pcm_24000` → resample 24→48k | `xi-api-key` header; model e.g. `eleven_flash_v2_5` (low latency) |
| OpenAI-compatible | REST (`fetch`) | `response_format=pcm` / `wav` | base URL → OpenAI cloud or local server |
| Azure Neural | REST (`fetch`) | `raw-48khz-16bit-mono-pcm` (no resample) | `Ocp-Apim-Subscription-Key`; region host |
| Embedded local | in-process / exec | native PCM → resample | see §7 |

### Cost note (why premium is fine here)

The community's fear of premium TTS comes from high-volume use (voicing all chat
or NPC dialogue, where ElevenLabs can run ~$1000). **Raid callouts are the
opposite**: a small set of short, repeated phrases. Monthly character counts are
tiny, and the bridge cache (§9) makes repeats free after first synth. ElevenLabs
costs pennies/month in this use case.

---

## 5. Quality expectations

Rough MOS ladder (illustrative; ordering is robust, exact numbers vary by test):

```
ElevenLabs                         ~4.7–4.8   ← build #1, premium
Kokoro                             ~4.3–4.5   ← #1 open-source (TTS Arena)
Azure ≈ Win11 Narrator "Natural"   ~4.0–4.5   ← walled off from apps
Piper (medium/high)                ~3.8–4.0   ← "good but synthetic"
────────────────── big gap ──────────────────
Classic SAPI (David/Zira)          ~3.0–3.3   ← what the plugin uses now
```

Takeaways that drove the decisions:
- **vs what the plugin can use today (classic SAPI):** Piper and Kokoro are both a
  *dramatic, obvious* upgrade. The feature is clearly worth it.
- **vs the best Windows voices (Narrator Natural, which we can't touch):** Kokoro
  is ~on par; Piper is roughly equal-to-slightly-behind. So the local tier
  delivers "the quality Microsoft reserves for Narrator" into our context.
- **Kokoro > Piper** on naturalness; **ElevenLabs > both**.

---

## 6. Provider abstraction (TypeScript, in the bridge)

Providers only *produce* audio. Resample / normalize / mix / cache stay
centralized in the existing pipeline — a provider cannot get the output format
wrong. Providers may be REST (cloud) **or** local (in-process / child process).

```ts
export interface TtsResult {
    pcm: Buffer;        // 16-bit signed LE
    sampleRate: number; // provider's native rate; pipeline resamples to 48k
    channels: 1 | 2;    // pipeline up-mixes mono → stereo
}

export interface TtsProvider {
    readonly id: string;            // "elevenlabs" | "openai" | "azure" | "kokoro" | "piper"
    synthesize(text: string, opts: TtsOpts, signal: AbortSignal): Promise<TtsResult>;
    listVoices?(signal: AbortSignal): Promise<VoiceInfo[]>;  // for the .NET dropdown
    warm?(): Promise<void>;         // local engines: load model on channel join
}

export interface TtsOpts {
    voice: string;
    model?: string;
    speed?: number;
    apiKey?: string;
    baseUrl?: string;   // OpenAI-compatible → local server or cloud
    region?: string;    // Azure
}
```

A `TtsRegistry` keyed by `id` holds the instances; `DiscordHost` keeps the
currently-selected provider + its `TtsOpts`, set via `SetTtsConfig` (§8).

### Pipeline placement

`SpeakText` flows into the **same** path `SpeakFile` and `SpeakPcm` already use:

```
SpeakText{text}
  → provider.synthesize() → TtsResult
  → upmixMonoToStereo16 (if mono)
  → resampleStereo16 (→ 48k; skipped when provider already emits 48k, e.g. Azure)
  → [existing] applyRandomEffect → normalizePcm16 → PcmMixer.addVoice
  → SpeakResult{ok}
```

No new audio code — only the synth source is new. Reuse `resampleStereo16` /
`upmixMonoToStereo16` / `normalizePcm16` / `PcmMixer` verbatim.

---

## 7. Local/offline tier — Piper vs Kokoro (OPEN DECISION)

The local provider is **build #2**. Which engine ships is **not yet decided** —
the criterion is *maturity / safety for the end user*. Both clear the SAPI bar by
a wide margin (§5); they differ on integration risk, licensing, and quality.

### Integration patterns

| Engine | How it runs in the bridge | Output |
|---|---|---|
| **Piper** | spawn `piper.exe --output-raw`, read stdout | 16-bit mono PCM @ ~22 kHz |
| **Kokoro** | in-process via `kokoro-js` (`device:"cpu"`, `onnxruntime-node`) | PCM |

### Decision matrix

| Criterion | **Piper** | **Kokoro** |
|---|---|---|
| Gaming adoption / proven | **Strong** — default TTS in Mantella (large Skyrim AI-NPC mod) and forks | Rising/newer; FFXIV-TTS guide, #1 TTS Arena |
| Out-of-box quality | Good, clearly synthetic (~3.8–4.0) | **Better** (~4.3–4.5) |
| Integration risk | **Low** — shell out to a self-contained exe (espeak-ng embedded) | Higher — in-process **native `onnxruntime-node`** (must be staged like `@snazzah/davey`) |
| License for bundling | GPL wrinkle: `rhasspy/piper` archived → `OHF-Voice/piper1-gpl` (GPL-3.0) + GPL espeak. Shell-out = mere aggregation, but we'd ship a GPL exe. (MIT `piper-plus` exists but is less proven.) | **Cleaner** — model Apache-2.0, `kokoro-js` MIT |
| Package weight | voice model ~20–60 MB | ~80 MB model + ~28 MB voices (~110 MB) |
| CPU cost | Lighter | Heavier (fine on gaming rigs) |
| Latency | Lowest | Higher — but **cache-hideable** for callouts (§9) |

### How to decide (the actual question to answer)

Because callouts are short and cacheable, Piper's latency edge barely matters,
which tilts the *quality* axis toward Kokoro. But Piper is the **proven, simplest,
lowest-risk** integration in exactly this "TTS for a game" niche. So the decision
reduces to:

- **Pick Piper** if "matches what the gaming community already ships + simplest,
  most robust integration" wins, and the GPL-exe distribution is acceptable.
- **Pick Kokoro** if "best out-of-box quality + cleanest license to bundle" wins,
  and we accept the newer in-process `onnxruntime-node` integration + ~110 MB
  download.

**Resolution plan:** prototype both behind the `TtsProvider` seam (they share the
same interface), A/B the voices on real callouts, and confirm `onnxruntime-node`
stages cleanly through `build.ps1` before committing to Kokoro. Whichever proves
more robust for a non-technical end user wins. Model/exe assets should be
**downloaded on first selection** (and cached), not bloat the base release zip.

---

## 8. Protocol changes

Bump `PROTOCOL_VERSION` **3 → 4** in both `DiscordAPI/Protocol.cs` and
`DiscordBridge-node/src/protocol.ts`. Update `pipe-server.ts` dispatch and extend
both `ProtocolTests.cs` and `tests/protocol.test.ts` (standing rule —
incompatible wire shape → version bump + both test suites).

### `SetTtsConfig` — global config push (mirrors `SetNormalization`)

```ts
interface SetTtsConfigRequest extends BaseRequest {
    op: 'SetTtsConfig';
    providerId: string;     // "" or "system" => bridge synth disabled; plugin uses SpeakPcm
    voice: string;
    model?: string;
    speed?: number;
    apiKey?: string;        // never logged (see §10)
    baseUrl?: string;
    region?: string;
}
interface SetTtsConfigResponse { op: 'SetTtsConfigResult'; reqId: ReqId; ok: true; error: '' }
```

### `SpeakText` — synthesize + play (mirrors `SpeakFile`)

```ts
interface SpeakTextRequest extends BaseRequest {
    op: 'SpeakText';
    text: string;
    randomEffect?: boolean; // same meaning as SpeakFile.randomEffect
}
// Response reuses the existing SpeakResult { ok, error }.
```

`GetVoices` (optional, later) can back a "refresh voices" button in the UI:
`GetVoicesRequest{ providerId }` → `GetVoicesResult{ voices: VoiceInfo[] }`.

### Plugin-side routing (the ACT hook)

`PlayTtsMethod` hands the plugin text:

- selected provider is `system`/empty → synthesize via `System.Speech`, send
  `SpeakPcm` (**unchanged existing path**).
- otherwise → send `SpeakText{ text }`. On `SpeakResult{ ok:false }`, fall back:
  synthesize via `System.Speech` and send `SpeakPcm`.

---

## 9. Caching

Reuse the spirit of `WavCache`, keyed on `hash(providerId, voice, model, speed,
text)` → resampled 48k PCM. Short callout phrases repeat constantly, so this:

- removes per-callout latency on repeats (cloud TTFA 75–300 ms; local model load),
- drives recurring cloud cost to ~zero,
- **hides the local engines' synthesis latency** (relevant to the Piper/Kokoro
  choice in §7).

Bound the cache (LRU by entry count or bytes) and clear it on `SetTtsConfig`
change (voice/model swap invalidates entries). For local engines, also `warm()`
the model on voice-channel join so the first callout isn't slow.

---

## 10. Security & reliability

- **API keys**: stored in the plugin settings; pushed over the pipe via
  `SetTtsConfig`; held in Node memory only. Same trust level as the bot token,
  which already crosses this pipe. **Never** write keys to `DiscordBridge.log`.
- **TLS**: not a concern on the bridge side — Node handles modern TLS. (One of the
  reasons not to do this in net48.)
- **Timeouts / fallback**: each `synthesize()` takes an `AbortSignal` with a short
  deadline (e.g. 5 s). On timeout/error the bridge returns `SpeakResult{ok:false}`
  and the plugin falls back to `System.Speech` (§8).
- **Latency**: cloud adds 75–300 ms TTFA; cache (§9) hides it for repeats.
  `System.Speech` and warmed local engines are near-instant.

---

## 11. Rejected / deferred options (so we don't relitigate)

- **NaturalVoiceSAPIAdapter (third-party SAPI shim)** — *rejected.* Self-described
  hack: extracts system encryption keys, scrapes an unofficial Edge endpoint, runs
  native COM code inside ACT, already broke on a Windows update, single maintainer.
  Not something to recommend in setup docs, even though it's currently maintained.
- **Reading Win11 Narrator "Natural" voices directly** — *not possible.* Microsoft
  walls them off to Narrator; no official app API (§1).
- **WinRT `Windows.Media.SpeechSynthesis` from net48** — *not worth it.* Exposes
  more *standard* voices than `System.Speech`, but still **not** the neural ones,
  and it'd add a .NET-side dependency against the freeze.
- **Polly-first / five-provider plan** — *dropped.* Polly's apparent popularity was
  a **streamer-donation ("Brian") / chat-TTS (TextToTalk) artifact**, not raid-
  callout adoption. It fills no SAPI gap and has the **highest setup friction**
  (AWS account + IAM keys). Not worth leading with; may return as a low-priority
  extra.
- **Synthesizing in the .NET plugin** — *rejected* in favor of the bridge (§2).

---

## 12. Build / packaging impact

- **Cloud providers (ElevenLabs, OpenAI-compatible, Azure):** **none.** Plain
  `fetch`, PCM/WAV output → no new npm deps, no esbuild/`build.ps1` changes, no
  net48 changes.
- **Local tier (Piper or Kokoro):** **real impact, and it's the main reason §7 is a
  deliberate decision:**
  - *Kokoro* → adds `kokoro-js` + native **`onnxruntime-node`**. The native
    `.node` binary **cannot be esbuild-bundled** — it must be marked external and
    **staged into `dist/node_modules/`** (the existing `@snazzah/davey` /
    `opusscript` pattern; the `esbuild` external list and `build.ps1 $externals`
    list must agree). Plus a ~110 MB model download-on-first-use.
  - *Piper* → ship/download `piper.exe` + a voice model as **separate assets**
    (not bundled into `bundle.js`); GPL component must be shipped with its license.
  - Either way: assets **download on first selection**, cached locally — keep the
    base release zip lean.

---

## 13. Implementation phases

1. **Seam (no behavior change):** introduce `TtsProvider`/`TtsRegistry` in the
   bridge with a single `system`-disabled path; add `SetTtsConfig` + `SpeakText`
   ops + version bump 3→4 + tests. Plugin still uses `System.Speech`/`SpeakPcm`
   until a provider is selected. Proves the wiring end-to-end.
2. **ElevenLabs** *(decision #3)* — first real provider. REST + `xi-api-key`,
   `output_format=pcm_24000`, model `eleven_flash_v2_5`. Plus UI: provider
   dropdown, dynamic config panel (API key + voice), "test voice" button.
3. **Local provider** *(decision #4)* — resolve **Piper vs Kokoro** per §7
   (prototype both behind the seam, pick the more end-user-mature), then ship it as
   the free/offline neural option with first-use asset download + `warm()`.
4. **Caching** (§9) hardening + voice-list refresh.
5. **Later / optional:** OpenAI-compatible (BYO local server), Azure Neural (for
   key-holders), Google Cloud TTS.

---

## Appendix: source material

Research behind the decisions:

**Community usage & Microsoft's voice walls**
- [karashiiro/TextToTalk — FFXIV *chat* TTS providers (the Polly/Azure signal's real origin)](https://github.com/karashiiro/TextToTalk)
- [TextToTalk #153 — ElevenLabs request, quality vs. cost](https://github.com/karashiiro/TextToTalk/discussions/153)
- [cactbot #4694 — users stuck on the default SAPI voice, don't know how to change it](https://github.com/quisquous/cactbot/issues/4694)
- [Microsoft Q&A — Natural voices are Narrator-only, not for app Text-to-Speech](https://learn.microsoft.com/en-us/answers/questions/4125876/why-are-natural-voices-only-available-for-narrator)
- [Stack Overflow — Narrator Natural voices not in System.Speech, not open to third-party devs](https://stackoverflow.com/questions/77443751/how-to-access-newly-added-natural-voices-in-powershell-after-windows-11-update)
- [gexgd0419/NaturalVoiceSAPIAdapter — the third-party "hack" (rejected, §11)](https://github.com/gexgd0419/NaturalVoiceSAPIAdapter)
- [Azure AI Speech REST — Microsoft's official neural-TTS path](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/rest-text-to-speech)

**Local engines (Piper vs Kokoro)**
- [art-from-the-machine/Mantella — Piper as default TTS in a major game AI mod](https://github.com/art-from-the-machine/Mantella)
- [rhasspy/piper — `--output-raw` 16-bit mono PCM; now archived](https://github.com/rhasspy/piper)
- [OHF-Voice/piper1-gpl — maintained GPL-3.0 successor](https://github.com/OHF-Voice/piper1-gpl)
- [ayutaz/piper-plus — MIT, espeak-free, npm/WASM fork](https://github.com/ayutaz/piper-plus)
- [kokoro-js (npm) — Kokoro on Node CPU, in-process](https://www.npmjs.com/package/kokoro-js)
- [onnx-community/Kokoro-82M-ONNX — quantized model, Apache-2.0](https://huggingface.co/onnx-community/Kokoro-82M-ONNX)
- [Kokoro-FastAPI / openedai-speech — OpenAI-compatible local servers (Rung 2)](https://github.com/remsky/Kokoro-FastAPI)
- [Kokoro vs Piper comparison 2026](https://slashdot.org/software/comparison/Kokoro-TTS-vs-Piper-TTS/)

**Provider landscape**
- [Best TTS APIs 2026 — latency/quality (Speechmatics)](https://www.speechmatics.com/company/articles-and-news/best-tts-apis-in-2025-top-12-text-to-speech-services-for-developers)
- [MinhakaDev/FFXIV-TTS — Kokoro-based local TTS for FFXIV](https://github.com/MinhakaDev/FFXIV-TTS)
