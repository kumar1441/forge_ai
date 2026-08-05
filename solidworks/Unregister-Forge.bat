@echo off
REM Uninstall the Forge SolidWorks add-in. RIGHT-CLICK -> "Run as administrator".
setlocal
set REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
set DLL=%~dp0bin\x64\Release\Forge.SolidWorks.dll
echo Unregistering Forge...
"%REGASM%" /unregister "%DLL%"
echo.
pause
