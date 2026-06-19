# TTS Engines — UI Draft & Wiring

> **TL;DR** — Add user-selectable TTS engines: keep **Windows (SAPI)** (today's default, offline), and add two neural engines via **sherpa-onnx** — **Piper** (fast, light; the recommended pick) and **Kokoro** (more natural, heavier). The user picks an **Engine** and a **Voice** on the existing *Sound* page; neural voices are **downloaded on demand** (nothing ships in the box). SAPI is synthesized in C# as today; neural engines are synthesized **in the Node bridge** (sherpa-onnx is a native Node addon — it cannot run in net48), then rejoin the existing effects → normalize → Opus pipeline. This document is the UI draft (ASCII mockups + navigation) plus the wiring needed to make it work.

This is a design draft, not yet implemented. It captures the decisions taken so far so the build can proceed without re-litigating them.

---

## The one fact that shapes everything

**System.Speech (SAPI) runs in C#. Piper/Kokoro (sherpa-onnx) are a native Node addon — they can only run in the bridge process.**

So this is **not** "swap the synthesizer." It is "add a second synthesis *location*":

- **SAPI** → C# synthesizes 48 kHz/16-bit/stereo PCM and sends the existing `SpeakPcm` binary frame (unchanged).
- **Piper / Kokoro** → C# sends *text* + voice params via a new `SpeakText` op; the **bridge** synthesizes with sherpa-onnx, resamples to 48 kHz stereo, and hands the audio to the **same** effects/normalize/Opus path `SpeakPcm` already uses.

Both paths converge right after synthesis, so there is only one playback pipeline.

---

## Decisions taken

| Decision | Choice |
|---|---|
| Engines exposed in v1 | **SAPI + Piper + Kokoro** (all three) |
| Voice provisioning | **Download on demand** — nothing bundled; fetch from the k2-fsa release on first use |
| Default engine | **SAPI** (upgrading users see identical behavior) |
| Default neural CPU usage | **1 thread** (gentlest while gaming; see Benchmark below) |

---

## Benchmark basis (why these defaults)

Measured in-process via `sherpa-onnx-node` (full evaluation lives in the `sherpa-onnx-test` repo). Warm, per short callout (~2 s of audio):

| Engine | gen / callout | CPU work / callout | RAM (peak) | Notes |
|---|--:|--:|--:|---|
| **Piper** (1 thread) | ~150 ms | ~0.16 core-s | ~205 MB | 13× realtime; gentlest on a busy machine |
| **Piper** (4 threads) | ~66 ms | ~0.25 core-s | ~205 MB | faster wall-time, *more* total CPU work |
| **Kokoro** (1 thread) | ~1.7 s | ~1.7 core-s | ~645 MB | borderline single-threaded (RTF 0.74) |
| **Kokoro** (4 threads) | ~0.7 s | ~2.6 core-s | ~645 MB | ~10× Piper's CPU per callout |

Real-time is comfortable for both (RTF < 1 everywhere). The deciding factor for ACT users — who are **mid-raid** when callouts fire — is **how much CPU each callout steals**, which is why Piper @ 1 thread is the recommended default and Kokoro is the heavier opt-in.

---

## UI Draft

Everything below lives in the existing window chrome (left nav · content · bottom log) and the existing **Sound** page. Only two controls are genuinely new — an **Engine** dropdown and a **Test** button — plus a conditional download row.

### The window (unchanged chrome)

```
┌─ ACT Tab: "Discord Triggers" ───────────────────────────────────────────┐
│ ┌───────────┐ ┌─────────────────────────────────────────────────────┐  │
│ │ General   │ │                                                       │  │
│ │ ▸ Sound   │ │              (active page renders here)               │  │
│ │ Information│ │                                                       │  │
│ │           │ │                                                       │  │
│ └───────────┘ └─────────────────────────────────────────────────────┘  │
│ ┌─ Debug Log ───────────────────────────────────────────────────────┐  │
│ │ 06:14:22  Playing TTS for text: Stack for the tower               │  │
│ └───────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
```

Nav is unchanged: **General** (connection + channel), **Sound** (TTS + effects), **Information**. All TTS configuration happens on **Sound**.

### Sound page — default (SAPI; what upgrading users see)

```
┌─ Text-to-Speech ─────────────────────────────────────────────┐
│ Engine   [ Windows (SAPI)                    ▼ ]              │
│          ↳ System voices · offline · no download             │
│                                                              │
│ Voice    [ Microsoft Zira Desktop            ▼ ]   [ ▶ Test ] │
│                                                              │
│ Volume   ──────────●───────────                              │
│ Speed    ───────────●──────────                              │
│                                                              │
│ ▸ Advanced                                                   │
└──────────────────────────────────────────────────────────────┘
┌─ Effects && Leveling ────────────────────────────────────────┐
│ ☐ Random Sound FX      FX Chance ───●───  25%                │
│ ☑ Auto-level Volume    Target ──────●──  -20 dBFS            │
│ Audio Quality [ Medium (96 kbps) ▼ ]                         │
└──────────────────────────────────────────────────────────────┘
```

SAPI behaves exactly as today; the Engine row and Test button are the only additions.

### Engine → Piper (voice already installed)

```
┌─ Text-to-Speech ─────────────────────────────────────────────┐
│ Engine   [ Piper — neural, fast & light      ▼ ]              │
│          ↳ ~150 ms/callout · ~100 MB RAM · 1 CPU thread       │
│                                                              │
│ Voice    [ pt-BR · Faber (male)              ▼ ]   [ ▶ Test ] │
│                                                              │
│ Volume   ──────────●───────────                              │
│ Speed    ───────────●──────────                              │
│                                                              │
│ ▸ Advanced                                                   │
└──────────────────────────────────────────────────────────────┘
```

Changing the Engine dropdown refreshes both the description line and the Voice list.

### Voice dropdown open (Piper) — grouped by language

```
│ Voice    [ pt-BR · Faber (male)              ▲ ]              │
│        ┌──────────────────────────────────────────┐          │
│        │  pt-BR                                    │          │
│        │    ✓ Faber (male)                         │   ✓ = installed
│        │    ✓ Cadu (male)                          │          │
│        │      Jeff (male)            ⬇ 64 MB       │   ⬇ = needs download
│        │  en-US                                    │          │
│        │    ✓ Amy (female)                         │          │
│        │      Ryan (male) · high     ⬇ 110 MB      │          │
│        │  en-GB                                    │          │
│        │      Alan (male)            ⬇ 64 MB       │          │
│        └──────────────────────────────────────────┘          │
```

Installed voices show `✓`; not-yet-downloaded show their size.

### Picked a voice that isn't downloaded yet

```
┌─ Text-to-Speech ─────────────────────────────────────────────┐
│ Engine   [ Piper — neural, fast & light      ▼ ]              │
│ Voice    [ en-US · Ryan (male) · high        ▼ ]   [ ▶ Test ] │   ← Test disabled
│ Volume   ──────────●───────────                  (greyed)     │
│ Speed    ───────────●──────────                  (greyed)     │
│                                                              │
│ ⚠ This voice isn't downloaded yet.   [ Download · 110 MB ]   │
└──────────────────────────────────────────────────────────────┘
```

### During download

```
│ ⏬ Downloading Ryan (high)…   ▓▓▓▓▓▓▓▓░░░░░░░░  58 %           │
```

When it completes: the warning row disappears, `Test` + sliders enable, and the voice gains a `✓`. Progress is also mirrored into the Debug Log.

### Engine → Kokoro (the one-pack case)

```
┌─ Text-to-Speech ─────────────────────────────────────────────┐
│ Engine   [ Kokoro — neural, most natural     ▼ ]              │
│          ↳ ~0.7 s/callout · ~640 MB RAM · heavier on CPU      │
│ Voice    [ pt-BR · Dora (female)             ▼ ]   [ ▶ Test ] │
│                                                              │
│ ⚠ Kokoro voices aren't installed.   [ Download · 333 MB ]    │   ← one download
└──────────────────────────────────────────────────────────────┘     unlocks ALL
                                                                       Kokoro voices
```

Kokoro ships as a single multi-language pack: one download unlocks *every* Kokoro speaker, after which the Voice list is all-`✓`.

### Advanced expanded (rarely touched)

```
│ ▾ Advanced                                                   │
│    CPU usage   ( ● Low · 1 thread )  ( Balanced · 2 )  ( Fast · 4 )│
│                ↳ Low is gentlest while gaming (recommended)   │
```

Maps to sherpa-onnx `numThreads`. Hidden by default; most users never open it.

### Navigation / state flow

```
Sound page
   │
   ▼
[Engine ▼] ─── SAPI ───────────► voices = installed Windows voices ──► ready (Test)
   │
   ├──── Piper ──► [Voice ▼] ──► installed?  ── yes ──► ready (Test)
   │                               │
   │                               └─ no ──► [Download · NN MB] ──► progress ──► ready
   │
   └──── Kokoro ─► [Voice ▼] ──► pack installed? ── yes ──► ready (Test)
                                   │
                                   └─ no ──► [Download · 333 MB] ──► progress ──► ready
```

**Persistence:** each engine remembers its own last voice, so flipping Engine back and forth restores the previous pick per engine. Selections save with the rest of the plugin settings — no Apply button, same as the current sliders.

---

## Settings model (`PluginSettings.cs`)

```csharp
// --- TTS engine selection ---
public string TtsEngine   { get; set; } = "sapi";               // "sapi" | "piper" | "kokoro"
public string TtsVoice    { get; set; } = "";                   // SAPI voice name (existing — kept)
public string PiperVoice  { get; set; } = "pt_BR-faber-medium"; // remembered per-engine
public string KokoroVoice { get; set; } = "pf_dora";
public int    TtsThreads  { get; set; } = 1;                    // sherpa numThreads (Advanced)
```

Per-engine voice fields so switching engines remembers each choice. Bump `CurrentSchemaVersion` to **2** with a migration that defaults the new fields — existing users land on `sapi` and behave identically.

---

## Wiring (data flow)

### a. Populate the Voice list

- **SAPI:** C#-side via `SpeechSynthesizer.GetInstalledVoices()` (as today).
- **Piper / Kokoro:** the bridge owns the models, so add a **`ListVoices(engine)`** op that returns `[{ id, displayName, lang, installed, sizeMB }]`; the UI fills the combo (and the `✓` / `⬇` markers) from the response. Keeps the C# side from hard-coding a model catalog.

### b. Dispatch on speak (`DiscordPlugin.speak`)

```csharp
private void speak(string text) {
  switch (settings.TtsEngine) {
    case "sapi":             // unchanged: C# synthesizes PCM, sends SpeakPcm
      DiscordClient.Speak(text, settings.TtsVoice, vol, speed); break;
    case "piper":
    case "kokoro":           // NEW: send text, bridge synthesizes
      DiscordClient.SpeakText(settings.TtsEngine, VoiceId(), text, speed, vol, RandomEffectRoll());
      break;
  }
}
```

### c. Protocol v5 — new messages

Bump `ProtocolConstants.Version` 4 → 5; per `CLAUDE.md` update `Protocol.cs`, `protocol.ts`, dispatch in `pipe-server.ts`, and both `ProtocolTests.cs` + `protocol.test.ts`.

| Op | Direction | Payload |
|---|---|---|
| `SpeakText` | C# → bridge | `engine, voice, text, speed, volume, randomEffect` → synth + play (response reuses `SpeakResult`) |
| `ListVoices` | C# → bridge | `engine` → `[{ id, displayName, lang, installed, sizeMB }]` |
| `DownloadVoice` | C# → bridge | `engine, voice` → fetch tarball, extract to `voices/` |
| `DownloadProgress` | bridge → C# (push) | `voice, percent` → drives the progress bar |

### d. Bridge synthesis (new `tts.ts`, called from `pipe-server.ts`)

- Lazy `Map<voiceKey, OfflineTts>` cache; `numThreads` from settings; **warm once on join** (a throwaway `generate()`) so the first callout isn't cold.
- Use the canonical API: `new GenerationConfig(...)` + `generate({ text, generationConfig })`.
- **Kokoro = one instance**, language selected per call via `generationConfig.extra.lang` (`en-gb-x-rp`, `pt-br`, …).
- Float32 mono @ 24k/22.05k → 16-bit → **reuse `resampleStereo16`** to 48 kHz stereo → existing effects → normalize → Opus. Neural audio rejoins the existing pipeline immediately after synthesis.

### e. Voice provisioning (download on demand)

- Nothing bundled. Base install = plugin DLL + `sherpa-onnx-node` binaries (~tens of MB).
- Voices stored under `…\AppData\…\Plugins\Discord\voices\`.
- The **bridge holds the voice catalog** (id → k2-fsa tarball URL + size + lang), reusing the release URLs from the benchmark. `ListVoices` reports `installed` by scanning `voices/`; `DownloadVoice` curls + extracts the tarball, streaming `DownloadProgress`.
- **Kokoro special case:** all ~50 speakers map to the single 333 MB pack — the UI shows one "Download Kokoro voices · 333 MB", after which every speaker is selectable.

### f. Packaging (`build.ps1`)

`sherpa-onnx-node` is a native addon (`.node` + `onnxruntime` DLL). Add it to the esbuild `external` list and stage its runtime files into `dist/node_modules` via `$externals` (same mechanism as the other native deps). Confirm the `BRIDGE_READY` self-test still passes.

---

## Safety guard (non-negotiable)

An unknown espeak `lang` (e.g. the non-existent `en-gb` — the real id is `en-gb-x-rp`) or a missing model makes sherpa-onnx **hard-`exit()` the entire bridge process** — which would drop the Discord voice connection mid-raid. Therefore the bridge's `SpeakText` handler **must validate `voice`/`lang` against the installed catalog before calling `generate()`**, and on any synthesis failure **log + fall back to a safe path** (skip the callout, or hand back to SAPI) rather than let bad input reach espeak. This is the one place neural TTS can take down more than itself.

---

## Suggested build order

Each step is independently shippable and testable.

1. **Protocol v5 + `SpeakText` (Piper only), no UI** — hardcode one voice, prove bridge-synth → existing effects/Opus path plays in Discord.
2. **Packaging** — stage `sherpa-onnx-node` in `build.ps1 $externals`; confirm the `BRIDGE_READY` self-test still passes.
3. **UI** — Engine combo + `ListVoices` population + dispatch + Test button.
4. **`DownloadVoice` + progress + `[Download]` row.**
5. **Kokoro** — one-instance / per-call `extra.lang`, with the validation guard.
6. **Settings schema v2 migration + tests** (`ProtocolTests.cs`, `protocol.test.ts`, a bridge synth test).
