@echo off
setlocal EnableExtensions

REM Installiert SecureWindowApp nur fuer den aktuellen Benutzer.
REM Es werden keine Administratorrechte angefordert und keine Dateien in
REM C:\Program Files oder HKLM geschrieben.
cd /d "%~dp0"

set "INSTALL_DIR=%LOCALAPPDATA%\Programs\SecureWindowApp"
set "START_MENU=%APPDATA%\Microsoft\Windows\Start Menu\Programs"

if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
if errorlevel 1 (
    echo Der Benutzer-Installationsordner konnte nicht erstellt werden.
    pause
    exit /b 1
)

copy /Y "%~dp0SecureWindowApp.exe" "%INSTALL_DIR%\SecureWindowApp.exe" >nul
copy /Y "%~dp0Start-SecureWindowApp.bat" "%INSTALL_DIR%\Start-SecureWindowApp.bat" >nul
copy /Y "%~dp0MicrosoftEdgeWebView2Setup.exe" "%INSTALL_DIR%\MicrosoftEdgeWebView2Setup.exe" >nul
copy /Y "%~dp0SETUP.txt" "%INSTALL_DIR%\SETUP.txt" >nul

if errorlevel 1 (
    echo Die Dateien konnten nicht in den Benutzerordner kopiert werden.
    pause
    exit /b 1
)

if not exist "%START_MENU%" mkdir "%START_MENU%"
set "SHORTCUT=%START_MENU%\SecureWindowApp.lnk"

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$shell = New-Object -ComObject WScript.Shell; $shortcut = $shell.CreateShortcut($env:SHORTCUT); $shortcut.TargetPath = (Join-Path $env:INSTALL_DIR 'Start-SecureWindowApp.bat'); $shortcut.WorkingDirectory = $env:INSTALL_DIR; $shortcut.IconLocation = (Join-Path $env:INSTALL_DIR 'SecureWindowApp.exe'); $shortcut.Description = 'SecureWindowApp starten'; $shortcut.Save()"

if errorlevel 1 (
    echo Die Verknuepfung konnte nicht erstellt werden. Die App ist trotzdem installiert.
) else (
    echo SecureWindowApp wurde fuer den aktuellen Benutzer installiert.
    echo Startmenue-Verknuepfung: SecureWindowApp
)

echo.
echo Die App wird jetzt gestartet. Falls WebView2 fehlt, kann dessen Microsoft-Installer
echo je nach Windows-Konfiguration noch eine gesonderte Berechtigung verlangen.
start "" "%INSTALL_DIR%\Start-SecureWindowApp.bat"
exit /b 0
