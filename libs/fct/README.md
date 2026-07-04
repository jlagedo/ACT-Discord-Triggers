# FFXIV Combat Tracker contract packages

`Fct.Plugins.DiscordTriggers` compiles against the host's plugin contract, delivered as two NuGet
packages: `Fct.Abstractions` (core contract) and `Fct.Abstractions.UI` (Avalonia UI surfaces, which
depends on `Fct.Abstractions` + `Avalonia`).

The host publishes no public NuGet feed. It emits the `.nupkg`s into `dist\debug\packages`, which the
repo-root `nuget.config` adds as the `fct-local` package source (relative path — both repos are cloned
side-by-side under `E:\dev`). The plugin `PackageReference`s them with `ExcludeAssets="runtime"`: the
host loader shares the contract (and its `Avalonia`/`Microsoft.Extensions.Logging.Abstractions` deps)
into the default `AssemblyLoadContext` for single type identity across shell + plugin, so the plugin
must **not** ship its own private copy. `Avalonia` and `Microsoft.Extensions.Logging.Abstractions` are
declared explicitly (also `ExcludeAssets="runtime"`) so the direct reference overrides the packages'
transitive runtime flow.

## Refresh (after a host contract change)

Rebuild the host's packages, then re-restore here. If the package **version** changed, bump it in
`Fct.Plugins.DiscordTriggers.csproj` too. Run from this repo root (both repos cloned under `E:\dev`):

```
pwsh -c "dotnet pack ../ffxiv-combat-tracker/src/Fct.Abstractions.UI/Fct.Abstractions.UI.csproj -o ../ffxiv-combat-tracker/dist/debug/packages"
dotnet restore --force
```

(Packing `Fct.Abstractions.UI` also packs its `Fct.Abstractions` project reference. Use `--force`
so NuGet picks up a repacked package at the same version instead of a cached copy.)
