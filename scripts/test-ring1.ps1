#!/usr/bin/env pwsh
# ring1 — run after finishing a slice (ring0 + arch + public-API approval).
# Arch (os A + os B) already run inside dotnet test; public-API approval lands
# in PR2 (#12). Mirrors scripts/test-ring1 for PowerShell.
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$dir/test-ring0.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host '[ring1] placeholder: public-API approval tests — land in PR2 (#12)'
Write-Host '[ring1] OK'
