# TTS Engines — UI Draft & Wiring

> **TL;DR** — Add a user-selectable TTS **engine**: keep **Windows (SAPI)** (today's default, offline, synthesized in C#) and add **ONNX** — neural voices run **in the Node bridge** via `sherpa-onnx-node`, because that native addon cannot load in net48. Under ONNX a **Quality** toggle picks **Piper** (fast, light) or **Kokoro** (natural, heavy). TTS configuration moves out of the **Sound** page into a **new top-level "Text-to-Speech" section** whose first control is an **Engine** picker that reshapes the page. **C# (.NET) owns voice provisioning** — the curated catalog, the installed/not-installed state, and downloads — so it all works **with no Discord connection** (the bridge only exists while connected). The only thing that crosses the wire per call is **`SpeakText { text }`**; the chosen voice/family/speed/threads/models-dir all ride in the existing **`SetConfig`**. The bridge synthesizes with `generateAsync` (off the event loop, streaming chunks), caches the result, then rejoins the existing effects → normalize → Opus pipeline. This document is the UI draft (ASCII mockups + navigation) plus the wiring needed to make it work.

This began as a design draft; build-plan **steps 1–6 are now implemented** (catalog, relocated UI, ONNX UI, settings, the C# downloader, and bridge synthesis on protocol v6 — ONNX now speaks in Discord), with **steps 7–9** (release-packaging confirmation, synthesis cache, hardening) still to come. The **Implementation plan** section at the bottom tracks per-step status; the rest captures the decisions so the remaining build proceeds without re-litigating them.

---

## The one fact that shapes everything

**System.Speech (SAPI) runs in C#. Piper/Kokoro (`sherpa-onnx-node`) are a native Node addon — they can only run in the bridge process.**

So this is **not** "swap the synthesizer." It is "add a second synthesis *location*":

- **SAPI** → C# synthesizes 48 kHz/16-bit/stereo PCM and sends the existing `SpeakPcm` binary frame (unchanged).
- **ONNX (Piper / Kokoro)** → C# sends only the *text* via a new `SpeakText` op (the chosen voice/family/speed/models-dir already reached the bridge in `SetConfig`); the **bridge** synthesizes with `sherpa-onnx-node` using its currently-configured voice, resamples to 48 kHz stereo, and hands the audio to the **same** effects/normalize/Opus path `SpeakPcm` already uses.

Both paths converge right after synthesis, so there is only one playback pipeline.

---

## Current code baseline (so this doc stops drifting)

Pinned against the tree as of this revision — verify before editing:

| Thing | Current state | Location |
|---|---|---|
| Protocol version | **6** (`SpeakText` op + SetConfig `ttsParams` bag) | `Protocol.cs:6`, `protocol.ts:35` |
| Settings schema version | **2** (`V1ToV2` migration registered) | `PluginSettings.cs:22`, `Settings/Migrations/V1ToV2.cs` |
| Reply envelope | single generic `Result` `{op,reqId,ok,error,data?}`, correlated by `reqId` — **there is no `SpeakResult` op** | `Protocol.cs:117`, `pipe-server.ts` `_result` |
| TTS today | C# `DiscordClient.Speak(text,voice,vol,speed)` → System.Speech → `SpeakPcm` binary frame | `DiscordClient.cs:285` |
| Dispatch seam | ACT's `PlayTtsMethod` → `DiscordTriggersViewModel.SpeakText(string)` → `discord.Speak(...)` | `DiscordTriggersViewModel.cs:235` |
| TTS settings | SAPI `TtsVoice`/`TtsVolume` are `push:false` (C#-synthesized). `TtsSpeed` and the ONNX fields (`TtsEngine`/`OnnxFamily`/`OnnxVoice`/`TtsThreads`/`ModelsDir`) persist and **`push:true`** — they ride `SetConfig` for the bridge | `PluginSettings.cs`, VM `FromSettings`/`ToSettings` |
| Nav | WPF `TabControl` with `TabItem`s **General · Sound · Information** | `DiscordTriggersView.xaml:101/239/351` |
| Sound page | "Text-to-speech" (Voice/Volume/Speed) **+** "Effects & leveling" + "Audio quality" | `DiscordTriggersView.xaml:239-348` |
| Playback pipeline | `_enqueue`: random-fx (bridge rolls from config) → normalize (global RMS) → declick → `mixer.addVoice(fullBuffer)` | `discord-host.ts:374-422` |
| Mixer | pull-based `Readable`, `addVoice(pcm)` takes a **complete** buffer | `pcm-mixer.ts:55-69` |
| File PCM cache | `WavCache` — LRU `Map`, keyed by path+mtime, stores decoded+resampled 48k/16/stereo PCM | `wav-cache.ts`, used `discord-host.ts:469/486` |
| Bridge externals | staged in `build.ps1 $externals` + `esbuild.config.mjs` `external` | `build.ps1:70-86` |

---

## The `sherpa-onnx-node` API we'll use

Confirmed against `sherpa-onnx-node` 1.13.3 (the version benchmarked in the `sherpa-onnx-test` repo):

- **One `OfflineTts` instance per loaded model.** Piper uses `model.vits`; Kokoro uses `model.kokoro`. `provider:'cpu'`, `numThreads` from settings.
- **Synthesis off the event loop, with streaming chunks:**
  ```js
  await tts.generateAsync({
    text,
    generationConfig: new GenerationConfig({ sid, speed, ...(lang ? { extra: { lang } } : {}) }),
    onProgress: ({ samples, progress }) => { /* Float32 chunk; return 0/false to cancel */ },
  });
  ```
  `generateAsync` dispatches the native work to a libuv worker thread (returns a `Promise`) and invokes `onProgress` per produced chunk. This is the answer to "fire a new thread to stream audio while being generated" — **the addon already provides the worker thread**; no manual `worker_thread`.
- **Kokoro = one instance, language per call** via `generationConfig.extra.lang` (`en-gb-x-rp`, `pt-br`, `""`→English lexicon). Piper ignores `lang` (each model carries its own espeak voice).
- Output is **Float32 mono** at the model's rate (Piper 22.05 kHz, Kokoro 24 kHz) → convert to 16-bit → reuse `resampleStereo16` → 48 kHz stereo.

---

## Decisions taken

| Decision | Choice |
|---|---|
| Engines exposed | **SAPI** + **ONNX**; under ONNX a **Quality** toggle picks **Piper** (fast, light) or **Kokoro** (natural, heavy) |
| TTS lives in the UI | **New top-level "Text-to-Speech" nav section**; moved out of the Sound page |
| First control | **Engine choice-cards** (SAPI \| ONNX) side by side; picking one reshapes the page (the chosen card takes the accent fill + ✓). A 2-item dropdown for a page-defining choice was rejected |
| Quality labels | **Fast** (Piper) \| **Natural** (Kokoro) — benefit-first; the engine name and CPU/size trade-off ride in the description line under the toggle |
| Voice picker | **Searchable, locale-grouped flyout** — a field button opens a search box over a grouped, live-filtered list (the catalog is too long for a plain dropdown). Filtering lives in the view (code-behind) so Core stays WPF-free |
| Audition | **No preview link** — an engine-specific external samples site is a maintenance liability (Piper-only, breaks if the site goes down), so it was dropped |
| Voice/family state | two settings: `OnnxFamily` (`piper`\|`kokoro`) + `OnnxVoice` (the pick) — see Settings model |
| **Provisioning owner** | **C# (.NET)** owns the curated catalog, install-state, and downloads — works with **no Discord connection** (the bridge is only alive while connected) |
| Voice provisioning | **Download on demand** from the k2-fsa `tts-models` release; nothing bundled |
| Models directory | user-configurable `ModelsDir`, default `%APPDATA%\ACT_DiscordTriggers\models` |
| Default engine | **SAPI** (upgrading users see identical behavior) |
| Default neural CPU usage | **1 thread** (gentlest while gaming; see Benchmark) |
| Synthesis off-thread | **`generateAsync`** (libuv worker; non-blocking) |
| **Wire surface** | only **`SpeakText { text }`** is new; voice/family/speed/threads/modelsDir all ride in the existing **`SetConfig`** (commands don't carry settings) |
| Synthesis cache | **bridge-side**, keyed by the current synth config + text (see Wiring §e) |
| ONNX volume | **No volume slider** — loudness is handled by the shared Auto-level on the Sound page (sherpa has no native volume) |
| Languages | **English (US + GB), French, German, Spanish (ES + MX), Portuguese-BR, Russian** — Japanese excluded (own mature TTS ecosystem). See Voice catalog |
| Catalog source | **generated from Piper `voices.json`** (filtered to our locales, `medium`+`high` tiers), not hand-typed |
| Kokoro coverage | **English (graded A–F) + the 3 pt-BR speakers only** — Kokoro's non-English voices are thin/absent, so fr/de/es/ru are **Piper-only** |

> **Architecture decision (this revision):** voice provisioning lives in **C#**, not the bridge. The bridge process is spawned inside `ConnectAsync` (`DiscordClient.cs:46`) and only exists while connected to Discord — but users browse/download voices at settings-time, before any call. So C# owns the curated catalog, the installed scan, and the downloads (`HttpClient` + Windows `tar.exe`). The bridge stays synthesis-only and learns *which* voice to use entirely from `SetConfig`. This removes the previously-planned `ListVoices` / `DownloadVoice` / `DownloadProgress` ops — `SpeakText { text }` is the only new wire message.

---

## Benchmark basis (why these defaults)

Measured in-process via `sherpa-onnx-node` (full evaluation in the `sherpa-onnx-test` repo). Warm, per short callout (~2 s of audio):

| Engine | gen / callout | CPU work / callout | RAM (peak) | Notes |
|---|--:|--:|--:|---|
| **Piper** (1 thread) | ~150 ms | ~0.16 core-s | ~205 MB | 13× realtime; gentlest on a busy machine |
| **Piper** (4 threads) | ~66 ms | ~0.25 core-s | ~205 MB | faster wall-time, *more* total CPU work |
| **Kokoro** (1 thread) | ~1.7 s | ~1.7 core-s | ~645 MB | borderline single-threaded (RTF 0.74) |
| **Kokoro** (4 threads) | ~0.7 s | ~2.6 core-s | ~645 MB | ~10× Piper's CPU per callout |

Real-time is comfortable for both (RTF < 1 everywhere). The deciding factor for ACT users — **mid-raid** when callouts fire — is **how much CPU each callout steals**, which is why Piper @ 1 thread is the recommended default and Kokoro is the heavier opt-in.

---

## Voice catalog (languages, sources, generation)

**Languages shipped** — chosen from FFXIV demographics (English dominates; Russian is the #2 non-JP audience by Twitch share ~14%; German & French are official game languages; Spanish and Portuguese-BR are large grassroots communities). Japanese is deliberately excluded — that audience has its own mature TTS ecosystem.

| Locale | Piper | Kokoro | Notes |
|---|---|---|---|
| `en_US` | ✅ full | ✅ graded | biggest set (~25+ Piper voices) |
| `en_GB` | ✅ full | ✅ graded | ~13 Piper voices |
| `fr_FR` | ✅ full | ➖ | Piper-only (Kokoro French is ~1 thin voice) |
| `de_DE` | ✅ full | ➖ | Piper-only |
| `es_ES` (+ `es_MX`) | ✅ full | ➖ | Piper-only |
| `pt_BR` | ✅ full | ✅ 3 speakers | `pf_dora` / `pm_alex` / `pm_santa` |
| `ru_RU` | ✅ full | ➖ | Piper-only |

**Sources (single source of truth — the catalog is generated, not hand-maintained):**

- **Piper manifest — `voices.json`** (`rhasspy/piper-samples`): every voice with `language`, `quality`, `num_speakers`, per-file sizes. The C# `OnnxCatalog` is **generated from it**, filtered to the locales above and to **`medium` + `high`** tiers. Piper's `x_low`/`low` are 16 kHz and are **dropped**. A small dev script regenerates the catalog so it never drifts from upstream.
- **Kokoro grades — `VOICES.md`** (`hexgrad/Kokoro-82M`): grades voices **A–F** by training-data quality. The Kokoro side is seeded from the **A/B-grade English** voices + the pt-BR trio.

The UI carries **no external "preview/audition" link** — an engine-specific samples site (Piper-only, breaks if it goes down) isn't worth maintaining.

**Curation policy:**

- Ship the **full** Piper set per locale — download-on-demand means a long list has **no install cost**. Grouped by language, each entry tagged with its quality tier.
- Mark a few **Recommended** per language (en/pt-BR seeded from our benchmark; others by tier) so undecided users have a default.
- **Multi-speaker** Piper voices (e.g. `libritts`, `vctk` — dozens of speakers) expose only the **default speaker** (sid 0), or are omitted, to avoid a speaker-id explosion in the dropdown.
- **Kokoro is not a parallel** to Piper's seven locales — its toggle lists **English (by grade) + the 3 pt-BR speakers** only. If the user flips Quality→Kokoro while on a Piper-only locale (fr/de/es/ru), the voice list falls back to English with a one-line note.

The whole filtered catalog is a bounded **~70 voices** across the seven locales — not the hundreds of mixed-engine/-language assets in the raw k2-fsa release.

---

## UI Draft

Everything lives in the existing window chrome (left nav · content · bottom log). The nav gains **one new section** and TTS controls relocate into it.

### Navigation (one new section)

```
Before:  General · Sound · Information
After:   General · Text-to-Speech · Sound · Information
                   ^^^^^^^^^^^^^^^ new
```

- **Text-to-Speech** (new): Engine picker + everything that synthesizes speech (voice, volume, speed, ONNX Quality toggle, voice catalog/download, threads, models folder).
- **Sound** (kept): now hosts only the audio-output concerns shared by TTS *and* sound-file triggers — **Effects & leveling** + **Audio quality**. (The "Text-to-speech" header and the Voice/Volume/Speed cards move out.)
- **General** / **Information**: unchanged.

### The window (unchanged chrome)

```
┌─ ACT Tab: "Discord Triggers" ───────────────────────────────────────────┐
│ ┌────────────────┐ ┌────────────────────────────────────────────────┐  │
│ │ General        │ │                                                  │  │
│ │ ▸ Text-to-Speech│ │           (active page renders here)            │  │
│ │ Sound          │ │                                                  │  │
│ │ Information     │ │                                                  │  │
│ └────────────────┘ └────────────────────────────────────────────────┘  │
│ ┌─ Debug Log ───────────────────────────────────────────────────────┐  │
│ │ 06:14:22  Playing TTS for text: Stack for the tower               │  │
│ └───────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

### Text-to-Speech page — Engine = SAPI (default; what upgrading users see)

```
┌─ Text-to-Speech ─────────────────────────────────────────────┐
│ Engine                                                       │
│ ┌────────────────────────┐ ┌────────────────────────┐       │
│ │ ▦ Windows (SAPI)    ✓  │ │ ✦ ONNX — neural        │       │  ← choice-cards; SAPI selected
│ │   Built-in voices.     │ │   Natural voices,      │       │    (accent fill + ✓ on the pick)
│ │   Offline.             │ │   downloaded on pick.  │       │
│ └────────────────────────┘ └────────────────────────┘       │
│ Voice    [ Microsoft Zira Desktop          ▼ ]              │
│ Volume   ──────────●───────────                              │
│ Speed    ───────────●──────────                              │
│ ▶ Test voice                              [ ▶ Test ] (?)     │
└──────────────────────────────────────────────────────────────┘
```

SAPI behaves exactly as today; the Engine cards and the Test card are the only additions. Voice list comes from `SpeechSynthesizer.GetInstalledVoices()` (as now). The `(?)` beside **Test** and its enable rule are shared by both engines — see **Test button** below.

### Engine = ONNX (voice already installed)

```
┌─ Text-to-Speech ─────────────────────────────────────────────┐
│ Engine                                                       │
│ ┌────────────────────────┐ ┌────────────────────────┐       │
│ │ ▦ Windows (SAPI)       │ │ ✦ ONNX — neural     ✓  │       │  ← ONNX selected
│ │   Built-in voices.     │ │   Natural voices…      │       │
│ └────────────────────────┘ └────────────────────────┘       │
│ Quality  [ Fast │ Natural ]                                  │  ← segmented (OnnxFamily)
│          ↳ Piper — light on CPU, ~150 ms per callout.        │
│ Voice    [ en-US · Amy                       ▼ ]            │  ← searchable grouped picker
│ Speed    ───────────●──────────                              │
│          ↳ Volume is set under Sound › Auto-level            │
│ ▶ Test voice                              [ ▶ Test ] (?)     │
│ ▸ Advanced  (CPU threads · models folder)                    │
└──────────────────────────────────────────────────────────────┘
```

Selecting ONNX **folds in** the **Quality** toggle, the voice catalog, and the Advanced disclosure, and **hides the Volume slider** (neural loudness is the shared Auto-level on the Sound page). The Quality toggle (`OnnxFamily`) — **Fast** (Piper) / **Natural** (Kokoro) — scopes the Voice list to that family and swaps the description line (Natural → "Kokoro — most realistic; one 333 MB pack, heavier on CPU."). Switching back to SAPI restores Volume and hides the ONNX-only fields.

### Voice picker (ONNX) — search box over a locale-grouped, filtered list

The picker is a field button that opens a flyout with a search box on top of a grouped, live-filtered list (the catalog is too long for a plain dropdown). Search matches **locale, name, or tier** (e.g. `pt faber`, `en high`, `amy`).

```
│ Voice    [ en-US · Amy                        ▼ ]            │
│        ┌──────────────────────────────────────────┐          │
│        │ 🔍 Search voices…                        │          │  ← type to filter
│        │ pt-BR                                    │          │
│        │   ★ Faber · medium                  ✓    │  ★ recommended · ✓ installed
│        │     Jeff · medium               64 MB    │
│        │ en-US                                    │          │
│        │   ★ Amy · medium                    ✓    │
│        │     Ryan · high                110 MB    │
│        └──────────────────────────────────────────┘          │
```

Rows are grouped by locale (locale is the group header; the row shows name · tier). Installed voices show `✓`; not-yet-downloaded show their size; `★` marks the locale's recommended pick (also pre-selected). Flipping to **Natural** (Kokoro) shows its English voices (by grade) + the 3 pt-BR speakers — **not** a full mirror of Piper's locales. One 333 MB pack download unlocks every Kokoro speaker at once.

### Voice not downloaded yet — the inline download strip

The strip sits under the Voice picker and has three states:

```
needs download   ⚠ This voice isn't downloaded yet.        [ Download · 110 MB ]
downloading      ⏬ Downloading Ryan… 58%   ▓▓▓▓▓▓▓▓░░░░░░░░
done             ✓ Ryan is ready.    ← persists until another voice is picked
```

Test stays disabled until the voice is installed. The **success confirmation persists** (it clears only when another voice is picked) so a finished download isn't an abrupt vanish — large packs run for a while and the user may walk away; the picker's permanent `✓` is the lasting marker. **Kokoro** ⇒ one 333 MB pack download flips every Kokoro speaker to `✓` at once. The download milestones (URL, size, extraction, install path) and the install-state scan results (on load, on a models-folder change) are mirrored into the **Diagnostics log**, which **auto-scrolls to follow new entries** unless the user has scrolled up to read history.

### Test button (both engines)

**Test** plays a sample line through the bot, so it routes audio through the bridge → Discord — and TTS only actually reaches a channel once the bot has **joined a voice channel** (the `PlayTtsMethod` delegate is wired in `OnJoinedChannel`). This is true for SAPI too (it still sends PCM via `SpeakPcm`). So:

```
In a voice channel + voice ready  →  [ ▶ Test ] (?)     ← enabled
Not in a channel                  →  [ ▶ Test ] (?)     ← greyed/disabled
                                                 └─ click (?) ─► popover:
                                                    "Test plays through the bot.
                                                     Connect and join a voice
                                                     channel on the General page
                                                     first."
```

- **Enable rule:** `Test.CanExecute = InChannel && voiceReady`. `InChannel` is "bot is currently in a voice channel" — the same state that enables **Leave** today (`CanLeave`); a dedicated `IsInChannel`/`InChannel` flag is cleaner than overloading `CanLeave`, but it's the same signal. `voiceReady` is "a SAPI voice is selected" or, for ONNX, "the chosen `OnnxVoice` is installed." (So Test is also disabled for a not-yet-downloaded ONNX voice, per the mockup above — two independent reasons it can be greyed.)
- **Note the gate is *in-channel*, not merely *connected*** (`IsConnected` = bot logged in, but not necessarily in a channel). Being logged in isn't enough — audio goes nowhere until joined.
- **The `(?)`** is a small round help glyph beside Test, **always clickable even when Test is disabled**, opening a short popover explaining the requirement. The VM already tracks the connect/join lifecycle (`IsConnected`, `CanJoin`, `CanLeave`), so this binds to existing state.

### Advanced expanded (ONNX only, rarely touched)

```
│ ▾ Advanced                                                   │
│    CPU usage      ( ● Low · 1 thread ) ( Balanced · 2 ) ( Fast · 4 )│
│                   ↳ Low is gentlest while gaming (recommended)│
│    Models folder  [ %APPDATA%\ACT_DiscordTriggers\models  ] [ Browse… ]│
│                   ↳ where downloaded voices are stored       │
```

CPU usage maps to `sherpa-onnx` `numThreads` (`TtsThreads`). **Models folder** is `ModelsDir` — where C# downloads and looks for voices; default `%APPDATA%\ACT_DiscordTriggers\models`, editable via a folder picker. Both hidden by default; most users never open Advanced.

### Navigation / state flow

```
Text-to-Speech page
   │
   ▼
[Engine cards] ─ SAPI ─► voices = installed Windows voices ─► ready (Test)
   │
   └─ ONNX ─► [Quality: Fast | Natural] ─► [Voice picker] ─► installed? ─ yes ─► ready (Test)
                                              │
                                              └─ no ─► [Download · NN MB] ─► progress ─► ready
                                                      (Kokoro ⇒ one 333 MB pack unlocks all)
```

**Persistence:** `OnnxFamily` + `OnnxVoice` (and SAPI's `TtsVoice`) all persist. Flipping the **Engine** restores SAPI's vs ONNX's last voice. Flipping the **Quality** toggle has no per-family memory (single `OnnxVoice`): it lands on that family's **first-installed voice, else the catalog default**. Selections save with the rest of the plugin settings — no Apply button, same as the current sliders.

---

## Settings model (`PluginSettings.cs`)

```csharp
// --- TTS engine selection ---
public string TtsEngine  { get; set; } = "sapi";    // "sapi" | "onnx"
public string TtsVoice   { get; set; } = "";         // SAPI voice name (existing — kept)
public string OnnxFamily { get; set; } = "piper";    // "piper" | "kokoro"  (Quality toggle)
public string OnnxVoice  { get; set; } = "vits-piper-pt_BR-faber-medium"; // catalog id (piper) | speaker (kokoro)
public int    TtsThreads { get; set; } = 1;          // sherpa numThreads (Advanced)
public string ModelsDir  { get; set; } = "";         // empty ⇒ %APPDATA%\ACT_DiscordTriggers\models
// TtsVolume / TtsSpeed: existing. Volume applies to SAPI only; Speed applies to both.
```

- **Two ONNX fields** (`OnnxFamily` + `OnnxVoice`) per your call — no per-family voice memory; flipping the Quality toggle sets `OnnxVoice` to that family's first-installed-or-default. SAPI keeps its own `TtsVoice`.
- **`ModelsDir`** stored empty means "resolve to `%APPDATA%\ACT_DiscordTriggers\models` at runtime." C# resolves it to an **absolute** path before sending, so the bridge always receives a concrete directory.
- Bump `CurrentSchemaVersion` to **2** with a migration (`Settings/Migrations/`) that defaults the new fields — existing users land on `sapi`/their current voice and behave identically.
- **The bridge learns the ONNX voice from `SetConfig`, never from `SpeakText`.** `OnnxFamily`, `OnnxVoice`, `TtsSpeed`, `TtsThreads`, and the resolved `ModelsDir` persist in the settings POCO; on each push C# resolves them (plus the catalog's `sid`/`lang`) into the `ttsParams` descriptor the bridge actually reads (§c) — consistent with the IPC rule that commands don't carry settings. So these must **push** — flip the relevant VM props to `push:true`. (`TtsSpeed` pushes for both engines; the bridge simply ignores it under SAPI. `TtsVolume` stays `push:false` — SAPI-only, consumed in C#.)

---

## Volume & speed semantics (per engine)

| Slider | SAPI | ONNX |
|---|---|---|
| **Speed** | `SpeechSynthesizer.Rate = speed-10` (−10..10) | `generationConfig.speed` (map slider 0..20 → ~0.5..1.5×), delivered via **`SetConfig`** and applied by the bridge per synthesis |
| **Volume** | `SpeechSynthesizer.Volume = vol*5` (0..100) | **Hidden.** sherpa has no native volume; loudness is governed by the shared **Auto-level** on the Sound page. Not sent to the bridge at all. |

---

## Wiring (data flow)

### a. Populate the Voice list — entirely C#

- **SAPI:** `SpeechSynthesizer.GetInstalledVoices()` (as today; feeds the existing `Voices` collection).
- **ONNX:** the new **`OnnxCatalog`** (C#, Core; generated from `voices.json` — see Voice catalog) is a static list; the VM filters it by `OnnxFamily`, annotates each entry's `Installed` by scanning `ModelsDir`, and fills an `OnnxVoices` collection (each `OnnxVoiceItem` carries name/tier/locale + observable install-state). The view groups it by locale (a `CollectionViewSource`) and live-filters it from the picker's search box (a code-behind `Filter` predicate). **No bridge round-trip** — this works whether or not Discord is connected.

### b. Dispatch on speak (`DiscordTriggersViewModel.SpeakText`)

The real seam is the VM method ACT's `PlayTtsMethod` is wired to (`:235`), **not** a `DiscordPlugin.speak`. ONNX sends only the text — the voice/speed are already in the bridge's config:

```csharp
public void SpeakText(string text) {
  Log("Playing TTS for text: " + text);
  if (TtsEngine == "onnx")
    discord.SpeakOnnx(text);                            // NEW → SpeakText { text }
  else
    discord.Speak(text, TtsVoice, TtsVolume, TtsSpeed); // unchanged → SpeakPcm
}
```

### c. Protocol v6 — one new op

Bump `ProtocolConstants.Version` / `PROTOCOL_VERSION` 5 → 6; per `CLAUDE.md` update `Protocol.cs`, `protocol.ts`, dispatch in `pipe-server.ts`, and both `ProtocolTests.cs` + `protocol.test.ts`. The reply uses the **generic `Result` envelope** — no `*Result` ops.

| Op | Direction | Payload | Reply |
|---|---|---|---|
| `SpeakText` | C# → bridge | `{ text }` — bridge synthesizes with its **currently-configured** ONNX voice/family/speed | `Result {ok,error}` |

- **No `ListVoices` / `DownloadVoice` / `DownloadProgress`** — provisioning is entirely C#-side (§f). Everything the bridge needs to pick the voice arrives in `SetConfig` as the **resolved synth descriptor `ttsParams`** — an extensible `{string:string}` bag riding *alongside* the settings POCO (not inside it; it's derived, not persisted): `{ engine, family, modelDir (absolute), sid, lang, speed, threads }`. C# fills it (`OnnxSynthParams.Resolve`) only for an **installed** catalog voice, so the bridge stays a pure validated synth with no catalog/speaker knowledge. Adding a synth knob extends the bag with no DTO churn on either side.
- `SpeakText` deliberately carries **only `text`**, matching the IPC rule that commands don't duplicate settings (like `SpeakPcm`, which carries no fx params).
- **Random-fx is bridge-rolled** from `config.randomFx`/`fxChance`, same as `SpeakPcm`.

### d. Bridge synthesis (new `tts.ts`, called from `pipe-server.ts`)

- **The bridge resolves the active voice from `SetConfig`'s `ttsParams`, not from `SpeakText`.** Each config push carries the already-resolved descriptor (`family`, absolute `modelDir`, `sid`, `lang`, `speed`, `threads`); the bridge (re)loads the model when `modelDir`/`threads` change. The `*.onnx` inside is found by scanning the dir (as the benchmark does).
- Single loaded model kept (keyed `modelDir#threads`) — a neural model is 200–650 MB, so switching Piper voices reloads while switching Kokoro speakers (same pack/dir) does not; `numThreads` from the descriptor; **warm once on channel join** (a throwaway `generateAsync`) so the first real callout isn't cold (benchmark finding #5).
- **`sid` and the espeak `lang` are baked into the catalog at generation time** (`tools/gen-onnx-catalog`), not held in a bridge-side map: the bridge takes `sid` + `generationConfig.extra.lang` straight from `ttsParams`. This keeps the crash-critical espeak id (an unknown one hard-exits the process) in one vetted place. Piper: `sid 0`, `lang ''` (the model carries its own espeak config). **Kokoro = one instance** serving all speakers, `lang` per call.
- `SpeakText { text }` → `generateAsync` on the loaded instance → resample → existing `_enqueue`.
- Synthesize with **`generateAsync`** (off the event loop). Two ways to consume the stream:
  - **(A) Assemble-then-enqueue (recommended v1):** accumulate `onProgress` chunks into the full buffer, convert Float32 mono → 16-bit → `resampleStereo16` → 48 kHz stereo, then hand to the **existing** `_enqueue` (fx → normalize → declick → `mixer.addVoice`). Preserves global-RMS normalize and effect tails. Latency is just synth time (~150 ms Piper), already imperceptible.
  - **(B) True streaming into the mixer (future):** push resampled chunks into a live streaming mixer voice as they arrive. Lower first-audio latency, **but** global-RMS `normalize` (and tail-bearing effects) need the whole clip — streaming would require a streaming leveler or skipping normalize for ONNX. **Not v1.**

  > Direction 4 ("stream audio while being generated") is satisfied at the *synthesis* layer by `generateAsync` regardless of which consumer we pick — the event loop (pipe reads, mixer's 20 ms frame delivery, Discord keepalives) is never blocked. (A) is the pragmatic choice because the existing leveling assumes a complete buffer; (B) is the optimization once a streaming leveler exists.

### e. Synthesis cache (direction 5)

Mirror `WavCache`, but key by the **current synth config + text** (config from the bridge's last `SetConfig`; only `text` from `SpeakText`) instead of path+mtime — key = `md5(OnnxFamily | OnnxVoice | TtsSpeed | text)`:

```
key = md5(family + "|" + onnxVoice + "|" + speed + "|" + text)
```

- Store the **synthesized + resampled 48 kHz/16-bit/stereo PCM** (the expensive part), i.e. the same stage `WavCache` stores for files — **before** `_enqueue`'s fx/normalize. That way the random-fx roll and auto-level still vary per playback while repeated callouts skip synthesis entirely.
- Lookup is cache-first: **hit** → straight to `_enqueue` (no `generateAsync`); **miss** → synthesize, store, `_enqueue`.
- LRU `Map`, same `maxEntries` discipline as `WavCache` (callouts are short; ~32 entries is trivial RAM). Everything that affects the samples (family/voice/speed/text) is in the key; `ModelsDir`/`TtsThreads` don't change the output, so config pushes never need to invalidate it.
- Volume is irrelevant to the cache — ONNX has no volume stage; loudness is applied downstream by Auto-level, which already runs per playback in `_enqueue`.

### f. Voice provisioning — entirely C# (download on demand)

Owned by C# (Core) so it works with **no Discord connection** (the bridge may be down):

- **`OnnxCatalog` (generated, static):** records of `{ Id, Family, Locale, DisplayName, Quality, Sid, DownloadId, SizeMB, Recommended }`. The Piper rows are **generated from `voices.json`** (filtered to our locales + `medium`/`high`; see Voice catalog) by a dev script, not hand-typed; `DownloadId == Id`. All Kokoro speaker rows are seeded from `VOICES.md` grades + the pt-BR trio and share `DownloadId = "kokoro-multi-lang-v1_0"`. (`Quality` = Piper tier / Kokoro grade; `Recommended` flags a per-language default.)
- **Download URL is deterministic** (proven by the benchmark's `run-bench.js`):
  `https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/<DownloadId>.tar.bz2`
- **Install-state:** scan `ModelsDir/<DownloadId>/` for the model `.onnx`. To avoid half-downloaded false positives, **download + extract to a temp dir, then atomically move into place** — the directory existing means complete.
- **Download:** `HttpClient` GET with content-length → progress events drive the bar + Debug Log.
- **Extraction (`.tar.bz2`):** Windows' built-in **`tar.exe`** (bsdtar/libarchive, with bzip2) via `Process.Start` — `tar -x -f <archive> -C <stagingDir>`. **No managed archive library.** This is a deliberate choice: ACT loads every plugin into one shared process, and managed archive libs collide there — OverlayPlugin bundles `SharpCompress` 0.24 (and ACT itself bundles `ICSharpCode.SharpZipLib` 0.85), so a Costura-merged copy of either gets hijacked at bind time by the already-loaded version (`MethodNotFound`). `tar.exe` has no managed identity to collide on. It ships with Windows 10 1803+ / Windows 11; absence is reported as a clear error, not a crash. This also *replaces* the previously-considered Node `unbzip2-stream`/`tar` deps — provisioning no longer touches the bridge.
- **Kokoro special case:** every Kokoro speaker maps to the single 333 MB pack; the UI shows one "Download · 333 MB", after which all Kokoro voices are `✓`.
- Nothing bundled. Base install adds only the `sherpa-onnx-node` runtime to the bridge (~21 MB native DLLs + `.node`); models arrive on demand.

### g. Packaging (`build.ps1` + `esbuild.config.mjs` + `.csproj`)

- **Bridge (native):** `sherpa-onnx-node` is a native addon — add it and its platform binary to the esbuild `external` list and stage the runtime files into `dist\node_modules` via `$externals` (same mechanism as `@snazzah/davey` et al.):
  ```
  'sherpa-onnx-node',
  'sherpa-onnx-win-x64',   # onnxruntime.dll + sherpa-onnx-*.dll + sherpa-onnx.node (~21 MB)
  ```
  The addon loader (`sherpa-onnx-node/addon.js`) probes `./node_modules/sherpa-onnx-win-x64/sherpa-onnx.node` and siblings, so staging that package next to the bridge is sufficient (no `LD_LIBRARY_PATH` on Windows). **Confirm `build.ps1`'s `BRIDGE_READY` self-test still passes** with the addon present — it's the packaging regression guard and must not be weakened.
- **C# (managed):** no archive `PackageReference` — extraction shells out to Windows `tar.exe`, so there is no managed archive dependency to Costura-merge (and nothing to collide with another plugin's bundled copy). No Node bz2 deps either — extraction is C#-side.

---

## Safety guard (non-negotiable)

An unknown espeak `lang` (e.g. the non-existent `en-gb` — the real id is `en-gb-x-rp`) or a missing model makes `sherpa-onnx` **hard-`exit()` the entire bridge process** — which would drop the Discord voice connection mid-raid. Defense on both sides:

- **C# gates input:** it only pushes an `OnnxVoice` that is **installed**, and `OnnxFamily`/voice come from the curated catalog — so the bridge never receives an unknown voice or a bogus `lang`. Test/playback stay disabled until the chosen voice is downloaded.
- **Bridge validates on `SetConfig`:** before loading, confirm the model files exist under `ModelsDir/<DownloadId>/` and (for Kokoro) that the speaker is in its speaker→lang map. If validation fails, **refuse to load and report unable** rather than calling `generateAsync`; a `SpeakText` with no valid loaded voice **logs + skips** the callout. Bad input must never reach espeak, and the bridge must never crash on it.

---

## Implementation plan (build order)

Ordered per the chosen sequence: **catalog → relocate UI → build ONNX UI (no persistence) → settings → download → the rest.** The front half is pure C#/UI and **bridge-independent** — the whole settings/UI/download experience is built and testable before the bridge can synthesize a word. Each step is independently shippable; "Done when" is the acceptance check.

### 1. Build the catalog (C#, data only — no UI, no settings)

- **Goal:** a generated, queryable voice catalog with install-state. No UI, no persistence, no bridge.
- **Work:** define `OnnxVoiceInfo` (`{ Id, Family, Locale, DisplayName, Quality, Sid, DownloadId, SizeMB, Recommended }`); a **dev script** that pulls Piper `voices.json`, filters to the seven locales + `medium`/`high`, and emits the Piper rows, **hand-seeding** the Kokoro rows (top-graded English from `VOICES.md` + the pt-BR trio) — written to **`onnx-voices.json`, embedded as a .NET resource** in Core (data-only, refreshable without code changes, ships inside the DLL). `OnnxCatalog` reads that resource and exposes static accessors (`All`/`ByFamily`/`Locales`/`Find`); `IsInstalled(voice, modelsDir)` directory scan (Piper = any `*.onnx`; Kokoro = `model.onnx` + `voices.bin`); `ResolveModelsDir(settingValue)` → `%APPDATA%\ACT_DiscordTriggers\models` when empty.
- **Files:** `ACT_DiscordTriggers.Core/Tts/OnnxVoiceInfo.cs`, `OnnxCatalog.cs`, `Tts/onnx-voices.json` (embedded; **generated**), `tools/gen-onnx-catalog/gen.mjs`.
- **Done when:** a unit test enumerates voices per family/locale and computes install-state against a temp dir. ✅ **Done** — 65 voices (49 Piper / 16 Kokoro), 12 passing tests in `OnnxCatalogTests.cs`.

### 2. Relocate the existing TTS UI to the new section (behavior-preserving)

- **Goal:** move today's SAPI Voice/Volume/Speed into a new **Text-to-Speech** nav section — no new fields, no behavior change. Done as its own step so the *move* is decoupled from the *new feature*.
- **Work:** add `TabItem Header="Text-to-Speech"` between General and Sound; move the existing Voice/Volume/Speed cards out of the Sound page into it (bindings unchanged: `Voices`, `TtsVoice`, `TtsVolume`, `TtsSpeed`). Sound keeps Effects & leveling + Audio quality.
- **Files:** `DiscordTriggersView.xaml`.
- **Done when:** the plugin loads, the new tab shows the SAPI controls, and TTS works exactly as before — nothing else changed.

### 3. Build the ONNX UI from the catalog (no settings persistence yet)

- **Goal:** the full new UI visually working against the catalog + in-memory VM state; selections deliberately **not** saved yet.
- **Work:** Engine **choice-cards** (SAPI \| ONNX) with folding; **Quality** segmented toggle (**Fast**/Piper \| **Natural**/Kokoro); **searchable, locale-grouped voice picker** (field button → flyout with a search box over a `CollectionViewSource`-grouped, live-filtered `OnnxVoices` list — filter in code-behind so Core stays WPF-free; `★`/`✓`/size rows); inline **download strip** (needs-download → progress → persistent success), simulated in-memory for this step; Advanced disclosure (CPU-usage segmented + Models-folder field with a working folder picker); **Test** card + `(?)` help popover with the in-channel gate. VM holds transient properties (`Engine`/`OnnxFamily`/`SelectedOnnxVoice`/`TtsThreads`/`ModelsDir`) — no `PluginSettings` yet — exposed as paired bools so the cards/segments two-way bind without a converter. `OnnxVoiceItem` wraps a catalog row with observable `Installed`.
- **Files:** `DiscordTriggersView.xaml` (+`.cs`), `Theme.xaml` (new component styles + glyphs), `Core/ViewModels/DiscordTriggersViewModel.cs`, `Core/ViewModels/OnnxVoiceItem.cs`.
- **Done when:** you can flip Engine/Quality, search the catalog, watch rows show `✓`/size, run a (simulated) download to completion, toggle Advanced — all visually correct; a restart loses the selection (expected — not wired yet). ✅ **Done** — committed `44cacb7`; full `check.ps1` green.

### 4. Set up settings (persist + migrate + push)

- **Goal:** the UI state survives restart and reaches the bridge config.
- **Work:** add `TtsEngine`/`OnnxFamily`/`OnnxVoice`/`TtsThreads`/`ModelsDir` to `PluginSettings`; bump `CurrentSchemaVersion` to **2** + a `Settings/Migrations/` v1→v2 that defaults them; back the step-3 VM props with settings (`FromSettings`/`ToSettings`); make the bridge-relevant props `push:true` (SetConfig); resolve empty `ModelsDir` to the absolute default before sending.
- **Files:** `PluginSettings.cs`, `Settings/Migrations/*`, VM load/save/push.
- **Done when:** selections persist across restart; `SetConfig` carries the new fields; the migration test is green; the SAPI path is unchanged. ✅ **Done** — committed `0eeff82`; `V1ToV2` registered, VM persists/pushes the five fields, a saved-but-removed voice warns and falls back; full `check.ps1` green and verified live in ACT (v1 config upgrades in place, token preserved). `ModelsDir` is held resolved-absolute in the VM, so `ToSettings` emits an absolute path for both disk and wire.

### 5. Download function (C#)

- **Goal:** `⬇` voices become installable straight from the UI — with no Discord connection.
- **Work:** `OnnxDownloader` — `HttpClient` GET `…/tts-models/<DownloadId>.tar.bz2` with content-length → progress; extract via Windows **`tar.exe`** into a staging dir on the same volume; **atomic `Directory.Move`** into `ModelsDir/<DownloadId>/`; refresh install-state. UI: `[Download · NN MB]` row, progress bar, Debug-Log mirror; enable Test + flip `✓` on completion; Kokoro = one pack unlocks all speakers. No managed archive `PackageReference`.
- **Files:** `ACT_DiscordTriggers.Core/Tts/OnnxDownloader.cs`, VM download command, `.csproj`.
- **Done when:** pick an uninstalled voice → Download → progress → `✓`; a cancelled/failed download leaves **no** half-installed dir. ✅ **Done** — `OnnxDownloader` (HttpClient + `tar.exe`, staged on-volume + atomic move) replaces the step-3 simulation; the VM re-scans on-disk install-state after a download and whenever `ModelsDir` changes; download is cancelled on teardown. `OnnxDownloaderTests` covers the publish/replace logic (pure FS), a real `tar.exe` round-trip, corrupt-archive cleanup, and an opt-in real-network download (asserts the full pack unpacked). **`tar.exe` is used instead of a managed archive lib** because ACT runs all plugins in one process and OverlayPlugin/ACT bundle older `SharpCompress`/`SharpZipLib` copies that hijack a merged copy at bind time (`MethodNotFound`); shelling out has no managed identity to collide on.

### 6. Bridge synthesis + protocol v6

- **Goal:** ONNX actually speaks in Discord.
- **Work:** add `sherpa-onnx-node` to `DiscordBridge-node` deps; new `tts.ts` — lazy-`require` the addon (so the bridge still starts/`BRIDGE_READY`s when it's absent), load `vits`/`kokoro` from the `ttsParams` descriptor (single loaded model, keyed `modelDir#threads`), **validation guard**, warm on join; `SpeakText { text }` handler → `generateAsync` (assemble `onProgress` chunks / resolved audio) → Float32→16-bit stereo → `resampleStereo16` → existing `_enqueue`. Protocol: add `SpeakText` + the SetConfig `ttsParams` bag to `Protocol.cs` + `protocol.ts`, bump Version 5→6, dispatch in `pipe-server.ts`; C# `OnnxSynthParams.Resolve` (the install gate) + `DiscordClient.SpeakOnnx(text)` + the `VM.SpeakText` engine switch.
- **Files:** `protocol.ts`/`Protocol.cs`, `pipe-server.ts`, `tts.ts`, `discord-host.ts`, `DiscordClient.cs`/`DiscordClientService.cs`/`OnnxSynthParams.cs`, VM; `OnnxVoiceInfo.cs` + `gen.mjs` (baked `lang`); `package.json`/`esbuild.config.mjs`/`build.ps1` (staged native runtime).
- **Done when:** with an installed voice + joined channel, a trigger/Test speaks via ONNX in Discord; a bad voice/lang logs + skips and never crashes the bridge. ✅ **Done** — protocol v6 (`SpeakText` + `ttsParams`); `sid`/`lang` baked statically into the catalog, the bridge holds no speaker map; `OnnxSynthParams.Resolve` emits the descriptor only for an installed voice; the native addon is lazy-required + staged (release ships `sherpa-onnx-win-x64`), so the `BRIDGE_READY` self-test stays green even when it's absent. Full `check.ps1` green.

### 7. Packaging

- **Goal:** the release ships the native runtime and still self-tests.
- **Work:** the esbuild `external` entries + `build.ps1 $externals` staging of `sherpa-onnx-node` + `sherpa-onnx-win-x64` already landed in step 6 (the release folder ships `sherpa-onnx-win-x64`'s `onnxruntime.dll` + `sherpa-onnx.node` + DLLs, ~21 MB). No managed archive dep to bundle — extraction shells out to Windows `tar.exe` (step 5). Remaining: confirm the staged addon actually *loads + synthesizes* in a real run (the `BRIDGE_READY` self-test passes without loading it, since the require is lazy).
- **Done when:** `pwsh ./build.ps1` produces a release that loads the addon and passes the `BRIDGE_READY` self-test. ✅ build + staging + self-test green; runtime-load + synthesis now confirmed automatically by the step-9 gated suites (`tts-synth`/`tts-e2e` and `OnnxBridgeE2ETests` actually load the staged addon and synthesize real audio), so the only thing left to a human is a subjective listen + the live in-Discord voice-channel path.

### 8. Synthesis cache

- **Goal:** repeated callouts skip synthesis.
- **Work:** bridge LRU keyed `md5(family|voice|speed|text)` → resampled PCM (stored **before** `_enqueue`), cache-first; mirror `WavCache`.
- **Done when:** a repeated line hits cache (log shows no `generateAsync`); fx/auto-level still vary per play.

### 9. Hardening + tests

- **Work:** extend `ProtocolTests.cs` + `protocol.test.ts` (SpeakText + version bump); settings v1→v2 migration test; a bridge synth smoke test; verify the dual-side lang/model validation guard.
- **Validation suite (built):** the synthesis path is confirmed end-to-end by automated, auto-skipping tests rather than only by eye. An **audio sink** (`ACT_DT_AUDIO_SINK=<dir>`) makes the bridge write every played clip as a 48 kHz/16-bit/stereo WAV and lets `SpeakText`/`SpeakFile`/`SpeakPcm` run without a Discord channel (capture mode) — production is unchanged when the var is unset.
  - `DiscordBridge-node/tests/tts.test.ts` — `parseTtsParams` branches, `sliderToSpeed`, `monoFloat32ToStereoInt16`, the WAV writer, the sink, and `OnnxTts.configure`'s file-validation (no addon/model needed; always runs).
  - `tts-synth.test.ts` — loads the real Piper + Kokoro models and asserts non-silent audio, the 22050/24000 rates, the baked pt-BR/en-GB Kokoro `lang` path, and that the speed slider changes clip length. Never feeds an invalid espeak `lang` (it would hard-`exit()` the runner).
  - `tts-e2e.test.ts` — spawns the real bridge with the sink, drives `SetConfig(ttsParams)`+`SpeakText` over the pipe, reads the WAV back; plus negatives (no voice → "not ready"; bad model dir → "unavailable" warn + skip; bridge stays up).
  - `ACT_DiscordTriggers.Tests/OnnxBridgeE2ETests.cs` — the same loop across the real C# boundary, resolving the descriptor through `OnnxSynthParams.Resolve` and asserting the captured WAV.
  - The real-model suites auto-skip unless the `sherpa-onnx-node` addon **and** the voice models are present (`ACT_DT_MODELS_DIR`, default `E:\dev\sherpa-onnx-test\models`); `BridgeProcess.ExtraEnv` injects the sink env per-child so concurrent integration tests are unaffected. `npm run tts:probe -- --model <dir> …` dumps a WAV for a subjective listen.
- **Done when:** `pwsh ./check.ps1` is green.
