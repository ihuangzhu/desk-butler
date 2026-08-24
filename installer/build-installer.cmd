@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "ROOT=%~dp0.."
set "PUBLISH=%ROOT%\artifacts\publish\win-x64"
set "OUTPUT=%ROOT%\artifacts\installer"
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
set "ISCC_VERSION="

if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"

if not exist "%PUBLISH%\DeskButler.Desktop.exe" (
  echo ERROR: publish input is missing DeskButler.Desktop.exe.
  exit /b 1
)
if not exist "%PUBLISH%\DeskButler.Desktop.runtimeconfig.json" (
  echo ERROR: publish input is missing runtimeconfig.json.
  exit /b 1
)
if not exist "%PUBLISH%\DeskButler.Desktop.deps.json" (
  echo ERROR: publish input is missing deps.json.
  exit /b 1
)
if not exist "%PUBLISH%\coreclr.dll" (
  echo ERROR: publish output is not self-contained: coreclr.dll is missing.
  exit /b 1
)
if not exist "%PUBLISH%\hostfxr.dll" (
  echo ERROR: publish output is not self-contained: hostfxr.dll is missing.
  exit /b 1
)
if not exist "%ISCC%" (
  echo ERROR: official Inno Setup 7.1.0 ISCC.exe was not found.
  exit /b 1
)

for /f "usebackq delims=" %%V in (`"%ISCC%" --version`) do set "ISCC_VERSION=%%V"
if not "%ISCC_VERSION%"=="7.1.0" (
  echo ERROR: expected Inno Setup 7.1.0, found %ISCC_VERSION%.
  exit /b 1
)

echo Inno Setup compiler: %ISCC_VERSION%
if not exist "%OUTPUT%" mkdir "%OUTPUT%"
"%ISCC%" --no-ide-signtools "%ROOT%\installer\DeskButler.iss"
if errorlevel 1 exit /b %errorlevel%

if not exist "%OUTPUT%\DeskButler-Setup-0.1.0.exe" (
  echo ERROR: expected installer output was not created.
  exit /b 1
)

echo Installer: %OUTPUT%\DeskButler-Setup-0.1.0.exe
exit /b 0
