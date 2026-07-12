<#
.SYNOPSIS
    Builds (and optionally installs) the sparse MSIX package that adds Explorer Helper's
    Windows 11 top-level context-menu entry (issue #11).

.DESCRIPTION
    Produces a signed .msix that carries only the manifest + logos, plus an "external location"
    folder holding the real app payload (ExplorerHelper.exe and the shell-extension COM host).
    See packaging\README.md for the full walkthrough and prerequisites.

.PARAMETER Install
    After building, trust the signing certificate (LocalMachine) and register the package with
    Add-AppxPackage. Requires an elevated (Administrator) PowerShell.

.PARAMETER Uninstall
    Remove a previously installed package and exit.

.EXAMPLE
    ./Build-SparsePackage.ps1                 # build + sign only
    ./Build-SparsePackage.ps1 -Install        # build, sign, trust cert, install (run as admin)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.1.0',
    [string]$PublisherSubject = 'CN=Explorer Helper Dev',
    [string]$PackageName = 'JacobPoteet.ExplorerHelper',
    [switch]$Install,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$root       = Split-Path $PSScriptRoot -Parent          # repo root
$artifacts  = Join-Path $root 'artifacts'
$external   = Join-Path $artifacts 'sparse\ExternalLocation'  # real payload lives here
$pkgSource  = Join-Path $artifacts 'sparse\PackageSource'     # manifest + logos -> into the .msix
$msixVer    = "$Version.0"                                # MSIX needs a 4-part version
$msixPath   = Join-Path $artifacts "ExplorerHelper-Sparse-$Version.msix"
$pfxPath    = Join-Path $artifacts 'ExplorerHelperDev.pfx'
$cerPath    = Join-Path $artifacts 'ExplorerHelperDev.cer'

if ($Uninstall) {
    Get-AppxPackage -Name $PackageName | ForEach-Object {
        Write-Host "Removing $($_.PackageFullName)"
        Remove-AppxPackage $_.PackageFullName
    }
    return
}

# --- Locate the Windows SDK tools (makeappx + signtool) ------------------------------------
function Find-SdkTool([string]$name) {
    $bases = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }
    $hit = $bases |
        ForEach-Object { Get-ChildItem $_ -Recurse -Filter $name -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $hit) {
        throw "$name not found. Install the Windows 10/11 SDK (winget install Microsoft.WindowsSDK) and retry."
    }
    return $hit.FullName
}
$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'
Write-Host "makeappx: $makeappx"
Write-Host "signtool: $signtool"

# --- 1. Publish the app + shell extension into the external location ------------------------
Remove-Item $external -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $external | Out-Null

# Both are framework-dependent: COM hosting (the shell extension) does not support
# self-contained deployments, so this package requires the .NET 8 Desktop Runtime installed.
Write-Host "`nPublishing ExplorerHelper (app)..."
dotnet publish (Join-Path $root 'src\ExplorerHelper\ExplorerHelper.csproj') `
    -c $Configuration -r win-x64 --self-contained false `
    -p:Version=$Version -p:PublishSingleFile=false -o $external
if ($LASTEXITCODE) { throw "app publish failed" }

Write-Host "`nPublishing ExplorerHelper.ShellExtension (COM host)..."
dotnet publish (Join-Path $root 'src\ExplorerHelper.ShellExtension\ExplorerHelper.ShellExtension.csproj') `
    -c $Configuration -r win-x64 --self-contained false `
    -p:Version=$Version -o $external
if ($LASTEXITCODE) { throw "shell extension publish failed" }

if (-not (Test-Path (Join-Path $external 'ExplorerHelper.ShellExtension.comhost.dll'))) {
    throw "comhost DLL missing from payload - check EnableComHosting."
}

# --- 2. Assemble the package source (manifest + logos), stamping the version ----------------
Remove-Item $pkgSource -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $pkgSource 'Images') | Out-Null

$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest = $manifest -replace 'Version="[0-9.]+"', "Version=`"$msixVer`""
$manifest = $manifest -replace 'Publisher="CN=[^"]+"', "Publisher=`"$PublisherSubject`""
Set-Content -Path (Join-Path $pkgSource 'AppxManifest.xml') -Value $manifest -Encoding UTF8
Copy-Item (Join-Path $PSScriptRoot 'Images\*') (Join-Path $pkgSource 'Images') -Force

# --- 3. Pack the sparse .msix (/nv skips validation of the external payload) ----------------
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Remove-Item $msixPath -Force -ErrorAction SilentlyContinue
& $makeappx pack /o /nv /d $pkgSource /p $msixPath
if ($LASTEXITCODE) { throw "makeappx failed" }
Write-Host "Packed: $msixPath"

# --- 4. Ensure a signing certificate whose subject matches the manifest Publisher -----------
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $PublisherSubject } | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed signing certificate ($PublisherSubject)..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $PublisherSubject `
        -KeyUsage DigitalSignature -FriendlyName 'Explorer Helper Dev Signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
$pwd = ConvertTo-SecureString -String 'ExplorerHelperDev' -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
Export-Certificate  -Cert $cert -FilePath $cerPath | Out-Null

# --- 5. Sign the package -------------------------------------------------------------------
& $signtool sign /fd SHA256 /a /f $pfxPath /p 'ExplorerHelperDev' $msixPath
if ($LASTEXITCODE) { throw "signtool failed" }
Write-Host "`nSigned. Public cert exported to: $cerPath"

if (-not $Install) {
    Write-Host @"

Build complete. To install (in an elevated PowerShell):

  Import-Certificate -FilePath "$cerPath" -CertStoreLocation Cert:\LocalMachine\TrustedPeople
  Add-AppxPackage -Path "$msixPath" -ExternalLocation "$external"

Right-click any folder afterwards -> "Clean this folder" appears at the top level of the Win11 menu.
Remove with:  ./Build-SparsePackage.ps1 -Uninstall
"@
    return
}

# --- 6. Optional install (needs admin for the machine trust store) -------------------------
Write-Host "`nTrusting certificate and installing package..."
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Add-AppxPackage -Path $msixPath -ExternalLocation $external
Write-Host "Installed. Right-click a folder to see 'Clean this folder' at the top level."
