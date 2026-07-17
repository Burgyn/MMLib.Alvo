#!/usr/bin/env pwsh
# ring1 — run after finishing a slice (ring0 + arch + public-API approval).
# Arch and public-API approval tests run inside dotnet test (ring0).
# Mirrors scripts/test-ring1 for PowerShell.
$ErrorActionPreference = 'Stop'
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$dir/test-ring0.ps1"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host '[ring1] arch + public-API approval run inside dotnet test (ring0)'
Write-Host '[ring1] OK'
