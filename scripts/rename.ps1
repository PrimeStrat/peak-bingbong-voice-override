<#
.SYNOPSIS
    Rename the template (MyMod / YourName) to your mod's identity in one shot.

.EXAMPLE
    pwsh ./scripts/rename.ps1 -ModName CoolMod -Author Alice
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ModName,
    [Parameter(Mandatory)] [string]$Author
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$replacements = @{
    "MyMod"    = $ModName
    "YourName" = $Author
}

$files = Get-ChildItem -Path $root -Recurse -File `
    -Include *.cs,*.csproj,*.sln,*.props,*.json,*.toml,*.md,*.ps1,*.sh `
    | Where-Object { $_.FullName -notmatch "\\(bin|obj|build|dist|\.git)\\" }

foreach ($f in $files) {
    $content = Get-Content $f.FullName -Raw
    $orig = $content
    foreach ($k in $replacements.Keys) {
        $content = $content.Replace($k, $replacements[$k])
    }
    if ($content -ne $orig) {
        Set-Content -Path $f.FullName -Value $content -NoNewline
        Write-Host "updated $($f.FullName)"
    }
}

# Rename files/dirs whose names contain MyMod
Get-ChildItem -Path $root -Recurse -Force `
    | Where-Object { $_.Name -like "*MyMod*" -and $_.FullName -notmatch "\\\.git\\" } `
    | Sort-Object FullName -Descending `
    | ForEach-Object {
        $new = $_.Name.Replace("MyMod", $ModName)
        Rename-Item -Path $_.FullName -NewName $new
        Write-Host "renamed $($_.FullName) -> $new"
    }

Write-Host "Done. Review changes with 'git diff'." -ForegroundColor Green
