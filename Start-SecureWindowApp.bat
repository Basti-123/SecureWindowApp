@echo off
setlocal EnableExtensions

REM Startet SecureWindowApp ohne Administratorrechte.
REM Falls WebView2 fehlt, wird der offizielle Microsoft-Bootstrapper
REM fuer den aktuellen Benutzer ausgefuehrt.
cd /d "%~dp0"

set "WEBVIEW2_GUID={F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
call :detect_webview2
if not errorlevel 1 goto start_app

if not exist "%~dp0MicrosoftEdgeWebView2Setup.exe" (
    echo Der WebView2-Bootstrapper fehlt.
    echo Bitte lade ihn von Microsoft herunter:
    start "" "https://developer.microsoft.com/microsoft-edge/webview2/"
    pause
    exit /b 1
)

echo WebView2 Runtime ist nicht installiert.
echo Die Installation wird nur fuer den aktuellen Benutzer gestartet.
echo.
start "" /wait "%~dp0MicrosoftEdgeWebView2Setup.exe" /install
set "WEBVIEW2_INSTALL_EXIT=%ERRORLEVEL%"

REM Der Bootstrapper liefert bei einer bereits systemweit vorhandenen
REM Runtime je nach Version trotzdem einen Fehlercode. Deshalb wird nach
REM jedem Installationsversuch erneut in allen relevanten Registry-Ansichten
REM gesucht, bevor der Start abgebrochen wird.
call :detect_webview2
if not errorlevel 1 goto start_app

if not "%WEBVIEW2_INSTALL_EXIT%"=="0" (
    echo.
    echo Die WebView2-Installation ist fehlgeschlagen oder wurde abgebrochen (Code %WEBVIEW2_INSTALL_EXIT%).
    pause
    exit /b 1
)

echo WebView2 Runtime wurde nach der Installation nicht gefunden.
pause
exit /b 1

:start_app
if not exist "%~dp0SecureWindowApp.exe" (
    echo SecureWindowApp.exe wurde nicht gefunden.
    pause
    exit /b 1
)

start "" "%~dp0SecureWindowApp.exe"
exit /b 0

:detect_webview2
set "WEBVIEW2_VERSION="

REM Per-User-Installation (32/64 Bit)
for /f "tokens=2,*" %%A in ('reg.exe query "HKCU\Software\Microsoft\EdgeUpdate\Clients\%WEBVIEW2_GUID%" /v pv 2^>nul ^| findstr /i /c:"pv"') do if /i "%%A"=="REG_SZ" set "WEBVIEW2_VERSION=%%B"
if not defined WEBVIEW2_VERSION for /f "tokens=2,*" %%A in ('reg.exe query "HKCU\Software\WOW6432Node\Microsoft\EdgeUpdate\Clients\%WEBVIEW2_GUID%" /v pv 2^>nul ^| findstr /i /c:"pv"') do if /i "%%A"=="REG_SZ" set "WEBVIEW2_VERSION=%%B"

REM Systemweite Installation in der 64-Bit- und 32-Bit-Registry-Ansicht
if not defined WEBVIEW2_VERSION for /f "tokens=2,*" %%A in ('reg.exe query "HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\%WEBVIEW2_GUID%" /v pv 2^>nul ^| findstr /i /c:"pv"') do if /i "%%A"=="REG_SZ" set "WEBVIEW2_VERSION=%%B"
if not defined WEBVIEW2_VERSION for /f "tokens=2,*" %%A in ('reg.exe query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\%WEBVIEW2_GUID%" /v pv 2^>nul ^| findstr /i /c:"pv"') do if /i "%%A"=="REG_SZ" set "WEBVIEW2_VERSION=%%B"

if defined WEBVIEW2_VERSION exit /b 0
exit /b 1
