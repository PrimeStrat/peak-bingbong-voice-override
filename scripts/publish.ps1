<#
.SYNOPSIS
    Build, package, and publish the mod to Thunderstore.

.DESCRIPTION
    Runs `dotnet build -t:Pack` to produce dist/<author>-<mod>-<version>.zip, then
    invokes the Thunderstore CLI (`tcli publish`) to upload it. The Thunderstore API
    token is read from the THUNDERSTORE_TOKEN environment variable (or pass -Token).
    Skips publishing when -PackOnly is supplied.

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release.

.PARAMETER Token
    Thunderstore API token. Falls back to $env:THUNDERSTORE_TOKEN.

.PARAMETER PackOnly
    Build and zip the package without uploading.

.EXAMPLE
    pwsh ./scripts/publish.ps1 -PackOnly
    pwsh ./scripts/publish.ps1 -Token "tss_xxx"
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Token = $env:THUNDERSTORE_TOKEN,
    [switch]$PackOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    Write-Host "==> Restoring & building ($Configuration)" -ForegroundColor Cyan
    dotnet build "src/MyMod.csproj" -c $Configuration -t:Pack

    $zip = Get-ChildItem "dist/*.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $zip) { throw "No package zip produced in dist/." }
    Write-Host "==> Package: $($zip.FullName)" -ForegroundColor Green

    if ($PackOnly) { return }

    if (-not (Get-Command tcli -ErrorAction SilentlyContinue)) {
        Write-Host "==> Installing Thunderstore CLI (tcli)" -ForegroundColor Cyan
        dotnet tool install --global tcli
    }

    if (-not $Token) { throw "THUNDERSTORE_TOKEN not set. Pass -Token or set the env var." }

    Write-Host "==> Publishing to Thunderstore" -ForegroundColor Cyan
    tcli publish --file $zip.FullName --token $Token
}
finally {
    Pop-Location
}
