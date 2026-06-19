param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Sea Power",
    [string]$SourceDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$running = Get-Process -Name "Sea Power" -ErrorAction SilentlyContinue
if ($running) {
    throw "Sea Power is running. Close the game before installing the plugin."
}

$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
$configDir = Join-Path $GameDir "BepInEx\config"
$bepInExSource = Join-Path $SourceDir "BepInEx"
$pluginSource = Join-Path $SourceDir "SeaPowerFourPlayer.dll"
$liteNetSource = Join-Path $SourceDir "LiteNetLib.dll"
$doorstopSource = Join-Path $SourceDir "doorstop_config.ini"
$winHttpSource = Join-Path $SourceDir "winhttp.dll"

if (-not (Test-Path -LiteralPath (Join-Path $GameDir "Sea Power.exe"))) {
    throw "Sea Power.exe was not found: $GameDir"
}
if (-not (Test-Path -LiteralPath $pluginSource)) {
    throw "SeaPowerFourPlayer.dll was not found beside this installer: $pluginSource"
}
if (-not (Test-Path -LiteralPath $liteNetSource)) {
    throw "LiteNetLib.dll was not found beside this installer: $liteNetSource"
}
if (-not (Test-Path -LiteralPath (Join-Path $bepInExSource "core\BepInEx.dll"))) {
    throw "The bundled BepInEx core is missing: $bepInExSource"
}
if (-not (Test-Path -LiteralPath $doorstopSource) -or -not (Test-Path -LiteralPath $winHttpSource)) {
    throw "The bundled BepInEx Doorstop bootstrap files are missing."
}

New-Item -ItemType Directory -Path (Join-Path $GameDir "BepInEx") -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $bepInExSource "core") -Destination (Join-Path $GameDir "BepInEx") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $bepInExSource "proxy") -Destination (Join-Path $GameDir "BepInEx") -Recurse -Force
Copy-Item -LiteralPath $doorstopSource -Destination (Join-Path $GameDir "doorstop_config.ini") -Force
Copy-Item -LiteralPath $winHttpSource -Destination (Join-Path $GameDir "winhttp.dll") -Force

$doorstopVersionSource = Join-Path $SourceDir ".doorstop_version"
if (Test-Path -LiteralPath $doorstopVersionSource) {
    Copy-Item -LiteralPath $doorstopVersionSource -Destination (Join-Path $GameDir ".doorstop_version") -Force
}

New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null
New-Item -ItemType Directory -Path $configDir -Force | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupDir = Join-Path $pluginsDir "SeaPowerFourPlayer-backup"
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null

foreach ($name in @("SeapowerMultiplayer.dll", "SeaPowerFourPlayer.dll")) {
    $installed = Join-Path $pluginsDir $name
    if (Test-Path -LiteralPath $installed) {
        $disabledName = "$name.$stamp.disabled"
        Move-Item -LiteralPath $installed -Destination (Join-Path $backupDir $disabledName)
        Write-Host "Disabled existing plugin: $name"
    }
}

Copy-Item -LiteralPath $pluginSource -Destination (Join-Path $pluginsDir "SeaPowerFourPlayer.dll") -Force
Copy-Item -LiteralPath $liteNetSource -Destination (Join-Path $pluginsDir "LiteNetLib.dll") -Force

# BepInEx's assembly metadata cache can retain "0 plugins" when a plugin is
# installed between launches. Remove only the generated chainloader cache so the
# new DLL is scanned on the next startup.
$chainloaderCache = Join-Path $GameDir "BepInEx\cache\chainloader_typeloader.dat"
if (Test-Path -LiteralPath $chainloaderCache) {
    Remove-Item -LiteralPath $chainloaderCache -Force
    Write-Host "Cleared the generated BepInEx plugin discovery cache."
}

$oldConfig = Join-Path $configDir "com.seapowermultiplayer.plugin.cfg"
$newConfig = Join-Path $configDir "com.seapower.fourplayer.cfg"
if ((Test-Path -LiteralPath $oldConfig) -and -not (Test-Path -LiteralPath $newConfig)) {
    Copy-Item -LiteralPath $oldConfig -Destination $newConfig
    $configText = Get-Content -LiteralPath $newConfig -Raw
    $configText = $configText.Replace(
        "## Settings file was created by plugin SeapowerMultiplayer v0.3.0",
        "## Settings migrated for plugin Sea Power Four Player")
    $configText = $configText.Replace(
        "## Plugin GUID: com.seapowermultiplayer.plugin",
        "## Plugin GUID: com.seapower.fourplayer")
    $configText = [regex]::Replace(
        $configText, "(?m)^MissileStateHz\s*=\s*\d+\s*$", "MissileStateHz = 20")
    $configText = [regex]::Replace(
        $configText, "(?m)^UnitStateHz\s*=\s*\d+\s*$", "UnitStateHz = 10")

    if ($configText -notmatch "(?m)^\[Lobby\]\s*$") {
        $configText += @"

[Lobby]

PlayerName = $env:USERNAME
PreferredSlot = 0
PreferredTeam = 255
PreferredRole = 0
"@
    }

    Set-Content -LiteralPath $newConfig -Value $configText -Encoding UTF8
    Write-Host "Migrated the existing multiplayer network settings to the four-player config."
    Write-Host "Normalized legacy 60/40 Hz sync rates to four-player-safe 20/10 Hz defaults."
}

Write-Host ""
Write-Host "Sea Power Four Player installed successfully."
Write-Host "BepInEx bootstrap: $GameDir\winhttp.dll"
Write-Host "BepInEx core: $GameDir\BepInEx\core"
Write-Host "Plugin: $pluginsDir\SeaPowerFourPlayer.dll"
Write-Host "Old plugin backups: $backupDir"
Write-Host "Launch the game and press Ctrl+F9 to open the multiplayer panel."
