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
if (-not $integration) {
    Write-Host '[ring2] no integration test projects yet — skipping'
} else {
    Write-Host '[ring2] integration tests (Testcontainers), affected-scoped'
    $config = if ($env:ALVO_CONFIGURATION) { $env:ALVO_CONFIGURATION } else { 'Debug' }
    $base = if ($env:ALVO_AFFECTED_BASE) { $env:ALVO_AFFECTED_BASE } else { 'origin/main' }
    $affected = $null
    git -C $root rev-parse --verify --quiet "$base^{commit}" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $raw = dotnet tool run dotnet-affected -- --repository-path $root --filter-file-path "$root/MMLib.Alvo.slnx" --from $base --to HEAD --dry-run -f text 2>$null
        if ($LASTEXITCODE -eq 0) {
            $affected = @($raw | Where-Object { $_ -match '\.csproj$' })
        }
    }
    foreach ($proj in $integration) {
        if ($null -ne $affected -and $affected -notcontains $proj.FullName) {
            Write-Host "[ring2] unaffected, skipping $($proj.Name)"
            continue
        }
        dotnet test --project $proj.FullName -c $config
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

Write-Host '[ring2] placeholder: API invariant + Vacuum — land in a later F1 PR'
Write-Host '[ring2] OK'
