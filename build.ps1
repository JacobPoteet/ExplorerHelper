<#
.SYNOPSIS
    Builds Explorer Helper. Same script used locally and by GitHub Actions.
.EXAMPLE
    ./build.ps1                          # publish self-contained exe + zip
    ./build.ps1 -Version 1.2.3 -Installer  # also compile the Inno Setup installer
#>
param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.4.0',
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publishDir = Join-Path $root 'artifacts\publish'

dotnet publish (Join-Path $root 'src\ExplorerHelper\ExplorerHelper.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$zipPath = Join-Path $root "artifacts\ExplorerHelper-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force
Write-Host "Portable zip: $zipPath"

if ($Installer) {
    $isccCandidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Error 'Inno Setup 6 not found. Install it from https://jrsoftware.org/isinfo.php (or: winget install JRSoftware.InnoSetup)'
    }
    & $iscc "/DAppVersion=$Version" (Join-Path $root 'installer\ExplorerHelper.iss')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Installer written to artifacts\"
}
