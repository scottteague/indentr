@echo off
setlocal enabledelayedexpansion

:: ── Indentr Installer Build Script ──────────────────────────────────────────
::
:: Publishes the app as a self-contained Windows executable, then compiles
:: the Inno Setup script into a setup .exe in installer\output\.
::
:: Requirements (both must be on PATH or at the default locations below):
::   - .NET 10 SDK      https://dotnet.microsoft.com/download
::   - Inno Setup 6.1+  https://jrsoftware.org/isdl.php

set ROOT=%~dp0..
set PROJECT=%ROOT%\Indentr.UI\Indentr.UI.csproj
set PUBLISH_OUT=%ROOT%\Indentr.UI\bin\Release\net10.0\win-x64\publish

:: Optional version override: build.bat 1.0
:: If omitted, the version defined in indentr.iss is used.
if "%~1"=="" (
  set VERSION_ARG=
) else (
  set VERSION_ARG=/DMyAppVersion="%~1"
)

:: ── Step 1: dotnet publish ────────────────────────────────────────────────────

echo.
echo [1/2] Publishing Indentr.UI (self-contained, win-x64) ...
echo.

dotnet publish "%PROJECT%" ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  --output "%PUBLISH_OUT%"

if errorlevel 1 (
  echo.
  echo ERROR: dotnet publish failed. Aborting.
  exit /b 1
)

echo.
echo Publish succeeded: %PUBLISH_OUT%

:: ── Step 2: Compile the Inno Setup script ────────────────────────────────────

echo.
echo [2/2] Compiling Inno Setup script ...
echo.

:: Look for ISCC.exe in the two common install locations.
set ISCC=
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
  set ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe
) else if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
  set ISCC=C:\Program Files\Inno Setup 6\ISCC.exe
) else (
  :: Try PATH
  where ISCC >nul 2>&1
  if not errorlevel 1 (
    set ISCC=ISCC
  ) else (
    echo ERROR: ISCC.exe not found.
    echo Install Inno Setup 6 from https://jrsoftware.org/isdl.php
    exit /b 1
  )
)

"%ISCC%" %VERSION_ARG% "%~dp0indentr.iss"

if errorlevel 1 (
  echo.
  echo ERROR: Inno Setup compilation failed.
  exit /b 1
)

echo.
echo ─────────────────────────────────────────────────────
echo  Installer built successfully:
echo  %~dp0output\
echo  (see installer\output\ for the .exe)
echo ─────────────────────────────────────────────────────
echo.
