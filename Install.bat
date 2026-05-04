@echo off
set "SCRIPT=%~dp0GGSystemMonitor\Install.ps1"

if not exist "%SCRIPT%" (
    echo Install.ps1 was not found at:
    echo   %SCRIPT%
    echo.
    echo The release folder must contain Install.bat next to the GGSystemMonitor folder.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%SCRIPT%"
