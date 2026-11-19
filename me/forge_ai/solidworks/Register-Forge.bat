@echo off
REM Forge SolidWorks add-in installer. RIGHT-CLICK this file -> "Run as administrator".
setlocal
set REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
set DLL=%~dp0bin\x64\Release\Forge.SolidWorks.dll

if not exist "%DLL%" (
  echo Could not find "%DLL%".
  echo Build first:  dotnet build Forge.SolidWorks.csproj -c Release
  pause
  exit /b 1
)

echo Registering Forge...
"%REGASM%" /codebase "%DLL%"
echo.
echo If you see "Types registered successfully" above:
echo   1. Open SOLIDWORKS
echo   2. Tools ^> Add-Ins ^> check "Forge" (both boxes)
echo   3. The Forge panel appears on the right.
echo.
pause
