#!/usr/bin/env pwsh
# check.ps1 — run every quality gate locally in one shot, fail-fast.
#
# No args: the full gate set CI runs (ESLint -> XAML style -> build.ps1 -> C# tests -> Node tests).
#   -Fast     Skip build.ps1 and the bridge-spawning C# integration tests; run a standalone
#             tsc typecheck in place of build.ps1's. The quick inner-loop subset.
#   -Fix      Auto-fix instead of check (eslint --fix, xstyler write mode), then run the rest.
#   -Install  Run `npm ci` in DiscordBridge-node first (otherwise assumes deps are present).
[CmdletBinding()]
param(
  [switch]$Fast,
  [switch]$Fix,
  [switch]$Install
)

Set-Location $PSScriptRoot
$bridge = Join-Path $PSScriptRoot 'DiscordBridge-node'
$tests = 'ACT_DiscordTriggers.Tests/ACT_DiscordTriggers.Tests.csproj'

# Ensure the local XAML Styler tool is available before the style gate uses it.
dotnet tool restore | Out-Null

# Build the ordered gate list for this mode. Each gate is a name + a scriptblock that must
# leave $LASTEXITCODE 0 (native tools) or throw on failure (build.ps1 runs in a child pwsh).
$gates = [System.Collections.Generic.List[object]]::new()
function Add-Gate([string]$Name, [scriptblock]$Action) {
  $gates.Add([pscustomobject]@{ Name = $Name; Action = $Action })
}

if ($Install) {
  Add-Gate 'npm ci (bridge)' { npm --prefix $bridge ci }
}

if ($Fix) {
  Add-Gate 'ESLint --fix (bridge)' { npm --prefix $bridge run lint -- --fix }
  Add-Gate 'XAML Styler (write)'   { dotnet xstyler -r -d . -c Settings.XamlStyler }
} else {
  Add-Gate 'ESLint (bridge)'       { npm --prefix $bridge run lint }
  Add-Gate 'XAML style check'      { dotnet xstyler -p -r -d . -c Settings.XamlStyler }
}

if ($Fast) {
  # build.ps1 (and its tsc) is skipped, so typecheck standalone and skip integration tests.
  Add-Gate 'TS typecheck (bridge)' { npm --prefix $bridge run typecheck }
  Add-Gate 'C# unit tests (net48)' {
    dotnet test $tests -c Release --nologo --filter 'FullyQualifiedName!~BridgeIntegration'
  }
} else {
  # build.ps1 covers the tsc typecheck + bundles the bridge into dist/ that the C# integration
  # tests spawn, so it must precede them. This branch mirrors CI's gate set exactly.
  Add-Gate 'Build + bundle + self-test' { pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') }
  Add-Gate 'C# tests (net48)' { dotnet test $tests -c Release --nologo }
}

Add-Gate 'Node tests (bridge)' { npm --prefix $bridge test }

# Run them in order, stopping at the first failure.
$n = 0
foreach ($gate in $gates) {
  $n++
  Write-Host ""
  Write-Host "==> [$n/$($gates.Count)] $($gate.Name)" -ForegroundColor Cyan
  $sw = [System.Diagnostics.Stopwatch]::StartNew()
  $global:LASTEXITCODE = 0
  $failed = $false
  try {
    & $gate.Action
    if ($LASTEXITCODE -ne 0) { $failed = $true }
  } catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $failed = $true
  }
  $sw.Stop()
  $secs = [int]$sw.Elapsed.TotalSeconds
  if ($failed) {
    Write-Host ""
    Write-Host "FAILED: $($gate.Name) (${secs}s) — stopping (fail-fast)." -ForegroundColor Red
    exit 1
  }
  Write-Host "    ok (${secs}s)" -ForegroundColor Green
}

Write-Host ""
Write-Host "All $($gates.Count) gates passed." -ForegroundColor Green
