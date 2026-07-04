# Modern release build: produces ./release-modern/Fct.Plugins.DiscordTriggers/, the
# native net10 plugin folder installed into the FFXIV Combat Tracker host via its
# in-app "Add plugin" catalog.
#
# Steps:
#   0. preflight the host's fct-local contract feed (nupkgs must already exist)
#   1. type-check + bundle the bridge TS (esbuild), copy node.exe, stage native/WASM
#      externals, self-test the bridge spawn (asserts BRIDGE_READY on stdout)
#   2. dotnet build the plugin (net10.0-windows, Debug) — its StageDiscordBridge
#      target folds the freshly-built bridge into bin/
#   3. assemble ./release-modern/ from the plugin bin output (already the install layout:
#      plugin.json + entry DLL + .deps.json + private deps + runtimes/ + node bridge;
#      Avalonia + Fct.Abstractions* are correctly absent — host-provided, shared ALC)
#
# This is the modern counterpart to build.ps1 (the net48 ACT drop-in). They coexist:
# build.ps1 serves master's net48 servicing, this serves the net10 native migration.
#
# Pass -Zip to also produce Fct.Plugins.DiscordTriggers.zip at the repo root.
# Pass -SkipBridge to reuse an already-built DiscordBridge-node/dist/ (fast iteration).
# Pass -Configuration Release for a release build (default Debug).

