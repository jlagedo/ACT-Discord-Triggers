# Design: Pluggable TTS Providers

Status: **proposed** · Owner: TBD · Last updated: 2026-06-17

Implements roadmap feature #1. This document is the single source of truth for
the pluggable-TTS work; the roadmap entry only links here.

---

## 1. Goal

Let users pick the voice engine that synthesizes ACT trigger callouts, instead of
being locked to the in-process Windows `System.Speech` voices (the #1 complaint
across the FFXIV/ACT community). Add high-quality cloud voices and local/offline
engines without destabilizing the net48 plugin.

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

2. **Zero new dependencies.** Node 24 ships global `fetch`. Every cloud provider
   is a REST call; if we **require providers to return PCM or WAV** (all the
   majors support it), no SDK and no audio-decoder package is needed. The
   `esbuild.config.mjs` `external:` list and `build.ps1` `$externals` list (which
   must stay in agreement) **do not change**. By contrast, the .NET path would
   pull in dying-support NuGet packages, native-DLL SDKs that fight Costura
   (Azure Speech SDK), and net48 TLS-1.2 workarounds.

3. **No new trust boundary.** API keys cross the named pipe and live in Node
   memory — but the **Discord bot token already does exactly that**
   (`InitRequest.token`). The pipe is local and per-user. An API key is no
   different from the token already trusted to it.

4. **Less IPC.** Today `System.Speech` ships a full base64 PCM blob per callout.
   `SpeakText` ships a short string; the bridge produces the audio. Smaller
   frames, one less synth→serialize→deserialize hop.

### Why `System.Speech` stays in .NET

It is Windows SAPI, in-process, and cannot move to Node. It is also the only
zero-download, zero-config, fully-offline option. Keep it exactly as-is on the
existing binary `SpeakPcm` path. It doubles as the **fallback**: if a bridge-side
provider errors or times out, the bridge returns `SpeakResult { ok:false }` and
the plugin re-synthesizes the same text via `System.Speech` and sends `SpeakPcm`.
A raid callout is never silently lost.

### Hard constraint

> **Providers MUST return 16-bit signed PCM or PCM-WAV.** This keeps the bridge
> dependency-free (no mp3/opus decoder). Request `pcm`/`wav`/`LINEAR16` output
> from every provider. This is a design rule, not an implementation detail — a
> provider that can only emit mp3 is not acceptable without first adding a
> decoder, which would break the zero-dependency promise.

---

## 3. Provider lineup

Chosen to match where the FFXIV/ACT/streamer community already is, plus the
quality and offline differentiators. (Adoption rationale: Polly is the gaming
workhorse — TextToTalk default + the Streamlabs "Brian" voice; Azure is the
familiar #2; ElevenLabs is the quality leader; Kokoro/Piper are the 2026
free/offline movement.)

| Provider | Where | Transport | Output requested | Ship phase |
|---|---|---|---|---|
| **System.Speech** | .NET (existing) | binary `SpeakPcm` | 48k/16/stereo PCM | shipped |
| **Amazon Polly** | bridge | REST | `pcm` (16-bit, 16 kHz) → resample | 1 |
| **Azure Neural** | bridge | REST | `raw-48khz-16bit-mono-pcm` (no resample) | 1 |
| **ElevenLabs** | bridge | REST | `pcm_24000` / `pcm_44100` → resample | 1 |
| **OpenAI-compatible** | bridge | REST (configurable base URL) | `pcm` / `wav` | 1 |
| Google Cloud TTS | bridge | REST | `LINEAR16` (wav) | later |

**The OpenAI-compatible connector does double duty:** a configurable base URL
points it at OpenAI cloud *or* at any local OpenAI-compatible server —
**Kokoro-FastAPI**, **openedai-speech** (Piper / Coqui XTTS) — capturing the
entire free/offline crowd with one provider and no dedicated local integration.

### Cost note (why premium providers are viable here)

The community's fear of premium TTS comes from high-volume use (voicing all chat
or NPC dialogue, where ElevenLabs can run ~$1000). **Raid callouts are the
opposite**: a small set of short, repeated phrases. Monthly character counts are
tiny, and the bridge cache (§6) makes repeats free after first synth. ElevenLabs
and OpenAI therefore cost pennies/month in this use case and are offered as
first-class.

