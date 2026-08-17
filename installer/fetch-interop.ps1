# Forge — SolidWorks interop provisioning for clean-runner CI builds.
# Copies the 3 SolidWorks interop assemblies into lib/sw-interop/, which the add-in csproj resolves
# via -p:SwInteropRoot (CI does this). Run ONCE on a machine with SolidWorks installed, then commit
# the lib/sw-interop/ folder so GitHub Actions can build the add-in without SolidWorks.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File installer\fetch-interop.ps1             # auto-detect SW install
#   powershell -ExecutionPolicy Bypass -File installer\fetch-interop.ps1 -From "C:\...\api\redist"
#
# Licensing: the SolidWorks interop assemblies are redistributable with applications built on the
# SolidWorks API; they ship in <SW install>\api\redist\ and are committed here for build purposes.

param(
    [string]$OutDir = 'lib\sw-interop',
    [string]$From
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = [System.IO.Path]::GetFullPath((Join-Path (Split-Path $here -Parent) $OutDir))

$names = @(
    'SolidWorks.Interop.sldworks.dll',
    'SolidWorks.Interop.swconst.dll',
    'SolidWorks.Interop.swpublished.dll'
)

if (-not $From) {
    $candidates = @(
        'C:\Program Files\Dassault Systemes\SOLIDWORKS 3DEXPERIENCE R2026x\SOLIDWORKS\api\redist',
        'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist'
    )
    $From = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if (-not $From -or -not (Test-Path $From)) {
    throw "No SolidWorks redist folder found. Pass -From <api\redist path> (needs a SolidWorks install, or point it at a folder holding the 3 SolidWorks.Interop.*.dll files)."
}

New-Item -ItemType Directory -Path $out -Force | Out-Null
foreach ($n in $names) {
    $src = Join-Path $From $n
    if (-not (Test-Path $src)) { throw "Missing interop assembly: $src" }
    Copy-Item $src $out -Force
    Write-Host "copied: $n"
}
Write-Host "Interop ready in $out - commit this folder so CI builds without SolidWorks."
