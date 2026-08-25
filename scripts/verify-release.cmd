@echo off
setlocal EnableExtensions DisableDelayedExpansion

for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "PUBLISH_PARENT=%ROOT%\artifacts\publish"
set "PUBLISH_STAGING=%PUBLISH_PARENT%\win-x64.staging"
set "PUBLISH_FINAL=%PUBLISH_PARENT%\win-x64"
set "INSTALLER=%ROOT%\artifacts\installer\DeskButler-Setup-0.1.0.exe"
if not defined DOTNET_CMD set "DOTNET_CMD=dotnet"
if not defined CERTUTIL_CMD set "CERTUTIL_CMD=certutil"

if not exist "%ROOT%\.git" (
  echo ERROR: DeskButler repository root was not found.
  exit /b 1
)
if /i not "%PUBLISH_STAGING%"=="%ROOT%\artifacts\publish\win-x64.staging" exit /b 1
if /i not "%PUBLISH_FINAL%"=="%ROOT%\artifacts\publish\win-x64" exit /b 1

cd /d "%ROOT%"
if errorlevel 1 exit /b %errorlevel%

call "%DOTNET_CMD%" restore DeskButler.slnx
if errorlevel 1 exit /b %errorlevel%

call "%DOTNET_CMD%" build DeskButler.slnx -c Release --no-restore
if errorlevel 1 exit /b %errorlevel%

call "%DOTNET_CMD%" test DeskButler.slnx -c Release --no-build
if errorlevel 1 exit /b %errorlevel%

if exist "%PUBLISH_STAGING%" rmdir /s /q "%PUBLISH_STAGING%"
if errorlevel 1 exit /b %errorlevel%
call "%DOTNET_CMD%" publish src\DeskButler.Desktop -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64.staging
if errorlevel 1 exit /b %errorlevel%

if exist "%PUBLISH_FINAL%" rmdir /s /q "%PUBLISH_FINAL%"
if errorlevel 1 exit /b %errorlevel%
move "%PUBLISH_STAGING%" "%PUBLISH_FINAL%" >nul
if errorlevel 1 exit /b %errorlevel%

call installer\build-installer.cmd
if errorlevel 1 exit /b %errorlevel%

if not exist "%INSTALLER%" (
  echo ERROR: expected installer was not created: %INSTALLER%
  exit /b 1
)
call "%CERTUTIL_CMD%" -hashfile "%INSTALLER%" SHA256
if errorlevel 1 exit /b %errorlevel%

exit /b 0
