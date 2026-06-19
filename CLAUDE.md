# CLAUDE.md

## Build & test

Release build (produces `release/ACT_DiscordTriggers/`, the folder users drop into ACT's plugins dir):
```
cd DiscordBridge-node && npm ci && cd ..
pwsh ./build.ps1            # add -Zip to also emit ACT_DiscordTriggers.zip at the repo root
```
`build.ps1` does `dotnet build` (net48 — ACT only loads net48) → `tsc --noEmit` → esbuild bundle → stages `node.exe` + externals → spawns the bridge and asserts `BRIDGE_READY` on stdout → assembles the release folder. That self-test catches packaging regressions; **do not skip or weaken it**.

Bridge-only iteration (in `DiscordBridge-node/`):
- `npm run typecheck` — `tsc --noEmit`
- `npm run bundle` — esbuild only (no staging/self-test)
- `npm test` — JS suite (protocol, framing, op dispatch; plus a Windows-only lifecycle suite that spawns the real bridge). Independent of `dist/`. Lifecycle tests skip on non-Windows (Windows named pipes).

C# tests (net48, xUnit — pinned to the 2.x line; 3.x dropped net48 support):
```
dotnet test ACT_DiscordTriggers.Tests/ACT_DiscordTriggers.Tests.csproj
```
- `BridgeIntegrationTests` spawns the real bridge from `dist/` — run `build.ps1` first or it fails with a "build the bridge first" message.
- Single test: `dotnet test --filter "FullyQualifiedName~<name>"`.

CI:
- Needs `Advanced Combat Tracker.exe` to resolve the plugin reference — install ACT to `C:\Program Files (x86)\Advanced Combat Tracker\` (csproj fallback path) or copy the exe to `packages/`.
- Runs `build.ps1` + `dotnet test` + `npm test`; Node via `setup-node`, .NET SDK is preinstalled on the runner (no `setup-dotnet`).

## Architecture: two processes

The plugin must not open a Discord connection itself — Discord voice now requires DAVE E2EE, which net48 (all ACT loads) can't do. Voice lives in a separate node process:

```
ACT (net48) --loads--> ACT_DiscordTriggers.dll (net48)
DLL --spawns "node.exe bundle.js <pipe>"--> node bridge (discord.js + @snazzah/davey)
DLL <--Windows named pipe, length-prefixed JSON--> node bridge
```

- **`ACT_DiscordTriggers/`** (net48): the single production project — UI, settings, IPC, protocol all in one assembly. Organized by folder:
  - root: `DiscordPlugin.cs` — UI, settings glue, hooks `PlayTtsMethod`/`PlaySoundMethod`. TTS is synthesized in-process via `System.Speech` and sent to the bridge as 48k/16/stereo PCM; sound files are sent by path — the bridge decodes + resamples them.
  - `Ipc/` (namespace `ACT_DiscordTriggers.Ipc`): IPC client. `DiscordClient` is the static facade. `BridgeProcess.StartAndConnectAsync` spawns node, scans stdout for `BRIDGE_READY`, then connects the pipe. Also `PipeClient`, `DiagnosticsLog`.
  - `Protocol/` (namespace `ACT_DiscordTriggers.Protocol`): `Protocol.cs` — the C# wire-protocol DTOs + `Op`.
  - `Settings/` (namespace `ACT_DiscordTriggers.Settings`): `PluginSettings` POCO + versioned migration framework.
- **`DiscordBridge-node/src/`**: the bridge (TS, esbuild→`dist/bundle.js`). `bridge.ts` owns lifecycle + pipe server. `pipe-server.ts` does framing/dispatch. `discord-host.ts` wraps discord.js + `@discordjs/voice` (`StreamType.Raw`). `protocol.ts` mirrors `Protocol/Protocol.cs`.
- **`ACT_DiscordTriggers.Tests/`** (net48, xUnit; namespace `ACT_DiscordTriggers.Tests`): protocol/IPC/lifecycle unit tests + `BridgeIntegrationTests` (real bridge). Links the plugin's pure-C# sources (`Protocol/`, `Ipc/`, `Settings/`) via `<Compile Include>` rather than a `ProjectReference` (which would pull in the ACT exe + WinForms + Costura); moving those files means updating the linked paths in `ACT_DiscordTriggers.Tests.csproj`.

Lifecycle — no launcher / Job Object:
- Plugin shutdown: `process.Kill()`s node directly.
- Plugin dies (crash, Task Manager): OS closes the pipe → `bridge.ts` `socket.close` handler runs `host.disconnect()` + `process.exit(0)`.
- Don't reintroduce a launcher unless you can show both paths fail.

## Wire protocol — keep both sides in sync

Defined twice: `ACT_DiscordTriggers/Protocol/Protocol.cs` (C# DTOs + `Op`) and `DiscordBridge-node/src/protocol.ts` (TS types + `Op`, consumed by `pipe-server.ts`). Both hold a protocol version (`ProtocolConstants.Version` / `PROTOCOL_VERSION`) that must match.

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

Discord voice is hard-wired to 48 kHz / 16-bit signed / stereo PCM end-to-end. It's pinned in several places — change all of them, don't add a one-off conversion step:
- `DiscordClient.formatInfo` — C# TTS synthesis format.
- the `48000/16/2` hard-coded in `DiscordClient`'s PCM sends, and the matching check in `pipe-server.ts`.
- `discord-host.ts` — `TARGET_SAMPLE_RATE` + `resampleStereo16` (file decode/resample) and `StreamType.Raw`.
- `effects.ts` `SR`.

## Packaging & releases

Packaging:
- Release archive is one top-level `ACT_DiscordTriggers/` folder: DLL + `node.exe` + `bundle.js` + `node_modules/` + `README.md`/`LICENSE`.
- `FindBridgeDir()` resolves the bridge next to the plugin DLL first, then ACT's `AppData\Plugins\Discord\`.
- Costura.Fody merges net48 managed deps into the DLL — keep new managed deps Costura-merged so the plugin stays single-file.

Releases:
- Push a `v*` tag → `release.yml` runs the full build + `build.ps1 -Zip` and publishes a GitHub Release. Tags containing `-` → pre-release.
- To cut one: bump `AssemblyVersion`/`FileVersion`/`Version` in `ACT_DiscordTriggers.csproj`, then `git tag vX.Y.Z && git push origin vX.Y.Z`.
