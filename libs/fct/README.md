# Vendored FFXIV Combat Tracker contract assemblies

`Fct.Plugins.DiscordTriggers` compiles against the host's plugin contract. The host publishes
no NuGet feed, so the two contract assemblies are vendored here and referenced by `<HintPath>`
with `<Private>false</Private>` (the host loader shares them into the default `AssemblyLoadContext`,
so the plugin must **not** ship its own private copy — see the migration doc's Phase 2 notes).

These are pure contract assemblies (interfaces + records, no logic), so a committed build is a
stable compile-time reference.

| File | Source (host repo `E:\dev\ffxiv-combat-tracker`) |
|---|---|
| `Fct.Abstractions.dll` | `src\Fct.Abstractions\bin\Debug\net10.0\Fct.Abstractions.dll` |
| `Fct.Abstractions.UI.dll` | `src\Fct.Abstractions.UI\bin\Debug\net10.0\Fct.Abstractions.UI.dll` |

## Refresh (after a host contract change)

Rebuild the host abstractions, then re-copy (run from this repo root, both repos cloned side-by-side under `E:\dev`):

```
pwsh -c "dotnet build ../ffxiv-combat-tracker/src/Fct.Abstractions.UI/Fct.Abstractions.UI.csproj; copy ../ffxiv-combat-tracker/src/Fct.Abstractions/bin/Debug/net10.0/Fct.Abstractions.dll libs/fct/; copy ../ffxiv-combat-tracker/src/Fct.Abstractions.UI/bin/Debug/net10.0/Fct.Abstractions.UI.dll libs/fct/"
```

(Building `Fct.Abstractions.UI` also builds its `Fct.Abstractions` project reference.)
