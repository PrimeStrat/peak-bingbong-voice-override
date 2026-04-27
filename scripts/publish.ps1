<#
.SYNOPSIS
    Build, package, and publish the mod to Thunderstore.

.DESCRIPTION
    Runs `dotnet build` to compile and stage the plugin, then invokes the Thunderstore
    CLI (`tcli publish`) which reads thunderstore.toml to build the zip and upload it
    with the correct community, categories, and dependencies.
    The Thunderstore API token is read from the THUNDERSTORE_TOKEN environment variable
    (or pass -Token). Skips publishing when -PackOnly is supplied (uses PackThunderstore
    to produce a local zip for inspection).

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release.

.PARAMETER Token
    Thunderstore API token. Falls back to $env:THUNDERSTORE_TOKEN.

.PARAMETER PackOnly
    Build and zip the package locally without uploading.

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
    Write-Host "==> Building & packaging ($Configuration)" -ForegroundColor Cyan
    dotnet build "src/MyMod.csproj" -c $Configuration -t:PackThunderstore
    $zip = Get-ChildItem "dist/*.zip" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $zip) { throw "No package zip produced in dist/." }
    Write-Host "==> Package: $($zip.FullName)" -ForegroundColor Green

    if ($PackOnly) { return }

    if (-not (Get-Command tcli -ErrorAction SilentlyContinue)) {
        Write-Host "==> Installing Thunderstore CLI (tcli)" -ForegroundColor Cyan
        dotnet tool install --global tcli
    }

    if (-not $Token) {
        Write-Host "==> Skipping Thunderstore upload (no token set)" -ForegroundColor Yellow
        return
    }

    Write-Host "==> Publishing to Thunderstore" -ForegroundColor Cyan
    tcli publish --token $Token
}
finally {
    Pop-Location
}