param([switch]$Zip, [switch]$SkipBridge, [string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-Location $PSScriptRoot

$PluginProj = 'Fct.Plugins.DiscordTriggers\Fct.Plugins.DiscordTriggers.csproj'
$PluginName = 'Fct.Plugins.DiscordTriggers'
$PluginId   = 'com.discordtriggers'

# --- 0. Preflight: the host's local contract-package feed ---
# The plugin PackageReferences Fct.Abstractions[.UI] from the fct-local feed
# (nuget.config -> ..\ffxiv-combat-tracker\dist\debug\packages). The host emits those
# nupkgs only after `dotnet run --project build`, and wipes/recreates dist\ each time.
# Fail early with the fix rather than letting `dotnet build` emit an opaque NU1101.
Write-Host "==> Checking host contract feed"
$feed = Join-Path $PSScriptRoot '..\ffxiv-combat-tracker\dist\debug\packages'
$haveCore = (Test-Path $feed) -and (Get-ChildItem -Path $feed -Filter 'Fct.Abstractions.*.nupkg' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike 'Fct.Abstractions.UI.*' })
$haveUi   = (Test-Path $feed) -and (Get-ChildItem -Path $feed -Filter 'Fct.Abstractions.UI.*.nupkg' -ErrorAction SilentlyContinue)
if (-not $haveCore -or -not $haveUi) {
    Write-Error @"
Host contract packages not found in:
    $feed
Run the host's package build first, then re-run this script:
    dotnet run --project build     # in E:\dev\ffxiv-combat-tracker
"@
    exit 1
}
Write-Host "    OK"

# --- 1. Bridge: type-check, bundle, stage externals, self-test ---
$bridgeBundle = 'DiscordBridge-node\dist\bundle.js'
if ($SkipBridge -and (Test-Path $bridgeBundle)) {
    Write-Host "==> Skipping bridge build (-SkipBridge; reusing DiscordBridge-node\dist\)"
} else {
    Push-Location DiscordBridge-node
    try {
        Write-Host "==> Cleaning DiscordBridge-node\dist\"
        if (Test-Path dist) { Remove-Item -Recurse -Force dist }
        New-Item -ItemType Directory dist | Out-Null

        Write-Host "==> Type-checking (tsc --noEmit)"
        # --no-install: fail loudly if `typescript` devDep is missing instead of
        # silently fetching from the registry mid-build.
        npx --no-install tsc --noEmit
        if ($LASTEXITCODE -ne 0) { Write-Error "Type check failed"; exit 1 }

        Write-Host "==> Bundling JS (esbuild)"
        node esbuild.config.mjs

        Write-Host "==> Copying node.exe"
        $nodeExe = (Get-Command node).Source
        Copy-Item $nodeExe -Destination dist\node.exe

        Write-Host "==> Staging external deps next to node.exe"
        # These packages are NOT bundled by esbuild (see esbuild.config.mjs `external`)
        # and must ship as files for Node's CJS resolver to find at runtime.
        #
        #   @snazzah/davey                — DAVE wrapper (JS, requires its native sibling below)
        #   @snazzah/davey-win32-x64-msvc — DAVE native (Rust NAPI .node, win-x64 only)
        #   opusscript                    — pure-JS Opus encoder (uses its own .wasm via __dirname)
        #   libsodium-wrappers            — pure-JS libsodium API (depends on libsodium)
        #   libsodium                     — WASM blob loaded by libsodium-wrappers
        #   sherpa-onnx-node              — ONNX neural TTS addon loader (lazy-required by tts.ts)
        #   sherpa-onnx-win-x64           — its native runtime (onnxruntime + sherpa .dll/.node, ~21 MB)
        #   audify                        — local audio output (RtAudio/WASAPI; lazy-required by local-output.ts).
        #                                   Ships its prebuilt napi .node + rtaudio.dll/opus.dll in build/Release/.
        #   bindings                      — audify's runtime addon loader (npm hoists it to the top-level
        #                                   node_modules, so it must be staged explicitly or audify's
        #                                   require('bindings') fails when local output starts).
        #   file-uri-to-path              — bindings' own top-level require; hoisted next to bindings,
        #                                   so it must be staged too or bindings.js throws Cannot find module.
        #
        # If you bump deps and a require fails at startup, audit this list — npm
        # hoists transitive packages here and our esbuild externals list silently
        # misses them. The sherpa + audify packages are best-effort: when they aren't
        # installed the staging warns and continues, and the bridge still self-tests
        # green (lazy require) — only ONNX synthesis / local output is unavailable
        # until installed.
        $externals = @(
            '@snazzah/davey',
            '@snazzah/davey-win32-x64-msvc',
            'opusscript',
            'libsodium-wrappers',
            'libsodium',
            'sherpa-onnx-node',
            'sherpa-onnx-win-x64',
            'audify',
            'bindings',
            'file-uri-to-path',
            'r8brain-wasm'
        )
        $stageDir = 'dist\node_modules'
        foreach ($pkg in $externals) {
            $src = "node_modules\$pkg"
            $dst = "$stageDir\$pkg"
            if (-not (Test-Path $src)) { Write-Warning "Missing dep: $src"; continue }
            New-Item -ItemType Directory -Path (Split-Path $dst) -Force -ErrorAction SilentlyContinue | Out-Null
            Copy-Item -Recurse -Force $src $dst
            $nestedNm = "node_modules\$pkg\node_modules"
            if (Test-Path $nestedNm) {
                Copy-Item -Recurse -Force $nestedNm "$stageDir\$pkg\node_modules"
            }
        }

        # Self-test: spawn the bridge and confirm BRIDGE_READY appears on stdout.
        # Catches packaging bugs (missing native module, broken require path) at
        # build time instead of at deploy time.
        Write-Host "==> Build self-test (spawn bridge, expect BRIDGE_READY)"
        $testPipe = "build-self-test-$(Get-Random)"
        $proc = Start-Process -FilePath dist\node.exe -ArgumentList "dist\bundle.js",$testPipe `
            -PassThru -NoNewWindow -RedirectStandardOutput dist\selftest.stdout -RedirectStandardError dist\selftest.stderr
        $ok = $false
        for ($i = 0; $i -lt 50; $i++) {
            Start-Sleep -Milliseconds 100
            if (Test-Path dist\selftest.stdout) {
                $line = Get-Content dist\selftest.stdout -ErrorAction SilentlyContinue -TotalCount 1
                if ($line -and $line.StartsWith('BRIDGE_READY')) { $ok = $true; break }
            }
            if ($proc.HasExited) { break }
        }
        if (-not $proc.HasExited) { try { Stop-Process -Id $proc.Id -Force } catch { } }
        if (-not $ok) {
            Write-Error "Build self-test FAILED. stdout / stderr:"
            Get-Content dist\selftest.stdout -ErrorAction SilentlyContinue
            Get-Content dist\selftest.stderr -ErrorAction SilentlyContinue
            exit 1
        }
        Remove-Item dist\selftest.stdout, dist\selftest.stderr, dist\DiscordBridge.log -ErrorAction SilentlyContinue
        Write-Host "    PASS"
    } finally {
        Pop-Location
    }
}

# --- 2. Plugin (net10.0-windows via dotnet build) ---
# Restore runs implicitly against the fct-local feed (checked above). The plugin's
# StageDiscordBridge target folds DiscordBridge-node\dist\ into bin\ AfterTargets=Build.
Write-Host "==> Building plugin ($Configuration, net10.0-windows)"
dotnet build $PluginProj -c $Configuration -nologo -v:quiet
if ($LASTEXITCODE -ne 0) { Write-Error "Plugin build failed"; exit 1 }

# --- 3. Assemble ./release-modern/<PluginName>/ ---
# The plugin bin output IS the host's install layout: the loader shares Fct.Abstractions[.UI]
# + Avalonia from its default ALC (PluginLoadContext.IsShared), so ExcludeAssets=runtime keeps
# them out of bin and the plugin must NOT ship private copies. Everything else (entry DLL +
# .deps.json + Core + CommunityToolkit + System.Speech + runtimes\ + the node bridge) is present.
# So a clean recursive copy of bin is the correct, complete staging.
$binDir = "Fct.Plugins.DiscordTriggers\bin\$Configuration\net10.0-windows"
if (-not (Test-Path "$binDir\$PluginName.dll")) {
    Write-Error "Plugin build output not found at $binDir\$PluginName.dll"
    exit 1
}
Write-Host "==> Assembling release-modern\$PluginName\"
if (Test-Path release-modern) { Remove-Item -Recurse -Force release-modern }
$stage = "release-modern\$PluginName"
New-Item -ItemType Directory $stage | Out-Null
# Copy the whole bin tree, minus any bridge runtime cruft that a prior local run may have left.
Copy-Item -Recurse -Force "$binDir\*" $stage
Remove-Item "$stage\DiscordBridge.log", "$stage\selftest.stdout", "$stage\selftest.stderr" -ErrorAction SilentlyContinue
# Ship docs/license alongside the plugin for users browsing the folder.
foreach ($doc in 'README.md', 'LICENSE') {
    if (Test-Path $doc) { Copy-Item $doc $stage\ }
}

Write-Host ""
Write-Host "==> Done. Modern plugin staged in release-modern\$PluginName\:"
$totalSize = (Get-ChildItem -Recurse $stage | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("    Total: {0:N1} MB" -f $totalSize)
Get-ChildItem $stage | ForEach-Object { Write-Host ("    {0,-40} {1}" -f $_.Name, $_.LastWriteTime) }
Write-Host ""
Write-Host "==> Install into the host (id: $PluginId):"
Write-Host "    Plugins -> Add plugin -> point at:"
Write-Host ("    {0}" -f (Resolve-Path $stage).Path)

# --- 4. Optional: distributable zip at repo root ---
# Compress the wrapper folder (not its contents) so the archive's top-level entry is
# the plugin folder — extractable straight into an install source.
if ($Zip) {
    $zipPath = "$PSScriptRoot\$PluginName.zip"
    Write-Host ""
    Write-Host "==> Packaging $PluginName.zip"
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $stage -DestinationPath $zipPath
    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Host ("    {0} ({1:N1} MB)" -f $zipPath, $zipSize)
}
