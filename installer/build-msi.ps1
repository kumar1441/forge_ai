# Forge — MSI build script (WiX v4)
# Builds the single-file Forge-Setup.msi from the add-in Release output.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1
#   powershell -ExecutionPolicy Bypass -File installer\build-msi.ps1 -Sign -CertThumbprint <sha1>
#
# Requires (on this machine): .NET SDK (to build the add-in if the DLL is missing) and the
# SolidWorks interop assemblies the add-in's csproj references (HintPath -> local SW install).
# WiX v4 is installed on demand as a global dotnet tool.
#
# Produces: dist\Forge-Setup-<version>.msi  (version read from installer\Product.wxs).

param(
    [string]$AddinOutput = '..\solidworks\bin\x64\Release',
    [switch]$Sign,
    [string]$CertThumbprint,
    [string]$OutDir = '..\dist'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Exec([string]$exe, [string[]]$args) {
    Write-Host ">> $exe $($args -join ' ')" -ForegroundColor Cyan
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $exe $($args -join ' ')" }
}

# ---- 0. Resolve paths ----
$AddinOutput = [System.IO.Path]::GetFullPath((Join-Path $here $AddinOutput))
$OutDir      = [System.IO.Path]::GetFullPath((Join-Path $here $OutDir))

# ---- 1. Build the add-in if the DLL is missing ----
$dll = Join-Path $AddinOutput 'Forge.SolidWorks.dll'
if (-not (Test-Path $dll)) {
    Write-Host "Forge.SolidWorks.dll not found at $AddinOutput - building the add-in (close SolidWorks first: DLL lock = MSB3027)..." -ForegroundColor Yellow
    Exec 'dotnet' @('build', (Join-Path $here '..\solidworks\Forge.SolidWorks.csproj'), '-c', 'Release', '-p:Platform=x64')
}

# ---- 2. Ensure WiX v4 CLI (harvest needs it) ----
if (-not (Get-Command wix -EA SilentlyContinue)) {
    Write-Host 'WiX v4 CLI not found - installing global tool...' -ForegroundColor Yellow
    Exec 'dotnet' @('tool', 'install', '--global', 'wix')
}

# ---- 3. Harvest the add-in output into FileComponents.wxs (regenerated each build; gitignored) ----
$frag = Join-Path $here 'FileComponents.wxs'
Exec 'wix' @('harvest', 'dir', $AddinOutput, '-directoryref', 'INSTALLFOLDER', '-o', $frag, '-g', 'ForgeAddinFiles')

# ---- 4. Read the version from Product.wxs (single source of truth) ----
$m = Select-String -Path (Join-Path $here 'Product.wxs') -Pattern 'Version="([^"]+)"' | Select-Object -First 1
$version = $m.Matches[0].Groups[1].Value
Write-Host "Installer version: $version"

# ---- 5. Build the MSI ----
Exec 'dotnet' @('build', (Join-Path $here 'Forge.Setup.wixproj'), '-c', 'Release')

# ---- 6. Locate the produced MSI ----
$msi = Get-ChildItem (Join-Path $here 'bin') -Recurse -Filter '*.msi' -EA Stop |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw 'No .msi produced - inspect the WiX build output above.' }

# ---- 7. Copy to dist with versioned name ----
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$dest = Join-Path $OutDir "Forge-Setup-$version.msi"
Copy-Item $msi.FullName $dest -Force
Write-Host "MSI ready: $dest" -ForegroundColor Green

# ---- 8. Optional Authenticode signing (self/corporate cert; SignPath OSS signs in CI instead) ----
if ($Sign) {
    if (-not $CertThumbprint) { throw '-Sign requires -CertThumbprint' }
    $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter 'signtool.exe' -EA SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found - install Windows SDK' }
    Exec $signtool.FullName @('sign', '/sha1', $CertThumbprint, '/tr', 'http://timestamp.digicert.com', '/td', 'SHA256', '/fd', 'SHA256', $dest)
    Write-Host "Signed: $dest" -ForegroundColor Green
}
