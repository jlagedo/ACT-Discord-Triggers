# Refresh the vendored FCT contract packages under libs/fct/packages.
#
# Fct.Plugins.DiscordTriggers compiles against the host's plugin contract (Fct.Abstractions +
# Fct.Abstractions.UI), delivered as NuGet packages from the sibling ffxiv-combat-tracker repo's
# dist/debug/packages feed. CI has no sibling checkout, so we commit a copy of those nupkgs here
# (the nuget.config `fct-vendored` source). Run this after the host bumps the contract version,
# then commit the refreshed nupkgs. Bump the version in Fct.Plugins.DiscordTriggers.csproj too if
# the package version changed.
#
# Usage:
#   pwsh ./tools/vendor-fct-packages.ps1                 # copy from ..\ffxiv-combat-tracker
#   pwsh ./tools/vendor-fct-packages.ps1 -HostRepo <dir> # copy from a custom host repo path

param([string]$HostRepo = '..\ffxiv-combat-tracker')

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot\..

$src = Join-Path $HostRepo 'dist\debug\packages'
$dst = 'libs\fct\packages'

if (-not (Test-Path $src)) {
    Write-Error @"
Host package feed not found at:
    $src
Build the host packages first, then re-run:
    dotnet run --project build     # in $HostRepo
"@
    exit 1
}

# The two contract packages the plugin references. Fct.Abstractions.UI carries Fct.Abstractions
# as a project reference, but the host packs both as separate nupkgs.
$patterns = @('Fct.Abstractions.*.nupkg', 'Fct.Abstractions.UI.*.nupkg')
$found = Get-ChildItem -Path $src -Include $patterns -File -Recurse -ErrorAction SilentlyContinue
if (-not $found) {
    Write-Error "No Fct.Abstractions[.UI] nupkgs found in $src"
    exit 1
}

New-Item -ItemType Directory -Force $dst | Out-Null
# Clear stale versions so a version bump doesn't leave the old nupkg behind.
Remove-Item "$dst\Fct.Abstractions*.nupkg" -Force -ErrorAction SilentlyContinue
foreach ($f in $found) {
    Copy-Item $f.FullName -Destination $dst -Force
    Write-Host "    vendored $($f.Name)"
}

Write-Host ""
Write-Host "==> Done. Commit the refreshed nupkgs under $dst\ (and bump Fct.Plugins.DiscordTriggers.csproj if the version changed)."
