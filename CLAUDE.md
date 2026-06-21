# CLAUDE.md

## Build & test

Run every gate at once (fail-fast, mirrors CI): `pwsh ./check.ps1`. Order: ESLint → XAML style → `build.ps1` → C# tests → Node tests. Flags: `-Fast` (skip `build.ps1` + the bridge-spawning C# integration tests; runs a standalone `tsc` typecheck instead), `-Fix` (eslint `--fix` + `xstyler` write mode, then test), `-Install` (`npm ci` first). The no-arg run is the full CI gate set.

Release build (produces `release/ACT_DiscordTriggers/`, the folder users drop into ACT's plugins dir):
```
cd DiscordBridge-node && npm ci && cd ..
pwsh ./build.ps1            # add -Zip to also emit ACT_DiscordTriggers.zip at the repo root
```
`build.ps1` does `dotnet build` (net48 — ACT only loads net48) → `tsc --noEmit` → esbuild bundle → stages `node.exe` + externals → spawns the bridge and asserts `BRIDGE_READY` on stdout → assembles the release folder. That self-test catches packaging regressions; **do not skip or weaken it**.

Bridge-only iteration (in `DiscordBridge-node/`):
- `npm run typecheck` — `tsc --noEmit`
- `npm run lint` — ESLint flat config (`eslint.config.mjs`), type-aware (`typescript-eslint` `recommendedTypeChecked`) over `src/` + `tests/` + `tools/`. Run in CI, not in `build.ps1` (lint is a code-quality gate, not a packaging prerequisite).
- `npm run bundle` — esbuild only (no staging/self-test)
- `npm test` — JS suite (protocol, framing, op dispatch; plus a Windows-only lifecycle suite that spawns the real bridge). Independent of `dist/`. Lifecycle tests skip on non-Windows (Windows named pipes). The real-ONNX suites (`tts-synth.test.ts` synthesizes through `OnnxTts`; `tts-e2e.test.ts` spawns the bridge with the audio sink and drives `SetConfig`+`SpeakText` to a WAV) auto-skip unless the `sherpa-onnx-node` addon and the voice models are both present — models default to `E:\ai`, override with `ACT_DT_MODELS_DIR`. `tts.test.ts` covers the parser/converter/sink/validation with neither.
- `npm run tts:probe -- --model <dir> [--family piper|kokoro --sid N --lang <espeak> --speed 0..20 --text "..." --out f.wav]` — synthesize one line through the real bridge synth path to a WAV for manual listening.

C# tests (net48, xUnit v3 — `xunit.v3` builds the test project as an `.exe`, run under VSTest via `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio` 3.x):
```
dotnet test ACT_DiscordTriggers.Tests/ACT_DiscordTriggers.Tests.csproj
```
- `BridgeIntegrationTests` spawns the real bridge from `dist/` — run `build.ps1` first or it fails with a "build the bridge first" message.
- Network integration tests (`*_RealNetwork_*` in `OnnxDownloaderTests`/`DiscordTriggersViewModelTests`) download a real ONNX voice pack from the k2-fsa `tts-models` release and drive the VM download command end-to-end. They are opt-in: `Assert.SkipUnless` gates them on the `ACT_DT_NETWORK_TESTS` env var, so they skip by default (CI included) and run with `ACT_DT_NETWORK_TESTS=1 dotnet test ...`.
- `OnnxBridgeE2ETests` drives the full C#→bridge ONNX path: it resolves the synth descriptor via `OnnxSynthParams.Resolve`, pushes it in `SetConfig`'s `ttsParams`, sends `SpeakText`, and asserts a non-silent 48 kHz/stereo WAV (captured via the bridge's `ACT_DT_AUDIO_SINK`, injected per-child through `BridgeProcess.ExtraEnv`). `Assert.SkipUnless` auto-skips unless `dist/` is built with the `sherpa-onnx-node` addon staged **and** the voice models are present (`ACT_DT_MODELS_DIR`, default `E:\ai`).
- Single test: `dotnet test --filter "FullyQualifiedName~<name>"`.

ONNX audio sink (diagnostic / test hook): set `ACT_DT_AUDIO_SINK=<dir>` and the bridge writes every played clip as a 48 kHz/16-bit/stereo WAV there, **and** lets `SpeakText`/`SpeakFile`/`SpeakPcm` run without a joined voice channel (capture mode) — this is what makes the synthesis path observable offline. Off (no behavioural change) when the var is unset.

XAML formatting (XAML Styler `xstyler`, pinned as a local dotnet tool in `.config/dotnet-tools.json`; settings in `Settings.XamlStyler` at the repo root, auto-discovered up-tree):
- `dotnet tool restore` once, then `dotnet xstyler -f <file.xaml> -c Settings.XamlStyler` to format. Add `-p` for a no-write check that exits non-zero on any unformatted file (`-r -d .` to sweep the tree).
- Run in CI as a code-quality gate, not in `build.ps1` (formatting is not a packaging prerequisite). `IndentSize` is 2 and `RemoveDesignTimeReferences` is false (keeps the view's `d:` design attributes).

CI:
- Needs `Advanced Combat Tracker.exe` to resolve the plugin reference — install ACT to `C:\Program Files (x86)\Advanced Combat Tracker\` (csproj fallback path) or copy the exe to `packages/`.
- Runs `npm run lint` + `dotnet xstyler -p` (XAML format check) + `build.ps1` + `dotnet test` + `npm test`; Node via `setup-node`, .NET SDK is preinstalled on the runner (no `setup-dotnet`).

## Architecture: two processes

The plugin must not open a Discord connection itself — Discord voice requires DAVE E2EE, which net48 (all ACT loads) can't do. Voice lives in a separate node process:

```
ACT (net48) --loads--> ACT_DiscordTriggers.dll (net48)
DLL --spawns "node.exe bundle.js <pipe>"--> node bridge (discord.js + @snazzah/davey)
DLL <--Windows named pipe, length-prefixed JSON--> node bridge
```

The production code is two net48 assemblies — the thin assembly ACT scans, and a UI-agnostic core that Costura merges into it:

- **`ACT_DiscordTriggers/`** (net48, `UseWPF` + `UseWindowsForms`): the entry assembly ACT loads, hosting `Costura.Fody`, the ACT reference, and `Microsoft.Xaml.Behaviors.Wpf` (Costura-merged). It only *defines* GAC-based types — `DiscordTriggersPlugin : IActPluginV1` (lifecycle, bridge discovery, diagnostics, assembly-resolve fallback; hosts the WPF view inside ACT's WinForms `TabPage` via `ElementHost`), `DiscordTriggersView : System.Windows.Controls.UserControl` (WPF XAML view + code-behind; `DataContext` is the Core ViewModel, settings glue, hooks `PlayTtsMethod`/`PlaySoundMethod`; TTS synthesized in-process via `System.Speech` → 48k/16/stereo PCM, sound files sent by path), and the `static` attached-property helpers `PasswordBoxBinding`/`LogListCopy` (bindable masked token + Ctrl+C log copy; `ViewBehaviors.cs`).
  - **Load-time invariant:** no type *defined in this assembly* may derive from / implement a Costura-merged dependency. ACT loads via `Assembly.LoadFrom` + `GetTypes()`, which resolves every defined type's base/interfaces *before* any managed code of ours runs (so before Costura's module-init resolver attaches). A merged-dep base type here = `GetTypes()` throws = "Invalid Plugin". WPF base types (`UserControl`) are GAC, so the view is safe; the MVVM ViewModel lives in Core (not here) because it derives from merged `CommunityToolkit.Mvvm`. `Microsoft.Xaml.Behaviors` is used **XAML-only** (`Interaction.Triggers` with the built-in `LaunchUriOrFileAction` for the Information-tab links) and never subclassed here; view helpers that would otherwise be `Behavior<T>` are written as `static` attached-property classes to stay object-based. `DiscordTriggersPlugin`'s ctor calls `CosturaUtility.Initialize()` and the lifecycle methods carry `[MethodImpl(NoInlining)]` to keep resolver attach deterministic.
- **`ACT_DiscordTriggers.Core/`** (net48 class lib; no Costura/WinForms/ACT): all UI-agnostic code, referencing `CommunityToolkit.Mvvm` + `System.Text.Json` (PackageReference) and `System.Speech` (GAC). Costura merges Core.dll + those deps into the single plugin DLL (they ride in transitively + copy-local), so the closure stays isolated from other ACT plugins. Organized by folder:
  - `Ipc/` (`ACT_DiscordTriggers.Core.Ipc`): `DiscordClient` (static facade), `DiscordClientService` (`IDiscordService` impl), `BridgeProcess.StartAndConnectAsync` (spawns node, scans stdout for `BRIDGE_READY`, connects the pipe), `PipeClient`, `DiagnosticsLog`.
  - `Protocol/` (`ACT_DiscordTriggers.Core.Protocol`): `Protocol.cs` — the C# wire-protocol DTOs + `Op`.
  - `Settings/` (`ACT_DiscordTriggers.Core.Settings`): `PluginSettings` POCO + versioned migration framework.
  - `ViewModels/` (`ACT_DiscordTriggers.Core.ViewModels`): `DiscordTriggersViewModel : ObservableObject` (the sole CommunityToolkit-derived type), `LogEntry`.
- **`DiscordBridge-node/src/`**: the bridge (TS, esbuild→`dist/bundle.js`). `bridge.ts` owns lifecycle + pipe server. `pipe-server.ts` does framing/dispatch. `discord-host.ts` wraps discord.js + `@discordjs/voice` (`StreamType.Raw`). `protocol.ts` mirrors `Protocol/Protocol.cs`.
- **`ACT_DiscordTriggers.Tests/`** (net48, xUnit; namespace `ACT_DiscordTriggers.Tests`): protocol/IPC/lifecycle unit tests + `BridgeIntegrationTests` (real bridge). `<ProjectReference>`s `ACT_DiscordTriggers.Core` directly (Core has no WinForms/ACT/Costura, so the reference is clean); Core's `<InternalsVisibleTo Include="ACT_DiscordTriggers.Tests" />` grants the internal-member access the tests need (`DiagnosticsLog.MergeInterleave`, the internal `PipeClient`/`BridgeProcess` types).

Lifecycle — no launcher / Job Object:
- Plugin shutdown: `process.Kill()`s node directly.
- Plugin dies (crash, Task Manager): OS closes the pipe → `bridge.ts` `socket.close` handler runs `host.disconnect()` + `process.exit(0)`.
- Don't add a launcher unless you can show both paths fail.

## Wire protocol — keep both sides in sync

Defined twice: `ACT_DiscordTriggers.Core/Protocol/Protocol.cs` (C# DTOs + `Op`) and `DiscordBridge-node/src/protocol.ts` (TS types + `Op`, consumed by `pipe-server.ts`). Both hold a protocol version (`ProtocolConstants.Version` / `PROTOCOL_VERSION`) that must match.

Three message kinds:
- **Commands** — .NET→bridge request/response. The reply is always the single `Result` envelope `{ op:"Result", reqId, ok, error, data? }`; C# correlates by `reqId` alone (no per-op `*Result` ops). Query payloads ride in `data` (`BridgeResponse<TData>` on the C# side).
- **Config** — the single `SetConfig` op carries the whole `PluginSettings` POCO verbatim (token included). The bridge reads the fields it knows, ignores the rest, and owns all interpretation (fx dice roll, quality tier→bitrate, normalize target→dBFS). `Connect` takes no args and logs in from the config's token.
- **Notifications** — bridge→.NET push (`Log`/`BotReady`/`Disconnected`), no `reqId`.

When you add/change an op:
1. Update `Protocol.cs`, `protocol.ts`, and dispatch in `pipe-server.ts`.
2. On an incompatible wire change, bump the version in both. The Hello handshake fails fast on mismatch. Adding a config field is additive (the bridge defaults what's missing, ignores what's unknown), so it does NOT bump the version.
3. Extend both `ProtocolTests.cs` and `tests/protocol.test.ts`.

Framing:
- Frame = little-endian uint32 length prefix + UTF-8 JSON, max 64 MiB.
- Requests carry `reqId`; the `Result` response echoes it.
- Binary `SpeakPcm` frame: 11-byte header `[0x01][reqId u32][sampleRate u32][bits u8][channels u8]` + raw PCM.
- `Log`/`BotReady`/`Disconnected` are server-pushed notifications (no `reqId`).

Two non-obvious bits to preserve on refactor:
- `PipeClient.DispatchFrame` runs notification handlers on a thread-pool task, not the read loop — `BotReady` calls back into `SendAsync`, whose response only arrives once the read loop is back at `ReadFrameAsync`. Synchronous dispatch deadlocks.
- `PipeClient.SendFrameAsync` does **not** `FlushAsync` — on named pipes that's `FlushFileBuffers`, which blocks until the peer drains. `WriteAsync` already buffers into the OS pipe.

## Bridge runtime: bundling & externals

Ship plain `node.exe bundle.js` (not Node SEA).

- Native / WASM / `__dirname` deps can't be statically bundled, so they're `external` in `esbuild.config.mjs`; the ones actually loaded at runtime are staged into `dist/node_modules/` by `$externals` in `build.ps1` (the esbuild banner puts that dir on `NODE_PATH`). The two lists aren't identical — esbuild also externalizes optional discord.js/prism-media deps we never load, which aren't staged. `Cannot find module '<x>'` at startup = a runtime-required external didn't get staged (npm hoists transitive deps, so `$externals` can silently miss one).
- esbuild uses `conditions: ['node', 'require']` so CJS exports resolve (`@discordjs/voice`'s `.mjs` flavour uses `createRequire(import.meta.url)`, which throws in CJS output). Don't drop it.
- Stdout discipline: `BRIDGE_READY pipe=<name>` is the **only** expected stdout line; the handshake reads stdout linewise with a 15 s deadline. Use `log.info`/`log.error` (→ `DiscordBridge.log`) or stderr for everything else.

## Audio format constraint

Discord voice is hard-wired to 48 kHz / stereo end-to-end; the wire frame and the Opus encoder input are 16-bit signed PCM. The sample rate / channel count is pinned in several places — change all of them, don't add a one-off conversion step:
- `DiscordClient.formatInfo` — C# TTS synthesis format (48k/16/stereo).
- the `48000/16/2` hard-coded in `DiscordClient`'s PCM sends, and the matching check in `pipe-server.ts`.
- `discord-host.ts` — `TARGET_SAMPLE_RATE` + `resampleStereoF32` (file decode/resample) and `StreamType.Raw`.
- `effects.ts` `SR`.

Sample format: the bridge's whole interior pipeline (decode → resample → fx → normalize → declick → mix) works in **interleaved float32 stereo** — nominal [-1, 1] but allowed to exceed between stages so headroom is preserved (an fx tail or a hot mix sum is pulled back down, not clipped mid-chain). int16 lives only at the two edges, both via `audio-format.ts`: the `SpeakPcm` wire frame is widened on ingest (`int16ToFloat32`), and `PcmMixer` sums voices in float64 then performs the pipeline's single int16 quantization at its output (`floatToInt16`) right before the Opus encoder. The diagnostic WAV sink also converts float→int16 at its boundary. Don't reintroduce per-stage int16 round-trips; `floatToInt16` is the single quantization point and the natural home for future TPDF dither / a master limiter.

## Packaging & releases

Packaging:
- Release archive is one top-level `ACT_DiscordTriggers/` folder: DLL + `node.exe` + `bundle.js` + `node_modules/` + `README.md`/`LICENSE`.
- `FindBridgeDir()` resolves the bridge next to the plugin DLL first, then ACT's `AppData\Plugins\Discord\`.
- Costura.Fody merges net48 managed deps into the DLL — keep new managed deps Costura-merged so the plugin stays single-file.

Releases:
- Push a `v*` tag → `release.yml` runs the full build + `build.ps1 -Zip` and publishes a GitHub Release. Tags containing `-` → pre-release.
- To cut one: bump `AssemblyVersion`/`FileVersion`/`Version` in `ACT_DiscordTriggers.csproj`, then `git tag vX.Y.Z && git push origin vX.Y.Z`.
