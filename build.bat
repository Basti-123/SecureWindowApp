@echo off
setlocal

echo ============================================
echo   SecureWindowApp - Self-Contained Build
echo ============================================
echo.

REM In das Verzeichnis dieses Skripts wechseln
cd /d "%~dp0"

echo Baue und publiziere die Anwendung (win-x64, self-contained, single-file)...
echo.

dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o publish

if %ERRORLEVEL% neq 0 (
    echo.
    echo [FEHLER] Der Build ist fehlgeschlagen. Bitte Meldungen oben pruefen.
    echo Ist das .NET 8 SDK installiert? ^(https://dotnet.microsoft.com/download^)
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ============================================
echo   Erfolgreich! Fertige EXE liegt in:
echo   %cd%\publish\SecureWindowApp.exe
echo ============================================
pause
