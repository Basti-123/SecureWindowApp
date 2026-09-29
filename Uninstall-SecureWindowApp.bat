@echo off
setlocal EnableExtensions

REM Entfernt die Benutzerinstallation, nicht jedoch Browserdaten in %LOCALAPPDATA%.
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\SecureWindowApp"
set "SHORTCUT=%APPDATA%\Microsoft\Windows\Start Menu\Programs\SecureWindowApp.lnk"

del /Q "%SHORTCUT%" 2>nul
tasklist /FI "IMAGENAME eq SecureWindowApp.exe" | find /I "SecureWindowApp.exe" >nul
if not errorlevel 1 (
    echo Bitte SecureWindowApp zuerst beenden.
    pause
    exit /b 1
)

if exist "%INSTALL_DIR%" rmdir /S /Q "%INSTALL_DIR%"
echo SecureWindowApp wurde fuer den aktuellen Benutzer entfernt.
echo Browserdaten bleiben erhalten und koennen bei Bedarf manuell geloescht werden:
echo %%LOCALAPPDATA%%\SecureWindowApp
exit /b 0
