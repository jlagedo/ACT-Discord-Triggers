# Migrating ACT‑Discord‑Triggers to a native net10 plugin

**Goal.** Port this plugin off the net48 ACT‑drop‑in model onto the **FFXIV Combat Tracker** host's
modern plugin contract (`Fct.Abstractions`) as a **native `IPlugin`** — no `.NET Framework 4.x`, no ACT
reference, no compat shim. The config UI is rewritten in **Avalonia**. The out‑of‑process node bridge,
the wire protocol, and the UI‑agnostic `Core` logic carry across essentially unchanged.

This is the **native path**, not the host's recompile‑shim path. Rationale: the UI is WPF and must be
rewritten for the Avalonia shell regardless (there is no WPF‑in‑Avalonia host), and the audio seam
collapses to a single `IAudioSink`. The shim's only value — "minimal code change" — is nullified by the
mandatory UI rewrite, so we target the modern contract directly.

Host references (in the `ffxiv-combat-tracker` repo, `E:\dev\ffxiv-combat-tracker`):
- `docs/PLUGIN-API.md` — the modern plugin contract (`IPlugin`, `IPluginHost`, `IAudioOutput`,
  `IUiContributor`).
- `docs/ACT-INTERFACE-MAP.md` §6/§16 — the legacy ACT audio seam this replaces.

## Source layout (what we are porting)

| Project | TFM | Role | Disposition |
|---|---|---|---|
| `ACT_DiscordTriggers` | net48 (WPF+WinForms) | entry assembly ACT loads; `IActPluginV1`, WPF view, Costura merge | **Replaced** by the native `Fct.Plugins.DiscordTriggers` project in **this** repo |
| `ACT_DiscordTriggers.Core` | net48 | UI‑agnostic: `Ipc/`, `Protocol/`, `Settings/`, `ViewModels/`, `Tts/` | **Retargeted** to net10‑windows, carried over |
| `DiscordBridge-node` | Node | out‑of‑process Discord/ONNX bridge | **Unchanged** (spawned as before) |
| `ACT_DiscordTriggers.Tests` | net48 | protocol/IPC/lifecycle tests | Retargeted with `Core` |

The new native plugin project (`Fct.Plugins.DiscordTriggers`, `IPlugin` + Avalonia UI) lives in
**this** repo alongside `Core` and the node bridge, so the whole Discord‑Triggers deliverable stays in
one place. It references `Core` by in‑repo `ProjectReference` and the host contracts
(`Fct.Abstractions`, `Fct.Abstractions.UI`) as **NuGet packages** from the host's local `.nupkg` feed
(`nuget.config` → `fct-local` → `../ffxiv-combat-tracker/dist/debug/packages`), pinned with
`ExcludeAssets="runtime"` (the `Private=false` equivalent — the host loader shares those into the
default `AssemblyLoadContext`, so they must not ship as private plugin deps; the host publishes no
public NuGet feed). See `libs/fct/README.md` for the refresh step.

## The entire ACT surface it touches (and the modern replacement)

Discord‑Triggers **consumes no combat data** — no `OnLogLineRead`, no encounter, no `AddCombatAction`
(verified: grep for LogLineRead/Encounter/CombatAction = 0). Its whole ACT contact surface lives in
`ACT_DiscordTriggers/DiscordTriggersPlugin.cs` + `DiscordTriggersView.xaml.cs`:

| Legacy ACT member | Used for | Modern replacement |
|---|---|---|
| `IActPluginV1.InitPlugin(TabPage,Label)` / `DeInitPlugin` | lifecycle | `IPlugin.InitializeAsync` / `DisposeAsync` |
| `ActGlobals.oFormActMain` | host hub | `IPluginHost` (injected) |
| `PluginGetSelfData(this).pluginFile.DirectoryName` | locate the node bridge; config name; assembly‑resolve fallback | plugin's own load location; ALC removes the resolve hack |
| `AppDataFolder.FullName` | diagnostics‑log dir, config dir, bridge fallback dir | `IPluginHost.Storage` (or a resolved AppData path) |
| `WriteExceptionLog(ex,msg)` | error sink | `IPluginHost.Logger` (MEL `ILogger`) |
| `PlayTtsMethod` / `PlaySoundMethod` (get‑to‑save, set‑to‑hijack) | route audio to Discord, suppress ACT's built‑in speakers | `IAudioOutput.RegisterSink(sink, terminal:true)` + `Dispose()` |
| `FormActMain.PlayTtsDelegate` / `PlaySoundDelegate` | field types for the saved delegates | **deleted** (no save/restore, see Audio below) |
| `Handle` (HWND) | parent for the folder‑picker dialogs | Avalonia `IStorageProvider.OpenFolderPickerAsync` |
| WPF view in the `TabPage` via `ElementHost` | the config UI | `IUiContributor` → Avalonia `UiSurface` |

