# CLAUDE.md — bootstrap assembly

The net48 `ACT_DiscordTriggers.dll` that ACT actually loads (`Assembly.LoadFrom` + `GetTypes()`).
Deliberately tiny (~10 KB): WinForms/ACT/BCL only — **no WPF, no third-party deps, no Costura**.
The real plugin lives in `ACT_DiscordTriggers.Main` under `libs/`, byte-loaded from here.

Files:
- `DiscordTriggersPlugin.cs` — `IActPluginV1`. `InitPlugin` installs the `AssemblyResolver`,
  byte-loads `ACT_DiscordTriggers.Main`, reflectively constructs `PluginImpl`, and forwards
  `InitPlugin`/`DeInitPluginAsync` (passing the resolved plugin dir + config name).
- `AssemblyResolver.cs` — hooks `AppDomain.AssemblyResolve` and byte-loads any `libs/<name>.dll`
  via `Assembly.Load(byte[])` (no file lock → the updater can overwrite `libs/` in place).
  Matches by **simple name** (version-agnostic), so one shipped copy satisfies any requested
  version and the closure stays isolated from other ACT plugins.

## Why this assembly stays thin (do not break)

ACT calls `GetTypes()` on this DLL before any of our code runs, resolving every defined type's
base/interfaces. So the only type here with a non-GAC base is `DiscordTriggersPlugin : IActPluginV1`
(ACT type) — nothing derives from a third-party dep, so the scan can't throw "Invalid Plugin".
**Do not** add WPF types, MVVM-derived types, or anything needing `libs/` deps at the type level
here; that all belongs in Main. Keep the bootstrap's only job: resolve + delegate.
