@echo off
setlocal
title Publish Team Hub Package

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\TeamHub.Web\TeamHub.Web.csproj"
set "CONFIG_SOURCE=%ROOT%config"

if not exist "%PROJECT%" goto :missing_project

where dotnet.exe >nul 2>&1
if errorlevel 1 goto :missing_dotnet

dotnet.exe --list-sdks | findstr.exe /R /B /C:"10\." >nul
if errorlevel 1 goto :missing_sdk

for /f %%I in ('powershell.exe -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%I"
set "DEFAULT_OUTPUT=%ROOT%artifacts\TeamHub-Publish-%STAMP%"

echo.
echo Team Hub Package Publisher
echo Press Enter to use the default output directory.
set "OUTPUT="
set /p "OUTPUT=Output directory [%DEFAULT_OUTPUT%]: "
if not defined OUTPUT set "OUTPUT=%DEFAULT_OUTPUT%"
for %%I in ("%OUTPUT%") do set "OUTPUT=%%~fI"

if exist "%OUTPUT%\" goto :check_output
mkdir "%OUTPUT%"
if errorlevel 1 goto :fail_directory
goto :publish

:check_output
for /f %%I in ('dir /b /a "%OUTPUT%" 2^>nul') do goto :output_not_empty

:publish
echo.
echo Publishing Team Hub in Release mode...
dotnet.exe publish "%PROJECT%" --configuration Release --output "%OUTPUT%"
if errorlevel 1 goto :fail_publish
if not exist "%OUTPUT%\TeamHub.Web.dll" goto :fail_verify
if not exist "%OUTPUT%\web.config" goto :fail_verify

if exist "%CONFIG_SOURCE%\" (
    echo Adding the default Team Hub configuration...
    robocopy.exe "%CONFIG_SOURCE%" "%OUTPUT%\config" /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /NFL /NDL /NP >nul
    if errorlevel 8 goto :fail_config
)

echo.
echo Team Hub package created successfully:
echo %OUTPUT%
echo.
echo Copy this entire folder to the Windows server, then provide its path
echo when Host-TeamHub.bat asks for the source repository or published package.
pause
exit /b 0

:missing_project
echo Team Hub web project was not found: %PROJECT%
goto :fail

:missing_dotnet
echo The .NET SDK is not installed or dotnet.exe is not on PATH.
echo Install the .NET 10 SDK and run this file again.
goto :fail

:missing_sdk
echo The .NET 10 SDK is required to publish Team Hub.
goto :fail

:output_not_empty
echo The output directory already contains files: %OUTPUT%
echo Choose a new or empty directory. Existing packages are never deleted automatically.
goto :fail

:fail_directory
echo The output directory could not be created: %OUTPUT%
goto :fail

:fail_publish
echo Team Hub publishing failed. Review the build errors shown above.
goto :fail

:fail_verify
echo Publishing did not produce TeamHub.Web.dll and web.config.
goto :fail

:fail_config
echo The application was published, but the default configuration could not be copied.

:fail
echo.
pause
exit /b 1