## What is deleted outright

The single‑mutable‑slot workaround and the net48 packaging scaffolding all disappear:

- The delegate **save/swap/restore** (`oldTTS`/`oldSound`, `OnOutputActivated`/`OnOutputDeactivated`
  bodies). Suppression is now "register a terminal sink"; un‑suppression is "dispose it".
- **Costura.Fody + Fody**, `CosturaUtility.Initialize()`, the `GetTypes()` load‑invariant gymnastics,
  the `[MethodImpl(NoInlining)]` markers, and the `AppDomain.CurrentDomain.AssemblyResolve` fallback —
  the host's ALC + `.deps.json` handle assembly isolation.
- `ElementHost` / `WindowsFormsIntegration`, `VistaFolderPicker`, `FolderBrowserDialog`, and the
  `ActGlobals.oFormActMain.Handle` plumbing.

## Audio — the whole legacy dance collapses to one sink

Legacy (single mutable slot, so it must save/overwrite/restore):

```csharp
oldTTS = ActGlobals.oFormActMain.PlayTtsMethod;          // save
ActGlobals.oFormActMain.PlayTtsMethod = vm.SpeakText;    // hijack (suppress ACT speakers)
...
ActGlobals.oFormActMain.PlayTtsMethod = oldTTS;          // restore
```

Native (additive multi‑sink; a **terminal** sink stops the chain so the host's built‑in speaker sink
never fires — that *is* the suppression):

```csharp
// on vm.OutputActivated:
_sinkHandle = host.Audio.RegisterSink(new DiscordAudioSink(vm), priority: 10, terminal: true);
// on vm.OutputDeactivated:
_sinkHandle?.Dispose();   // chain falls back through to the built-in sink
```

```csharp
sealed class DiscordAudioSink(DiscordTriggersViewModel vm) : IAudioSink
{
    // options (Voice/Rate/Volume) intentionally ignored — Discord-Triggers routes through
    // its OWN configured engine/voice, not per-request host options. See note below.
    public ValueTask SpeakAsync(string text, AudioOptions o, CancellationToken ct)
        => new(vm.SpeakTextCoreAsync(text));           // Task -> ValueTask wrap
    public ValueTask PlayAsync(string file, int volume, CancellationToken ct)
        => new(vm.SpeakFileCoreAsync(file));           // volume dropped (see note)
}
```

Small `Core` change — smaller than it first looks:
- `SpeakTextCoreAsync` **already exists** (returns `Task`; the Test command already awaits it). It is
  `private` today; promote it to `internal`.
- `SpeakSoundFile` is a one‑liner wrapping `discord.SpeakFileAsync(path)`. Add an `internal
  SpeakFileCoreAsync(string path)` that returns that `Task` directly — there is little to "factor out".
- Grant the new plugin assembly access via `[InternalsVisibleTo]` (keep the two cores `internal`, not
  `public`, so the exposed surface stays minimal).
- The `async void SpeakText`/`SpeakSoundFile` ACT‑delegate targets are no longer needed (nothing calls
  a `void` delegate now) and can be deleted.

