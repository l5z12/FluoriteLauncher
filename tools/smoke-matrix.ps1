<#
.SYNOPSIS
  Smoke-tests launching a matrix of Minecraft eras through the Fluorite CLI.
.EXAMPLE
  pwsh tools/smoke-matrix.ps1 -Root .fluorite-dev -Source Official
#>
param(
    [string]$Root = ".fluorite-dev",
    [ValidateSet("Official", "Bmclapi", "Auto")][string]$Source = "Official",
    [string[]]$Versions = @("26.3", "1.21.4", "1.20.1", "1.16.5", "1.12.2", "1.7.10", "1.5.2", "b1.7.3", "a1.0.4", "rd-132211"),
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Continue"
# `pwsh -File` passes "a","b" as a single string; accept comma-separated lists too.
$Versions = @($Versions | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo "tools/Fluorite.Cli/bin/$Configuration/net10.0-windows10.0.26100.0/fluorite.exe"
if (-not (Test-Path $cli)) {
    dotnet build (Join-Path $repo "tools/Fluorite.Cli/Fluorite.Cli.csproj") -c $Configuration -v q | Out-Null
}

$results = @()
foreach ($v in $Versions) {
    Write-Host "=== $v ===" -ForegroundColor Cyan
    $out = & $cli smoke $v --root $Root --source $Source 2>&1
    $code = $LASTEXITCODE
    $out | Select-Object -Last 6 | ForEach-Object { Write-Host "  $_" }
    $summary = ($out | Where-Object { $_ -match "^SMOKE " } | Select-Object -Last 1)
    if (-not $summary) { $summary = ($out | Select-Object -Last 1) }
    $results += [pscustomobject]@{ Version = $v; Ok = ($code -eq 0); Result = "$summary" }
}

Write-Host ""
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if ($results.Where({ -not $_.Ok }).Count -gt 0) { exit 1 }
