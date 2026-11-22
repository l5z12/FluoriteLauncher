<#
.SYNOPSIS
  Installs each mod loader (recommended build) for representative Minecraft versions and smoke-launches it.
.EXAMPLE
  pwsh tools/loader-matrix.ps1 -Root .fluorite-dev
  pwsh tools/loader-matrix.ps1 -Root .fluorite-dev -Cases "forge:1.20.1,fabric:26.3"
#>
param(
    [string]$Root = ".fluorite-dev",
    [ValidateSet("Official", "Bmclapi", "Auto")][string]$Source = "Official",
    [string[]]$Cases = @(
        "fabric:26.3", "fabric:1.21.4", "quilt:1.20.1",
        "forge:26.3", "forge:1.20.1", "forge:1.12.2", "forge:1.7.10",
        "neoforge:26.3", "neoforge:1.21.1",
        "legacyfabric:1.8.9", "liteloader:1.12.2",
        "optifine:1.20.1", "optifine:1.12.2"),
    [string]$Configuration = "Debug",
    [int]$AliveSeconds = 200,
    [int]$Timeout = 360
)

$ErrorActionPreference = "Continue"
$Cases = @($Cases | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo "tools/Fluorite.Cli/bin/$Configuration/net10.0-windows10.0.26100.0/fluorite.exe"

$results = @()
foreach ($case in $Cases) {
    $loader, $mc = $case -split ':', 2
    Write-Host "=== $loader $mc ===" -ForegroundColor Cyan
    $install = & $cli loader install $loader $mc recommended --root $Root --source $Source 2>&1
    $install | Select-Object -Last 4 | ForEach-Object { Write-Host "  $_" }
    $profileLine = $install | Where-Object { "$_" -match '^PROFILE=' } | Select-Object -Last 1
    if (-not $profileLine) {
        $results += [pscustomobject]@{ Case = $case; Ok = $false; Result = "INSTALL FAIL: " + (($install | Select-Object -Last 1) -replace '\s+', ' ') }
        continue
    }
    $profile = "$profileLine".Substring(8)
    $smoke = & $cli smoke $profile --root $Root --source $Source --alive-seconds $AliveSeconds --timeout $Timeout --game-dir (Join-Path $Root "dev-instances/$loader-$mc") 2>&1
    $code = $LASTEXITCODE
    $summary = ($smoke | Where-Object { "$_" -match '^SMOKE ' } | Select-Object -Last 1)
    if (-not $summary) { $summary = ($smoke | Select-Object -Last 1) }
    Write-Host "  $summary"
    $results += [pscustomobject]@{ Case = $case; Ok = ($code -eq 0); Result = "$summary" }
}

Write-Host ""
$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if ($results.Where({ -not $_.Ok }).Count -gt 0) { exit 1 }