Two behaviours to preserve deliberately (state them so a reviewer doesn't "fix" them):
- **`AudioOptions.Voice`/`Rate` on `SpeakAsync` are ignored by design** — the plugin uses its own
  configured engine/voice, not the host's per‑request options.
- **`PlayAsync`'s `volume` is dropped**, matching today's behaviour (`SpeakSoundFile`'s `volume` param
  is already ignored). Honouring it would need a per‑play gain across the wire protocol; leave it as a
  logged TODO rather than a protocol bump in this port.

## Migration steps (ordered, checkable)

### Phase 0 — branch & doc
- [x] `git checkout -b feature/net10-native-migration`
- [x] This migration document.

### Phase 1a — de‑risk `System.Speech` on net10 first (spike)
The in‑process SAPI synth path is the single highest‑risk unknown, so validate it before any real
retarget work.
- [x] Throwaway `net10.0-windows` console referencing the **`System.Speech` NuGet package**;
      synthesize one clip to WAV and confirm it plays. If it regresses, the ONNX (bridge‑side) path is
      unaffected — but you want to know now, not in Phase 3.
      **Result: passes.** `System.Speech` 10.0.0 restores/builds clean on `net10.0-windows`, enumerates
      the installed SAPI voices, synthesizes, and plays back. The WAV header decodes to exactly the
      pinned format (PCM, 48000 Hz, stereo, 16‑bit), so the in‑process synth path needs no conversion.
      One incidental find: `System.Media.SoundPlayer` moved to `System.Windows.Extensions` on net10 —
      not used anywhere in this codebase, so no impact.

### Phase 1b — carry `Core` to net10 (clean cut)
- [x] Retarget `ACT_DiscordTriggers.Core` to **`net10.0-windows`** — a clean cut, **not** a
      `net48;net10.0-windows` multi‑target. `master` retains the net48 ACT build for servicing; this
      branch avoids the ongoing `#if`/dual‑TFM tax for the duration of the port.
- [x] Replace the GAC `System.Speech` `<Reference>` with the **`System.Speech` NuGet package**
      (`10.0.0`, the version validated in Phase 1a); drop the `System.Net.Http` `<Reference>` (in‑box on
      net10). Also dropped the now‑obsolete net48 `ServicePointManager.SecurityProtocol` TLS‑1.2 nudge in
      `OnnxDownloader.cs` (net10 defaults to TLS 1.2+ and it no longer affects `HttpClient`).
- [x] Keep `CommunityToolkit.Mvvm`, `System.Text.Json`, the embedded `onnx-voices.json`. (`System.Text.Json`
      draws a benign `NU1510` — it's in‑box on net10 — but is kept per plan; the pinned `10.0.9` matches.)
- [x] Confirm the VM's `SynchronizationContext`‑based `OnUi` marshaling is intact — Avalonia installs
      a sync context on the UI thread, so `sync = SynchronizationContext.Current` in the ctor works the
      same way it did under the WPF dispatcher. Verified **no** WPF/`Dispatcher` types leak into `Core`.
- [x] Promote `SpeakTextCoreAsync` to `internal`; add `internal SpeakFileCoreAsync(string path)`
      returning `discord.SpeakFileAsync(path)`; add `[InternalsVisibleTo]` for the new plugin assembly
      (`Fct.Plugins.DiscordTriggers`).
- [x] Build `Core` standalone on net10; run its unit tests (protocol/settings/tts parser). **139 passed,
      5 skipped** (network‑gated + unmeasured‑voice); bridge‑spawning suites (`BridgeIntegrationTests`,
      `OnnxBridgeE2ETests`) still need a prior `build.ps1` and were filtered for this phase.
- [x] Side effects of the clean cut: the net48 entry assembly `ACT_DiscordTriggers` (which can't reference
      a net10 `Core`) is **unhooked from `ACT_DiscordTriggers.sln`** on this branch — its source stays on
      disk for the Phase 3 Avalonia port; `master` keeps building it. `ACT_DiscordTriggers.Tests` retargeted
      to `net10.0-windows` alongside `Core` (keeps `UseWPF` + the linked `ViewBehaviors.cs`). `build.ps1`/
      `check.ps1` still assume the net48 entry assembly + Costura packaging and are deferred to Phase 4.

### Phase 2 — native plugin project (headless first)
- [x] New `Fct.Plugins.DiscordTriggers` (`net10.0-windows`, `EnableDynamicLoading=true`) in **this**
      repo, `ProjectReference`ing `ACT_DiscordTriggers.Core` and `PackageReference`ing the host contract
      packages (`Fct.Abstractions`, `Fct.Abstractions.UI`) from the `fct-local` feed with
      `ExcludeAssets=runtime`. Avalonia 12.0.5 + `Microsoft.Extensions.Logging.Abstractions` 10.0.9
      (pulled transitively by those packages) are declared explicitly, also `ExcludeAssets=runtime`, so
      the direct reference overrides the transitive runtime flow (host‑provided, shared‑to‑default‑ALC).
      Added to `ACT_DiscordTriggers.sln`.
- [x] `plugin.json`: `id` `com.discordtriggers`, `version`, `contract` `1.0`, `assembly`,
      `entry` = the `IPlugin` type, `capabilities: ["audio","ui"]`.
- [x] `DiscordTriggersPlugin : IPlugin, IUiContributor` —
  - `InitializeAsync`: `FindBridgeDir` from the plugin's own location (`Assembly.Location`, fallback
    `host.Storage.DataDirectory`) → `DiscordClient.SetBridgePath` → `DiagnosticsLog.Init(host.Storage.
    DataDirectory, …)` → `DiscordClientService` + `SettingsStore(host.Storage.DataDirectory,
    "DiscordTriggers.config.xml", …)` + the VM; subscribe `vm.OutputActivated`/`OutputDeactivated` to
    register/dispose the terminal `IAudioSink`; `vm.Initialize()`.
  - `DisposeAsync`: dispose the sink, dispose the discord service, unsubscribe, `vm.Save()`,
    `await vm.ShutdownAsync()`, flush/shutdown `DiagnosticsLog` — each step guarded.
  - `RegisterUi`: `AddSettingsPage` with a **placeholder** Avalonia view (real UI is Phase 3).
  - Logging forwarded to `host.Logger` (plus the user‑emailable unified diagnostics file via the VM).
- [x] `DiscordAudioSink : IAudioSink` — `SpeakAsync`→`SpeakTextCoreAsync`, `PlayAsync`→`SpeakFileCoreAsync`
      (Voice/Rate/volume dropped by design); registered `priority: 10, terminal: true`.
- [x] Stage the node bridge (`node.exe` + `bundle.js` + `node_modules/`) next to the plugin output via
      a post‑build MSBuild target (gated on the bridge `dist/` existing); `FindBridgeDir` keeps the
      "next to the DLL, else fallback dir" logic. Full release packaging into the host `plugins/` dir
      is Phase 4.
- [ ] Load end‑to‑end in the host with the placeholder settings page; verify: plugin discovered, init
      runs, bridge spawns, a Test speak routes to the sink, deinit tears the bridge down. *(Pending —
      manual step: build the bridge, stage the plugin output into the host's `plugins/` dir, run the
      host. Plugin + solution build clean and the output dep layout is verified.)*

### Phase 3 — port the UI to Avalonia
- [x] `DiscordTriggersView` → Avalonia `UserControl` (`Ui/DiscordTriggersView.axaml` + code‑behind);
      `DataContext` = the same `DiscordTriggersViewModel`, `x:DataType` compiled bindings. The four tabs
      (Output · Text‑to‑Speech · Sound · Information) carry over, with the bot/local and SAPI/ONNX folds
      bound to the VM's `IsBotMode`/`IsLocalMode`/`IsSapi`/`IsOnnx` flags.
- [x] `IUiContributor.RegisterUi` → `ui.AddSettingsPage(new UiSurface(id, title, CreateView))` with a
      lazy `Func<Control>`; `CreateView` returns the real view (falling back to an error `TextBlock` if
      the VM failed to init). `RegisterUi` also rebinds the VM's `SynchronizationContext` to Avalonia's
      UI thread before the first `CreateView`.
- [x] Ported the sub‑pieces that used WPF/Win32 specifics:
  - ONNX voice‑picker flyout: `Ui/Controls/VoicePicker` — a searchable, locale‑grouped flyout over the
    VM's `OnnxVoices` (tokenized substring filter + `GroupBy(LocaleName)` in code‑behind), replacing the
    WPF `CollectionViewSource` + `FilterEventArgs`.
  - Masked token box → `TextBox PasswordChar="•"`; Ctrl+C log copy → `KeyDown` handler + `TopLevel`
    `Clipboard` in the view code‑behind (replaces the `ViewBehaviors.cs` attached properties).
  - Info‑tab links → click handlers using `TopLevel.GetTopLevel(this).Launcher.LaunchUriAsync(...)`.
  - Folder picker → `TopLevel.GetTopLevel(this).StorageProvider.OpenFolderPickerAsync(...)`
    (`VistaFolderPicker`/`PickFolderClassic`/`Handle` deleted).
  - Logo/mascot → `avares://` asset (`AvaloniaResource` linked from `docs/logo.png`); glyphs in
    `Assets/IconGlyphs.axaml`, replacing the `pack://` URIs.
  - Theme → `Ui/Styles/PluginStyles.axaml` over the host's semantic token contract (`FctSurface`/
    `FctAccent`/… + `fct-*` style classes), scoped to the plugin's subtree.
