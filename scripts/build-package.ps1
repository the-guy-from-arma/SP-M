param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Sea Power",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\SeaPowerFourPlayer.csproj"
$output = Join-Path $root "src\bin\$Configuration\net472"
$packageName = "SeaPowerFourPlayer-v0.1.2-alpha"
$package = Join-Path $root "dist\$packageName"
$zipPath = Join-Path $root "dist\$packageName.zip"

dotnet build $project -c $Configuration "/p:GameDir=$GameDir" "/p:InstallAfterBuild=false"
dotnet run --project (Join-Path $root "tests\ProtocolSmoke\ProtocolSmoke.csproj")

if (Test-Path -LiteralPath $package) {
    Remove-Item -LiteralPath $package -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
New-Item -ItemType Directory -Path $package | Out-Null

Copy-Item -LiteralPath (Join-Path $output "SeaPowerFourPlayer.dll") -Destination $package
Copy-Item -LiteralPath (Join-Path $output "LiteNetLib.dll") -Destination $package
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $package
Copy-Item -LiteralPath (Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $package
Copy-Item -LiteralPath (Join-Path $root "CHANGELOG.md") -Destination $package
Copy-Item -LiteralPath (Join-Path $root "scripts\install-plugin.ps1") -Destination (Join-Path $package "Install.ps1")

$bepInExSource = Join-Path $GameDir "BepInEx"
$bepInExBundle = Join-Path $package "BepInEx"
if (-not (Test-Path -LiteralPath (Join-Path $bepInExSource "core\BepInEx.dll"))) {
    throw "BepInEx core was not found in the game directory: $bepInExSource"
}
if (-not (Test-Path -LiteralPath (Join-Path $bepInExSource "proxy\winhttp.dll"))) {
    throw "BepInEx Doorstop proxy was not found: $bepInExSource\proxy\winhttp.dll"
}

New-Item -ItemType Directory -Path (Join-Path $bepInExBundle "core") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $bepInExBundle "proxy") -Force | Out-Null
Copy-Item -Path (Join-Path $bepInExSource "core\*") -Destination (Join-Path $bepInExBundle "core") -Recurse -Force
Copy-Item -Path (Join-Path $bepInExSource "proxy\*") -Destination (Join-Path $bepInExBundle "proxy") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $bepInExSource "proxy\winhttp.dll") -Destination $package -Force
Copy-Item -LiteralPath (Join-Path $bepInExSource "proxy\doorstop_config.ini") -Destination $package -Force

$doorstopVersion = Join-Path $GameDir ".doorstop_version"
if (Test-Path -LiteralPath $doorstopVersion) {
    Copy-Item -LiteralPath $doorstopVersion -Destination $package -Force
}

Compress-Archive -Path (Join-Path $package "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Complete package created at $package"
Write-Host "Zip created at $zipPath"
