<#
.SYNOPSIS
    Builds Explorer Helper. Same script used locally and by GitHub Actions.
.EXAMPLE
    ./build.ps1                            # version from Directory.Build.props; exe + zip
    ./build.ps1 -Version 1.2.3 -Installer  # override, and compile the Inno Setup installer
#>
param(
    [string]$Configuration = 'Release',
    [string]$Version,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Default to whatever Directory.Build.props says, so there's no second number to keep in step.
# CI passes -Version from the tag; release.yml has already checked the two agree.
if (-not $Version) {
    $props = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -notmatch '<Version>(.*?)</Version>') {
        Write-Error 'No <Version> in Directory.Build.props, and no -Version given.'
    }
    $Version = $Matches[1]
    Write-Host "Version $Version (from Directory.Build.props)"
}
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