---

## 4. Provider abstraction (TypeScript, in the bridge)

Providers only *fetch* audio. Resample / normalize / mix / cache stay centralized
in the existing pipeline — a provider cannot get the output format wrong.

```ts
export interface TtsResult {
    pcm: Buffer;        // 16-bit signed LE
    sampleRate: number; // provider's native rate; pipeline resamples to 48k
    channels: 1 | 2;    // pipeline up-mixes mono → stereo
}

export interface TtsProvider {
    readonly id: string;            // "polly" | "azure" | "elevenlabs" | "openai"
    synthesize(text: string, opts: TtsOpts, signal: AbortSignal): Promise<TtsResult>;
    listVoices?(signal: AbortSignal): Promise<VoiceInfo[]>;  // for the .NET dropdown
}

export interface TtsOpts {
    voice: string;
    model?: string;
    speed?: number;
    apiKey?: string;
    baseUrl?: string;   // OpenAI-compatible → local server or cloud
    region?: string;    // Azure / Polly
}
```

A `TtsRegistry` keyed by `id` holds the instances; `DiscordHost` keeps the
currently-selected provider + its `TtsOpts`, set via `SetTtsConfig` (§5).

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

## 5. Protocol changes

Bump `PROTOCOL_VERSION` **3 → 4** in both `DiscordAPI/Protocol.cs` and
`DiscordBridge-node/src/protocol.ts`. Update `pipe-server.ts` dispatch and extend
both `ProtocolTests.cs` and `tests/protocol.test.ts` (standing rule —
incompatible wire shape → version bump + both test suites).

### `SetTtsConfig` — global config push (mirrors `SetNormalization`)

The plugin owns the settings UI and pushes config to the bridge, exactly as it
already does for normalization.

