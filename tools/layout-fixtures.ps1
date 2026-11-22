<#
.SYNOPSIS
  Builds fixture folders of other launchers (Prism, official launcher, HMCL) from a Fluorite dev root, for testing
  in-place launching. Versions are copied; libraries/assets are junctions to the dev root's store (big, read-mostly).
.EXAMPLE
  pwsh tools/layout-fixtures.ps1 -Root .fluorite-dev -Out .fluorite-dev\fixtures
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Out,
    [string]$Minecraft = "1.21.4",
    [string]$FabricProfile = "fabric-loader-0.20.1-1.21.4",
    [string]$ModsFrom = "Fabric-1.21.4"
)
$ErrorActionPreference = "Stop"
$Root = (Resolve-Path $Root).Path
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$mods = Join-Path $Root "instances\$ModsFrom\.minecraft\mods"
$fabricVersion = ($FabricProfile -split '-')[2]

function New-Store($dotMinecraft) {
    New-Item -ItemType Directory -Force "$dotMinecraft\versions" | Out-Null
    foreach ($store in "libraries", "assets") { New-Item -ItemType Junction -Path "$dotMinecraft\$store" -Target "$Root\$store" | Out-Null }
    Copy-Item -Recurse "$Root\versions\$Minecraft" "$dotMinecraft\versions\$Minecraft"
    Get-ChildItem "$dotMinecraft\versions\$Minecraft" -Directory | Remove-Item -Recurse -Force   # natives etc.
}

# ── Prism ────────────────────────────────────────────────────────────────
$prism = "$Out\prism"
$inst = "$prism\instances\Fixture Fabric"
New-Item -ItemType Directory -Force "$inst\minecraft\mods" | Out-Null
Set-Content "$prism\prismlauncher.cfg" "[General]`nConfigVersion=1.2`nInstanceDir=instances`n"
Set-Content "$inst\instance.cfg" @"
[General]
ConfigVersion=1.2
InstanceType=OneSix
iconKey=default
name=Fixture Fabric
notes=Made by layout-fixtures.ps1
OverrideMemory=true
MaxMemAlloc=3072
MinMemAlloc=512
lastLaunchTime=1700000000000
totalTimePlayed=3600
"@
Set-Content "$inst\mmc-pack.json" @"
{
    "components": [
        { "uid": "org.lwjgl3", "version": "3.3.3", "dependencyOnly": true, "cachedVolatile": true },
        { "uid": "net.minecraft", "version": "$Minecraft", "important": true },
        { "uid": "net.fabricmc.intermediary", "version": "$Minecraft", "dependencyOnly": true },
        { "uid": "net.fabricmc.fabric-loader", "version": "$fabricVersion" }
    ],
    "formatVersion": 1
}
"@
Copy-Item "$mods\fabric-api-*.jar", "$mods\modmenu-*.jar" "$inst\minecraft\mods"
New-Item -ItemType Directory -Force "$prism\instances\Fixture Vanilla\.minecraft" | Out-Null
Set-Content "$prism\instances\Fixture Vanilla\instance.cfg" "InstanceType=OneSix`nname=Fixture Vanilla (old format)`n"
Set-Content "$prism\instances\Fixture Vanilla\mmc-pack.json" "{ `"components`": [ { `"uid`": `"net.minecraft`", `"version`": `"$Minecraft`" } ], `"formatVersion`": 1 }"
Set-Content "$prism\instances\instgroups.json" '{ "formatVersion": "1", "groups": { "Fixtures": { "hidden": false, "instances": [ "Fixture Fabric", "Fixture Vanilla" ] } } }'

# ── Official launcher ────────────────────────────────────────────────────
$official = "$Out\official\.minecraft"
New-Store $official
Copy-Item -Recurse "$Root\versions\$FabricProfile" "$official\versions\$FabricProfile"
New-Item -ItemType Directory -Force "$official\profiles\fabric\mods" | Out-Null
Copy-Item "$mods\fabric-api-*.jar" "$official\profiles\fabric\mods"
Set-Content "$official\launcher_profiles.json" @"
{
  "profiles": {
    "a1b2c3": { "name": "Fixture Vanilla", "type": "custom", "lastVersionId": "$Minecraft", "created": "2026-01-01T00:00:00.000Z", "lastUsed": "2026-02-01T00:00:00.000Z", "icon": "Grass" },
    "d4e5f6": { "name": "Fixture Fabric", "type": "custom", "lastVersionId": "$FabricProfile", "gameDir": "profiles/fabric", "javaArgs": "-Xmx3G -XX:+UseG1GC", "resolution": { "width": 1280, "height": 720 }, "icon": "Furnace" },
    "latest": { "name": "", "type": "latest-release", "lastVersionId": "latest-release", "icon": "Grass" }
  },
  "settings": { "enableSnapshots": false },
  "version": 3
}
"@

# ── HMCL (version isolation) ─────────────────────────────────────────────
$hmcl = "$Out\hmcl\.minecraft"
New-Store $hmcl
$iso = "$hmcl\versions\Fabric Isolated"
New-Item -ItemType Directory -Force "$iso\mods" | Out-Null
$json = Get-Content "$Root\versions\$FabricProfile\$FabricProfile.json" -Raw | ConvertFrom-Json
$json.id = "Fabric Isolated"
$json | ConvertTo-Json -Depth 32 | Set-Content "$iso\Fabric Isolated.json"
Set-Content "$iso\hmclversion.cfg" '{ "usesGlobal": false, "gameDirType": 1, "maxMemory": 3072, "autoMemory": false, "javaArgs": "-XX:+UseG1GC" }'
Copy-Item "$mods\fabric-api-*.jar", "$mods\modmenu-*.jar" "$iso\mods"

"Fixtures in $Out"