- [x] Background‑thread view updates go through the VM's existing sync‑context `OnUi` (rebound in
      `RegisterUi` to Avalonia's UI thread); the log tail‑follow marshals via `Dispatcher.UIThread.Post`.

*(Plugin builds clean against the real host contract; a live in‑host render is folded into Phase 4's
manual e2e.)*

### Phase 4 — packaging & tests
- [x] Host staging: `build_modern.ps1` assembles `release-modern/Fct.Plugins.DiscordTriggers/` (entry
      DLL + `plugin.json` + `.deps.json` + Core/CommunityToolkit/System.Speech + `runtimes/` + the node
      bridge) — the host's install layout, pointed at via the in-app "Add plugin" catalog.
- [x] Port/retarget `ACT_DiscordTriggers.Tests` to net10 (protocol/IPC/lifecycle keep working;
      `BridgeIntegrationTests` still spawns the real bridge). Retargeted alongside `Core` in Phase 1b;
      the dead WPF `PasswordBoxBindingTests` + the `ViewBehaviors.cs` compile-link + `UseWPF` were
      dropped (the Avalonia port replaced that seam). **140 passed, 5 skipped** (non-bridge suites).
- [x] New test (`DiscordAudioSinkTests`): the terminal-sink seam — `DiscordAudioSink.SpeakAsync` routes
      through the VM's SAPI/ONNX synth branch (ignoring per-request `AudioOptions` by design) and
      `PlayAsync` routes to the file core (dropping per-file volume), via a fake `IDiscordService`. The
      *fan-out suppression* half ("a terminal sink stops the chain so the built‑in speaker never fires")
      is the host `AudioService`'s job (G3) and is covered in the host repo — not reproducible here
      without the host runtime.
