@echo off
setlocal
title Team Hub Server Hosting

set "INSTALLER=%~dp0scripts\hosting\Install-TeamHub.ps1"
if not exist "%INSTALLER%" (
    echo Team Hub hosting installer was not found:
    echo %INSTALLER%
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Team Hub hosting did not complete successfully.
) else (
    echo Team Hub hosting completed successfully.
)
pause
exit /b %EXIT_CODE%
