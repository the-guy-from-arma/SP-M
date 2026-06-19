param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Sea Power",
    [string]$ServiceUrl = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$serviceConfigPath = Join-Path $root "service-endpoint.json"
if ([string]::IsNullOrWhiteSpace($ServiceUrl) -and
    (Test-Path -LiteralPath $serviceConfigPath)) {
    $ServiceUrl = (Get-Content -Raw -LiteralPath $serviceConfigPath |
        ConvertFrom-Json).serviceUrl
}
if ([string]::IsNullOrWhiteSpace($ServiceUrl)) {
    $ServiceUrl = "https://spdash-production.up.railway.app"
}
$pluginProject = Join-Path $root "src\SeaPowerFourPlayer.csproj"
$testProject = Join-Path $root "tests\ProtocolSmoke\ProtocolSmoke.csproj"
$launcherTestProject = Join-Path $root "tests\LauncherSmoke\LauncherSmoke.csproj"
$launcherProject = Join-Path $root "launcher\SeaPowerFourPlayer.Launcher.csproj"
$backendProject = Join-Path $root "backend\SeaPowerLobbyService.csproj"
$publishDir = Join-Path $root "launcher\bin\$Configuration\net8.0-windows\win-x64\publish"
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $root "dist"))
$packageName = "SeaPowerFourPlayer-Launcher-v0.6.3"
$packageDir = [System.IO.Path]::GetFullPath((Join-Path $distRoot $packageName))
$zipPath = [System.IO.Path]::GetFullPath((Join-Path $distRoot "$packageName.zip"))

if (-not $packageDir.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe package directory: $packageDir"
}
if (-not $zipPath.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe package zip path: $zipPath"
}

dotnet build $pluginProject -c Release "/p:GameDir=$GameDir" "/p:InstallAfterBuild=false"
dotnet build $backendProject -c Release
dotnet run --project $testProject
dotnet run --project $launcherTestProject -c Release
dotnet publish $launcherProject -c $Configuration -r win-x64 --self-contained true `
    "/p:PublishSingleFile=true" `
    "/p:IncludeNativeLibrariesForSelfExtract=true" `
    "/p:EnableCompressionInSingleFile=true"

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

$launcherExe = Join-Path $publishDir "SeaPowerFourPlayerLauncher.exe"
if (-not (Test-Path -LiteralPath $launcherExe)) {
    throw "Published launcher was not found: $launcherExe"
}

$packagedExe = Join-Path $packageDir "SeaPowerFourPlayerLauncher.exe"
Copy-Item -LiteralPath $launcherExe -Destination $packagedExe
Copy-Item -LiteralPath (Join-Path $root "FRIENDS_README.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $root "CHANGELOG.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $packageDir
@{
    serviceUrl = $ServiceUrl.TrimEnd("/")
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDir "service-endpoint.json") -Encoding UTF8

$hash = (Get-FileHash -LiteralPath $packagedExe -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = "0.6.3"
    downloadUrl = "SeaPowerFourPlayerLauncher.exe"
    sha256 = $hash
    releaseNotes = "Shows public lobbies reliably, detects and repairs the conflicting old multiplayer plugin, and ships as a working one-click website package."
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDir "latest.json") -Encoding UTF8

Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$webDownloadsDir = Join-Path $root "backend\wwwroot\downloads"
New-Item -ItemType Directory -Path $webDownloadsDir -Force | Out-Null
Copy-Item -LiteralPath $packagedExe `
    -Destination (Join-Path $webDownloadsDir "SeaPowerFourPlayerLauncher.exe") `
    -Force
Copy-Item -LiteralPath $zipPath `
    -Destination (Join-Path $webDownloadsDir "SeaPowerFourPlayer-Launcher.zip") `
    -Force

Write-Host ""
Write-Host "Launcher EXE: $packagedExe"
Write-Host "Friend package: $zipPath"
Write-Host "SHA-256: $hash"
Write-Host ""
Write-Host "Website package copied to:"
Write-Host (Join-Path $webDownloadsDir "SeaPowerFourPlayerLauncher.exe")
Write-Host (Join-Path $webDownloadsDir "SeaPowerFourPlayer-Launcher.zip")