```ts
interface SetTtsConfigRequest extends BaseRequest {
    op: 'SetTtsConfig';
    providerId: string;     // "" or "system" => bridge synth disabled; plugin uses SpeakPcm
    voice: string;
    model?: string;
    speed?: number;
    apiKey?: string;        // never logged (see §7)
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

`GetVoices` (optional, phase 2) can back a "refresh voices" button in the UI:
`GetVoicesRequest{ providerId }` → `GetVoicesResult{ voices: VoiceInfo[] }`.

### Plugin-side routing (the ACT hook)

`PlayTtsMethod` hands the plugin text:

- selected provider is `system`/empty → synthesize via `System.Speech`, send
  `SpeakPcm` (**unchanged existing path**).
- otherwise → send `SpeakText{ text }`. On `SpeakResult{ ok:false }`, fall back:
  synthesize via `System.Speech` and send `SpeakPcm`.

---

## 6. Caching

Reuse the spirit of `WavCache`, keyed on `hash(providerId, voice, model, speed,
text)` → resampled 48k PCM. Short callout phrases repeat constantly, so this:

- removes per-callout latency on repeats (cloud TTFA is 75–300 ms),
- drives recurring cloud cost to ~zero,
- makes premium providers practical (§3 cost note).

Bound the cache (LRU by entry count or bytes) and clear it on `SetTtsConfig`
change (voice/model swap invalidates entries).

---

## 7. Security & reliability

- **API keys**: stored in the plugin settings; pushed over the pipe via
  `SetTtsConfig`; held in Node memory only. Same trust level as the bot token,
  which already crosses this pipe. **Never** write keys to `DiscordBridge.log` —
  follow the existing discipline (the bot token is already kept out of logs).
- **TLS**: not a concern on the bridge side — Node handles modern TLS. (This is
  one of the reasons not to do this in net48.)
- **Timeouts / fallback**: each `synthesize()` takes an `AbortSignal` with a
  short deadline (e.g. 5 s). On timeout/error the bridge returns
  `SpeakResult{ok:false}` and the plugin falls back to `System.Speech` (§5).
- **Latency**: cloud adds 75–300 ms TTFA; cache (§6) hides it for repeats. Local
  servers (Kokoro/Piper) and `System.Speech` are near-instant.

---

## 8. Testing

- **Node unit tests** (`tests/`): each provider against a fake `fetch` — assert
  it requests PCM/WAV, parses the response into `TtsResult`, and surfaces HTTP
  errors as a rejected promise. Use the existing `FakeHost`/`FakeSocket` harness
  for `SpeakText`/`SetTtsConfig` dispatch.
- **Protocol tests**: round-trip `SetTtsConfig`/`SpeakText` on both sides; assert
  `PROTOCOL_VERSION` parity (the Hello handshake already fails fast on mismatch).
- **Pipeline test**: a fake provider returning 24 kHz mono PCM must come out
  48 kHz stereo after `upmixMonoToStereo16` + `resampleStereo16`.
- Bridge-side TTS does **not** need real API keys in CI — all provider tests run
  against a stubbed `fetch`.

---

## 9. Build / packaging impact

**None expected.** No new runtime npm dependencies (plain `fetch`, PCM/WAV
output), so:

- `esbuild.config.mjs` `external:` list — unchanged.
- `build.ps1` `$externals` staging list — unchanged.
- net48 plugin managed deps / Costura set — unchanged.

If a future provider forces an mp3/opus path, that is a separate decision: adding
a decoder dependency must be weighed against the §2 zero-dependency rationale, and
both the esbuild external list and `build.ps1` staging list must be updated
together (npm hoists transitive deps; the externals list silently misses them).

---

## 10. Implementation phases

1. **Seam (no behavior change):** introduce `TtsProvider`/`TtsRegistry` in the
   bridge with a single `system`-disabled path; add `SetTtsConfig` + `SpeakText`
   ops + version bump + tests. Plugin still uses `System.Speech`/`SpeakPcm` until
   a provider is selected. Proves the wiring end-to-end.
2. **OpenAI-compatible provider** (unlocks OpenAI cloud + local Kokoro/Piper) and
   **Amazon Polly** (the community workhorse).
3. **Azure Neural** + **ElevenLabs**.
4. **Caching** (§6) + UI: provider dropdown, dynamic config panel, voice list
   refresh, per-channel "test voice" button.
5. Later: **Google Cloud TTS**; optional move of `SpeakFile` decoding fully into
   the same provider pipeline.

---

## Appendix: source material

Provider landscape and gaming-community adoption that informed the lineup:

- [karashiiro/TextToTalk — FFXIV chat TTS: System/Polly/Azure/Uberduck/Websocket](https://github.com/karashiiro/TextToTalk)
- [TextToTalk #153 — ElevenLabs request, quality vs. cost](https://github.com/karashiiro/TextToTalk/discussions/153)
- [Amazon Polly TTS setup for FFXIV](https://www.youtube.com/watch?v=VC5B-CXbabI)
- [VRCWizard/TTS-Voice-Wizard — Polly/Azure/Google in VRChat](https://github.com/VRCWizard/TTS-Voice-Wizard/wiki/Amazon-Polly)
- [Twitch TTS — Streamlabs/StreamElements + Polly heritage](https://murf.ai/blog/twitch-text-to-speech)
- [Kokoro-FastAPI — OpenAI-compatible local server](https://github.com/remsky/Kokoro-FastAPI)
- [openedai-speech — OpenAI-compatible Piper/XTTS server](https://github.com/matatonic/openedai-speech)
- [MinhakaDev/FFXIV-TTS — Kokoro-based local TTS for FFXIV](https://github.com/MinhakaDev/FFXIV-TTS)
- [rhasspy/piper — standalone offline neural TTS](https://github.com/rhasspy/piper)
- [Azure TTS REST API (raw PCM output formats)](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/rest-text-to-speech)
- [AWSSDK.Polly (NuGet, .NET Standard 2.0) — REST also available](https://www.nuget.org/packages/AWSSDK.Polly/)
- [Best TTS APIs 2026 — latency/quality (Speechmatics)](https://www.speechmatics.com/company/articles-and-news/best-tts-apis-in-2025-top-12-text-to-speech-services-for-developers)
