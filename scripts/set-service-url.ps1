param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceUrl
)

$ErrorActionPreference = "Stop"

$uri = $null
if (-not [Uri]::TryCreate($ServiceUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -notin @("https", "http")) {
    throw "ServiceUrl must be a valid HTTP or HTTPS URL."
}

$normalized = $ServiceUrl.TrimEnd("/")
$root = Split-Path -Parent $PSScriptRoot
$repositoryConfig = Join-Path $root "service-endpoint.json"
$localConfigDir = Join-Path $env:LOCALAPPDATA "SeaPowerFourPlayer"
$localConfig = Join-Path $localConfigDir "service-endpoint.json"
$payload = @{ serviceUrl = $normalized } | ConvertTo-Json

New-Item -ItemType Directory -Path $localConfigDir -Force | Out-Null
$payload | Set-Content -LiteralPath $repositoryConfig -Encoding UTF8
$payload | Set-Content -LiteralPath $localConfig -Encoding UTF8

Write-Host "Sea Power service URL configured:"
Write-Host $normalized
Write-Host ""
Write-Host "Local launcher override: $localConfig"
Write-Host "Repository package config: $repositoryConfig"
