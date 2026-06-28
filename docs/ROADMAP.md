# ACT Discord Triggers — Roadmap

Post-v2 feature roadmap. v2 shipped the DAVE-compatible two-process architecture
(net48 plugin + `node.exe` bridge over a named pipe). This document tracks the
features we want to build next.

**Status as of v2.1.2** (protocol v6). What has actually shipped since v2:

- ✅ **Neural TTS (ONNX)** — Piper/Kokoro voices synthesized in the bridge via
  `sherpa-onnx-node`, alongside the in-process `System.Speech` (SAPI) path. (#1, partial)
- ✅ **Extra audio formats** — `audio-decode` decodes WAV/MP3/OGG/FLAC/Opus in the
  bridge. (#4, done)
- ✅ **Local output mode** — global "bot vs local" output target; local plays the
  same mix on the host's default device via `audify`/WASAPI. (#5, partial — global
  switch, not per-trigger routing)
- ✅ **Loudness normalization + master limiter** — K-weighted (BS.1770) per-clip
  LUFS leveling (`normalize.ts`) and a look-ahead brickwall master limiter
  (`limiter.ts`). (#8, partial — no per-message gain or ducking)
- ✅ **Source conditioning** — sound files are DC-blocked, silence-trimmed, and
  edge-faded before effects (`source-conditioning.ts`, v2.1.2).
- ✅ **Basic status indicator** — connected/disconnected dot in the view. (#6, partial)

Still untouched: auto-reconnect/heartbeat (#2), queue management (#3), per-trigger
routing (#5), multi-bot (#7), per-message volume/ducking (#8), soundboard (#9),
pronunciation dictionary (#10), and cloud TTS providers (Azure/Polly/ElevenLabs, #1).

Priorities are derived from community demand across the FFXIV trigger ecosystem
(Cactbot, Triggernometry, Triggevent, the original Makar8000 plugin's issue
tracker, Hojoring/TTSYukkuri, Dalamud TTS plugins) and adjacent-game callout
tools (WoW BigWigs_Voice / DBM VEM, EverQuest GINA). See **Sources** at the
bottom.

Legend — **Effort**: S (days) · M (1–2 weeks) · L (multi-week / architectural).
**Status**: 🔵 planned · 🟡 in progress · 🟢 done · ⚪ idea.

---

## Release sequencing

We group the top features into themed releases that target ~80% of recurring
complaints, plus a longer-tail backlog.

What actually shipped diverged from the original two-release plan: **Voice Quality**
work (#1 neural TTS, #4 formats, #8 normalize/limiter) landed first across v2.1.x,
while the **Reliability & Sanity** set (#2 reconnect, #3 queue) is still open.

- **Shipped (v2.1.0–v2.1.2)**: ONNX neural TTS (#1, partial), extra audio formats
  (#4 ✅), local output mode (#5, partial), loudness normalize + master limiter
  (#8, partial), source conditioning, basic status dot (#6, partial).
- **Next — "Reliability & Sanity"**: #2 auto-reconnect, #6 full status panel, #3
  queue management. Keeps users connected and stops audio spam. *(largest gap)*
- **Then — "Voice Quality" finish**: #10 pronunciation dictionary, #1 cloud TTS
  providers (Azure/Polly/ElevenLabs). Closes out the "mediocre Microsoft voices"
  complaint beyond the local ONNX voices already shipped.
- **Backlog**: #5 per-trigger routing, #7 multi-bot, #8 per-message gain/ducking,
  #9 soundboard + honorable mentions, scheduled by demand.

---

## Top 10 features

### 1. Pluggable cloud/neural TTS providers 🟡 — Effort: M
**Provider abstraction for Azure Neural, Amazon Polly, ElevenLabs (user API key).**

The single most-requested capability across the ecosystem. Users repeatedly ask to
escape the default Microsoft voices (issues #71, #2).

**Done:** local **neural TTS via ONNX** (Piper + Kokoro) is shipped. The bridge
synthesizes through `sherpa-onnx-node` (`discord-host.ts` `OnnxTts`, `tts.ts`); the
C# side curates the voice catalog (`ACT_DiscordTriggers.Core/Tts/onnx-voices.json`,
per-voice baked LUFS), downloads packs, and selects engine via
`PluginSettings.TtsEngine` (`"sapi"` | `"onnx"`) + `OnnxFamily`/`OnnxVoice`. The new
`SpeakText` op carries text to the bridge for ONNX synth; SAPI still synthesizes
in-process and ships PCM via `SpeakPcm`.

**Remaining:** the two engines are hardcoded branches, not a true provider
abstraction, and there are **no cloud providers** (Azure/Polly/ElevenLabs).

- Add an `ITtsProvider`-style abstraction so engines (SAPI / ONNX / cloud) plug in
  uniformly instead of the current two-way branch.
- Cloud providers (Azure/Polly/ElevenLabs) take a user-supplied API key from
  settings and return audio resampled to **48 kHz / 16-bit / stereo PCM**. Only the
  synth source changes; the wire format does not.
- Cache synthesized clips by (text, voice) to cut latency and API cost.
- Keep keys in the existing settings store; never log them.

### 2. Robust auto-reconnect & connection resilience 🔵 — Effort: M
**Exponential-backoff reconnect, bridge voice-connection watchdog, heartbeat.**

Disconnects are the #1 bug class users hit (#81, #75, #66, #70, #74).

- Reconnect with backoff in `BridgeProcess` / `DiscordClient` when the pipe or
  bot drops.
- Bridge-side watchdog in `discord-host.ts` to re-establish the voice connection
  without a full restart.
- Heartbeat/ping op so both sides detect a half-open pipe (new op → bump
  protocol if shape changes; update `Protocol.cs` + `protocol.ts` together).

### 3. Audio queue management: dedup, rate-limit, priority 🔵 — Effort: M
**Configurable queue to tame trigger spam.**

Requested on the ACT forums ("audio trigger rate limit"); ZDPS ships the pattern.

- De-dupe identical messages within a configurable window.
- Max-N-callouts-per-second cap.
- Priority tiers (info / alert / alarm) so urgent callouts preempt chatter.
- Decide placement: queue on the plugin side (before base64) keeps the bridge
  simple and lets us drop work before synthesis.

### 4. Extra audio formats (MP3 / OGG / etc.) 🟢 — Effort: S–M
**Play more than `.wav`. — Shipped.**

Open issue #67, **done**. All formats — including WAV — now go through the
`audio-decode` (audiojs) decoder in the bridge (`audio-decode.ts`,
`decodeFileToFinalPcm` in `discord-host.ts`), covering WAV/MP3/OGG/FLAC/Opus and
feeding the shared 48k pipeline. The wire format (`SpeakFile`) is unchanged. This
was Phase 1 of the larger audio rework that also moved the internal pipeline to
float32 end-to-end. Full design + phased plan:
**[docs/AUDIO-PIPELINE.md](AUDIO-PIPELINE.md)**.

> Note: an earlier sketch routed formats plugin-side via NAudio
> `MediaFoundationReader`. Superseded — the bridge already owns file decode, so
> one decoder there covers every format and feeds the same 48k pipeline.

### 5. Selective routing — personal (local) vs raid-wide (Discord) 🟡 — Effort: M
**Tag which triggers go to Discord vs stay on local speakers.**

The Triggernometry raid-repository model the community praises. Lets organized
groups send only shared callouts to the channel and keep personal-only callouts
on local audio, so the channel isn't flooded.

**Done:** a **global** output target — `PluginSettings.OutputMode` (`"bot"` |
`"local"`) on the Output tab — routes *all* audio either to the Discord voice
channel or to the host's default device (`local-output.ts` via `audify`/WASAPI).
The whole DSP pipeline is shared by both modes.

**Remaining:** routing is all-or-nothing, not **per-trigger**. The selective model
still needs:

- Match by trigger source / regex / category to pick a destination per callout.
- UI to manage the routing rules.

### 6. Connection & health status panel 🟡 — Effort: S
**Live "Bridge ✓ / Voice ✓ / Bot ✓" indicator with last-error surfacing.**

Feature request #72.

**Done:** a single connected/disconnected status dot (`StatusHalo`/`StatusDot`/
`StatusText` in `DiscordTriggersView.xaml`, bound to the VM's `IsConnected`), plus
the diagnostics log panel that surfaces raw bridge messages.

**Remaining:** the dot collapses Bridge/Voice/Bot into one signal and doesn't
surface the last error. `Disconnected` notifications already carry a `reason` the
UI ignores. Split into per-layer indicators and show the last error inline.

### 7. Multi-channel / multi-bot routing ⚪ — Effort: L
**More than one target voice channel / bot token.**

Frequently asked by organized raid groups (own channel per group, or different
alert classes to different channels). Bigger lift: the bridge currently models a
single client/host — would need multi-host support and protocol changes.

### 8. Per-message volume, ducking & normalization 🟡 — Effort: M
**Alarm louder than info; optional duck of game/music under a callout; loudness
normalize.**

Maps cleanly onto the fixed-format PCM pipeline.

**Done:** loudness **normalization** is shipped — per-clip ITU-R BS.1770
K-weighted LUFS leveling to `PluginSettings.NormalizeTarget` (`normalize.ts`,
`k-weighting.ts`), with per-voice neural-TTS loudness baked into `onnx-voices.json`.
A channel-linked look-ahead **master limiter** (`limiter.ts`, `LimiterEnabled` /
`LimiterCeilingIndex`) rides overlapping voices down to a true-peak-safe ceiling.
Local mode also has a global output volume (`LocalOutputVolume`).

**Remaining:** no **per-message / per-priority gain** (alarm louder than info) and
no **ducking** of game/music under a callout. Both need priority metadata on the
wire and (for ducking) a side-chain or host-audio attenuation hook.

### 9. Hotkey soundboard / manual raid-lead callouts ⚪ — Effort: M
**Push canned callouts ("stack", "spread", "move") to the channel on a hotkey.**

WoW Obscurity / VEM model. Turns the plugin into a live comms tool, not just an
automation relay. Independent of ACT triggers.

### 10. Custom pronunciation / text-substitution dictionary 🔵 — Effort: S
**User-editable regex → spoken-text map applied before synthesis.**

Boss/ability names get mangled by TTS. TextToTalk ships exactly this. Low effort,
removes a constant low-grade annoyance. Applies regardless of TTS provider.

---

## Honorable mentions / backlog

- Per-voice-channel sound test button.
- Remote-control / web dashboard.
- Token setup wizard (reduce the OAuth/bot-token friction users hit).
- Per-user volume within the channel.
- Contextual mechanic callout library ("move/stack/spread") — content, not just routing.

---

## Architecture notes (carry forward when implementing)

- Audio is hard-wired to **48 kHz / 16-bit signed / stereo PCM** at the edges
  (`DiscordClient.formatInfo`; the `48000/16/2` in `DiscordClient`'s PCM sends +
  the `pipe-server.ts` check; `discord-host.ts` `TARGET_SAMPLE_RATE` /
  `resampleStereoF32` / `StreamType.Raw`; `effects.ts` `SR`). The bridge's interior
  pipeline now works in **interleaved float32 stereo**, quantizing to int16 once at
  the mixer output (`audio-format.ts`). New audio sources must conform; don't add a
  per-stage int16 conversion step in the bridge.
- The wire protocol lives in **two places** — `ACT_DiscordTriggers.Core/Protocol/Protocol.cs`
  and `DiscordBridge-node/src/protocol.ts` (+ dispatch in `pipe-server.ts`),
  currently at **protocol v6**. Any new op updates both; bump `PROTOCOL_VERSION` on
  incompatible shape changes and extend both `ProtocolTests.cs` and
  `tests/protocol.test.ts`.
- Keep managed deps byte-loadable from `libs/` (land them in Main's copy-local
  output so `build.ps1` stages them; the bootstrap's `AssemblyResolver` loads them).
- Don't reintroduce a launcher process — the single-`node.exe` lifecycle is
  intentional (see CLAUDE.md).

---

## Sources

Demand evidence behind the rankings:

- [Makar8000/ACT-Discord-Triggers issue #2 — pipe other TTS / better voices](https://github.com/Makar8000/ACT-Discord-Triggers/issues/2)
- [Makar8000/ACT-Discord-Triggers issues (#67 MP3, #71 voice options, #72 status, #81/#75/#66/#70/#74 disconnects)](https://github.com/Makar8000/ACT-Discord-Triggers/issues)
- [ACT Forums — Audio trigger rate limit feature request](https://forums.advancedcombattracker.com/discussion/427/audio-trigger-rate-limit-feature-request)
- [cactbot raidboss — TTS, info/alert/alarm tiers](https://github.com/OverlayPlugin/cactbot/)
- [Triggevent — easy triggers, callouts, raid tooling](https://triggevent.io/)
- [xpdota/event-trigger (Triggevent) — configurable TTS + on-screen callouts](https://github.com/xpdota/event-trigger)
- [karashiiro/TextToTalk — multiple TTS providers, triggers/exclusions, substitutions](https://github.com/karashiiro/TextToTalk)
- [karashiiro/TextToTalk discussion #153 — ElevenLabs support](https://github.com/karashiiro/TextToTalk/discussions/153)
- [Sebane1/RoleplayingVoiceDalamud — ElevenLabs/XTTS voices](https://github.com/Sebane1/RoleplayingVoiceDalamud)
- [BigWigsMods/BigWigs_Voice — TTS for boss abilities](https://github.com/BigWigsMods/BigWigs_Voice)
- [Blue-Protocol ZDPS — dedup + alert queue model](https://github.com/Blue-Protocol-Source/BPSR-ZDPS)
- [Obscurity Raid Callouts — raid-lead live comms model](https://github.com/obscurelyme/Obscurity-Raid-Callouts)
