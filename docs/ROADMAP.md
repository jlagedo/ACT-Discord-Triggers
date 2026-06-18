# ACT Discord Triggers — Roadmap

Post-v2 feature roadmap. v2 shipped the DAVE-compatible two-process architecture
(net48 plugin + `node.exe` bridge over a named pipe). This document tracks the
features we want to build next.

Priorities are derived from community demand across the FFXIV trigger ecosystem
(Cactbot, Triggernometry, Triggevent, the original Makar8000 plugin's issue
tracker, Hojoring/TTSYukkuri, Dalamud TTS plugins) and adjacent-game callout
tools (WoW BigWigs_Voice / DBM VEM, EverQuest GINA). See **Sources** at the
bottom.

Legend — **Effort**: S (days) · M (1–2 weeks) · L (multi-week / architectural).
**Status**: 🔵 planned · 🟡 in progress · 🟢 done · ⚪ idea.

---

## Release sequencing

We group the top features into two themed releases that target ~80% of recurring
complaints, plus a longer-tail backlog.

- **v2.1 — "Reliability & Sanity"**: #2 auto-reconnect, #6 status panel, #3 queue
  management. Keeps users connected and stops audio spam.
- **v2.2 — "Voice Quality"**: #1 pluggable TTS providers, #10 pronunciation
  dictionary, #4 extra audio formats. Fixes the single biggest complaint —
  "mediocre Microsoft voices."
- **Backlog**: #5, #7, #8, #9 + honorable mentions, scheduled by demand.

---

## Top 10 features

### 1. Pluggable cloud/neural TTS providers 🔵 — Effort: M
**Provider abstraction for Azure Neural, Amazon Polly, ElevenLabs (user API key).**

The single most-requested capability across the ecosystem. The plugin currently
synthesizes TTS in-process via `System.Speech`; users repeatedly ask to escape
the default Microsoft voices (issues #71, #2).

- Add an `ITtsProvider` abstraction in `ACT_DiscordTriggers/`; `System.Speech`
  becomes the default implementation.
- New providers (Azure/Polly/ElevenLabs) take a user-supplied API key from
  settings and return audio that we resample to **48 kHz / 16-bit / stereo PCM**
  before base64 → bridge. Only the synth source changes; the wire format does not.
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

### 4. Extra audio formats (MP3 / OGG / etc.) 🔵 — Effort: S–M
**Play more than `.wav`.**

Open issue #67. Decided approach is **bridge-side**: unify all formats —
including WAV — on the `audio-decode` (audiojs) decoder, replacing the WAV-only
`wav` path in `discord-host.ts`. This is Phase 1 of a larger audio rework that
also tunes the Opus encoder and moves the internal pipeline to float32
end-to-end. Full design + phased plan: **[docs/AUDIO-PIPELINE.md](AUDIO-PIPELINE.md)**.

> Note: an earlier sketch routed formats plugin-side via NAudio
> `MediaFoundationReader`. Superseded — the bridge already owns file decode, so
> one decoder there covers every format and feeds the same 48k pipeline.

### 5. Selective routing — personal (local) vs raid-wide (Discord) 🔵 — Effort: M
**Tag which triggers go to Discord vs stay on local speakers.**

The Triggernometry raid-repository model the community praises. Lets organized
groups send only shared callouts to the channel and keep personal-only callouts
on local audio, so the channel isn't flooded.

- Match by trigger source / regex / category.
- UI to manage the routing rules.

### 6. Connection & health status panel 🔵 — Effort: S
**Live "Bridge ✓ / Voice ✓ / Bot ✓" indicator with last-error surfacing.**

Feature request #72. Cheap — the bridge already pushes `BotReady` /
`Disconnected` notifications; surface them plus the last error in the settings UI.

### 7. Multi-channel / multi-bot routing ⚪ — Effort: L
**More than one target voice channel / bot token.**

Frequently asked by organized raid groups (own channel per group, or different
alert classes to different channels). Bigger lift: the bridge currently models a
single client/host — would need multi-host support and protocol changes.

### 8. Per-message volume, ducking & normalization 🔵 — Effort: M
**Alarm louder than info; optional duck of game/music under a callout; loudness
normalize.**

Maps cleanly onto the fixed-format PCM pipeline. Per-priority gain + a normalize
pass so file-based and TTS callouts match loudness.

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

- Audio is hard-wired to **48 kHz / 16-bit signed / stereo PCM** end-to-end
  (`DiscordClient.formatInfo`; the `48000/16/2` in `DiscordClient`'s PCM sends +
  the `pipe-server.ts` check; `discord-host.ts` `TARGET_SAMPLE_RATE` /
  `resampleStereo16` / `StreamType.Raw`; `effects.ts` `SR`). New audio
  sources must conform; don't add a conversion step in the bridge.
- The wire protocol lives in **two places** — `DiscordAPI/Protocol.cs` and
  `DiscordBridge-node/src/protocol.ts` (+ dispatch in `pipe-server.ts`). Any new
  op updates both; bump `PROTOCOL_VERSION` on incompatible shape changes and
  extend both `ProtocolTests.cs` and `tests/protocol.test.ts`.
- Keep managed deps mergeable via Costura.Fody (single-file plugin DLL).
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
