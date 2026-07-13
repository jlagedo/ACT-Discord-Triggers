# Build both distributable packages from one checkout:
#   1. build.ps1 -Zip           -> release/ACT_DiscordTriggers/          + ACT_DiscordTriggers.zip
#                                   (net48 WPF drop-in for Advanced Combat Tracker)
#   2. build_modern.ps1 ... -Zip -> release-modern/Fct.Plugins.DiscordTriggers/ + .zip
#                                   (net10 Avalonia plugin for FFXIV Combat Tracker)
#
# build.ps1 builds the Node bridge into DiscordBridge-node/dist/; the modern build reuses it
# via -SkipBridge, so the bridge is bundled + self-tested exactly once.
#
# The modern build needs the FCT contract packages (fct-local live feed OR the committed
# fct-vendored copy under libs/fct/packages). When neither resolves, the modern build is skipped
# with a warning so a bare checkout still produces the legacy package.
#
# Flags:
#   -SkipModern   build only the legacy ACT package.
#   -SkipLegacy   build only the modern FCT package (still needs the bridge; builds it via build_modern).

param([switch]$SkipModern, [switch]$SkipLegacy)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-Location $PSScriptRoot

$bridgeBuilt = $false

if (-not $SkipLegacy) {
    Write-Host "==> [1/2] Legacy net48 package (build.ps1)" -ForegroundColor Cyan
    ./build.ps1 -Zip
    if ($LASTEXITCODE -ne 0) { Write-Error "Legacy build failed"; exit 1 }
    $bridgeBuilt = $true
}

if (-not $SkipModern) {
    # Resolve the FCT contract feed the same way build_modern.ps1's preflight does.
    $feeds = @(
        (Join-Path $PSScriptRoot '..\ffxiv-combat-tracker\dist\debug\packages'),
        (Join-Path $PSScriptRoot 'libs\fct\packages')
    )
    $haveFeed = $false
    foreach ($feed in $feeds) {
        if ((Test-Path $feed) -and (Get-ChildItem -Path $feed -Filter 'Fct.Abstractions.UI.*.nupkg' -ErrorAction SilentlyContinue)) { $haveFeed = $true; break }
    }
    if (-not $haveFeed) {
        Write-Warning "Skipping modern build: no FCT contract feed found (fct-local or libs\fct\packages). Run tools/vendor-fct-packages.ps1 or build the host packages."
    } else {
        Write-Host "==> [2/2] Modern net10 package (build_modern.ps1)" -ForegroundColor Cyan
        # Reuse the bridge dist/ that build.ps1 already produced + self-tested (-SkipBridge).
        if ($bridgeBuilt) {
            ./build_modern.ps1 -Configuration Release -Zip -SkipBridge
        } else {
            ./build_modern.ps1 -Configuration Release -Zip
        }
        if ($LASTEXITCODE -ne 0) { Write-Error "Modern build failed"; exit 1 }
    }
}

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
if (Test-Path ACT_DiscordTriggers.zip)        { Write-Host "    Legacy: $(Resolve-Path ACT_DiscordTriggers.zip)" }
if (Test-Path release-modern\Fct.Plugins.DiscordTriggers.zip) { Write-Host "    Modern: $(Resolve-Path release-modern\Fct.Plugins.DiscordTriggers.zip)" }
