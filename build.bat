@echo off
setlocal EnableExtensions
cd /d "%~dp0"
echo Baue das portable Veyra-Paket fuer Windows x64...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-portable.ps1"
if errorlevel 1 (
    echo [FEHLER] Das portable Paket konnte nicht erstellt werden.
    pause
    exit /b 1
)
echo ZIP entpacken und Veyra.exe direkt oeffnen. Kein Installer erforderlich.
pause
exit /b 0
