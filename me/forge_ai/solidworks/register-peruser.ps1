# Forge — PER-USER registration (NO ADMIN). Validation for the one-line installer.
# Proves SolidWorks can load Forge without elevation: COM class registered under
# HKCU\Software\Classes, add-in discovery keys under HKCU\Software\SolidWorks.
# If SolidWorks shows Forge in Tools > Add-Ins after this, the "no admin, 5 sec" story holds.

$ErrorActionPreference = 'Stop'
$guid   = '{8F3C9E21-4B6A-4C2D-9E1F-2A7B5C8D3E40}'   # SwAddin CLSID
$here   = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll    = Join-Path $here 'bin\x64\Release\Forge.SolidWorks.dll'
$regasm = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$regfile= Join-Path $env:TEMP 'forge-com.reg'

if (-not (Test-Path $dll)) { throw "DLL not found: $dll (build Release first)" }

# A leftover machine-wide (admin) registration would let SolidWorks load Forge from HKLM and
# give a FALSE pass on this per-user test. Detect it so we know the load is genuinely per-user.
if (Test-Path "HKLM:\SOFTWARE\SolidWorks\Addins\$guid") {
    Write-Host "WARNING: an HKLM (admin) Forge add-in key exists. SolidWorks may load from THAT," -ForegroundColor Yellow
    Write-Host "         masking the per-user test. Remove it (needs admin) for a clean result:"      -ForegroundColor Yellow
    Write-Host "         reg delete `"HKLM\SOFTWARE\SolidWorks\Addins\$guid`" /f   (run as admin)"     -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "1/4  Generating COM registration (.reg) — no registry write, no admin..."
& $regasm $dll /codebase /regfile:$regfile | Out-Null
if (-not (Test-Path $regfile)) { throw "RegAsm did not produce $regfile" }

Write-Host "2/4  Rewriting machine-wide -> per-user (HKCR -> HKCU\Software\Classes)..."
$txt = Get-Content $regfile -Raw
$txt = $txt -replace 'HKEY_CLASSES_ROOT\\', 'HKEY_CURRENT_USER\Software\Classes\'
Set-Content $regfile $txt -Encoding Unicode

Write-Host "3/4  Importing COM registration into HKCU (no admin)..."
reg import $regfile 2>&1 | Out-Null

Write-Host "4/4  Adding SolidWorks add-in discovery keys under HKCU..."
$addins  = "HKCU:\Software\SolidWorks\Addins\$guid"
$startup = "HKCU:\Software\SolidWorks\AddInsStartup\$guid"
New-Item -Path $addins  -Force | Out-Null
New-ItemProperty -Path $addins -Name '(default)'   -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $addins -Name 'Title'       -Value 'Forge' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $addins -Name 'Description' -Value 'Forge - AI for SolidWorks' -PropertyType String -Force | Out-Null
New-Item -Path $startup -Force | Out-Null
New-ItemProperty -Path $startup -Name '(default)'  -Value 1 -PropertyType DWord -Force | Out-Null

Write-Host ""
Write-Host "DONE (no admin used). Now open SolidWorks and check Tools > Add-Ins for 'Forge'." -ForegroundColor Green
Write-Host "If Forge loads and the panel appears -> per-user install works, Option A is validated." -ForegroundColor Green
