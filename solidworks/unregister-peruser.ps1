# Forge — PER-USER unregister (NO ADMIN). Clean removal: drops the COM class and the
# SolidWorks add-in keys from HKCU. Leaves no trace in the machine hive.

$ErrorActionPreference = 'SilentlyContinue'
$guid = '{8F3C9E21-4B6A-4C2D-9E1F-2A7B5C8D3E40}'

Remove-Item -Path "HKCU:\Software\Classes\CLSID\$guid" -Recurse -Force
Remove-Item -Path "HKCU:\Software\SolidWorks\Addins\$guid" -Recurse -Force
Remove-Item -Path "HKCU:\Software\SolidWorks\AddInsStartup\$guid" -Recurse -Force
# ProgID (panel control) — remove by name if present
Remove-Item -Path "HKCU:\Software\Classes\Forge.ForgePanel" -Recurse -Force

Write-Host "Forge unregistered from HKCU (no admin used). Restart SolidWorks to confirm it's gone." -ForegroundColor Green
