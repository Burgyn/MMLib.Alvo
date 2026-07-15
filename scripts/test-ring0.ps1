#!/usr/bin/env pwsh
# ring0 — run after every small step (unit + fast contract tests, seconds).
# Mirrors scripts/test-ring0 for PowerShell. Today runs the whole suite (fast
# because it is small); add a fast-only filter here when slow tests appear.
$ErrorActionPreference = 'Stop'
Write-Host '[ring0] dotnet test'
dotnet test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host '[ring0] OK'