- [ ] Manual e2e: run the host, connect the bot / local output, fire a Test speak, confirm audio and
      clean teardown. *(Still the one unverified path — the plugin has never run inside `Fct.App`.)*

## What does NOT change

- The **node bridge**, the **wire protocol** (`Protocol.cs`/`protocol.ts`), the audio DSP pipeline, and
  the two output modes (bot / local).
- The `Core` **VM logic, IPC, settings migrations, ONNX catalog** — only its TFM and the two exposed
  audio cores change.

## Risks / open items

- **System.Speech on net10**: the in‑process SAPI synth path (non‑ONNX voices) depends on the modern
  `System.Speech` package behaving like the net48 GAC assembly. **Phase 1a** de‑risks this with a
  one‑clip spike before any retarget work. If it regresses, the ONNX path (bridge‑side synth) is
  unaffected.
- **Host contract verified**: the `Fct.Abstractions` audio/UI members this port targets already exist
  and match the signatures used above (`IAudioSink.SpeakAsync(text, AudioOptions, ct)` /
  `PlayAsync(file, volume, ct)` → `ValueTask`; `IAudioOutput.RegisterSink(sink, priority, terminal)` →
  `IDisposable`; `AudioOptions(Volume, Voice, Rate)`; `IUiHost.AddSettingsPage`/`Dispatcher`). The
  sink shim compiles against the real contract, not an assumed one.
- **UI port scope**: the Avalonia rewrite is ~75% of total effort. The XAML is large (voice picker,
  output‑mode folding, info tab). Ship Phase 2's placeholder page first, then iterate the real UI so
  audio/lifecycle land independently of UI completeness.
- **`IUiHost` maturity**: this is the host's first real UI‑contributing plugin, so it exercises
  `Fct.Abstractions.UI` for real — confirm the shell renders an `AddSettingsPage` surface end‑to‑end.
- **Diagnostics log + settings location** (decided in Phase 2): both live under the host's per‑plugin
  storage (`IPluginHost.Storage.DataDirectory`). `Core`'s XML `SettingsStore` is kept (preserving the
  v2→v3 migration framework) but pointed at that dir, and `DiagnosticsLog.Init` writes the unified log
  there too. Switching the log back to a file next to the bridge is a one‑argument change if a more
  discoverable user‑emailable path is wanted later.

## Note for the host's compat shim (out of scope for this port)

`FormActMain.PlayTtsMethod`/`PlaySoundMethod` in the host's `Fct.Compat.Shim.ActFacade` are **set‑only**.
A literal recompile‑shim port of *any* audio‑hijack plugin (Discord‑Triggers, TTSYukkuri) would fail to
compile on the `get`‑to‑save line, and assigning the saved delegate back would register a second sink
rather than unregister. This port avoids the issue by going native, but the shim gap is real and should
be tracked in the host repo if the recompile path is meant to serve those plugins.
</content>
