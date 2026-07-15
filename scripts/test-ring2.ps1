#!/usr/bin/env pwsh
# ring2 — run before opening a PR (ring1 + integration (affected) + API
# invariant + Vacuum). Full run (+ mutation + e2e) stays in CI on the PR.
# Mirrors scripts/test-ring2 for PowerShell.
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $dir
& "$dir/test-ring1.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$integration = Get-ChildItem -Path $root -Recurse -Filter '*.Tests.Integration.csproj' -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
if ($integration) {
    Write-Host '[ring2] integration tests (Testcontainers)'
    # TODO(#9): scope via dotnet-affected once integration projects exist.
    $config = if ($env:ALVO_CONFIGURATION) { $env:ALVO_CONFIGURATION } else { 'Debug' }
    foreach ($proj in $integration) {
        dotnet test --project $proj.FullName -c $config
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
} else {
    Write-Host '[ring2] no integration test projects yet — skipping'
}

Write-Host '[ring2] placeholder: API invariant + Vacuum — land in a later F1 PR'
Write-Host '[ring2] OK'
