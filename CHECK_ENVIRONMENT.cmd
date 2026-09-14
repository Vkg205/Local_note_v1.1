@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title LocalNote Environment Check
echo ============================================================
echo LocalNote build environment check
echo ============================================================
where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] dotnet command not found.
  echo Install Microsoft .NET 10 SDK x64.
  pause
  exit /b 2
)
echo [OK] dotnet command found.
dotnet --list-sdks
echo.
dotnet --version
echo.
echo Required SDK baseline: 10.0.401 or compatible .NET 10 SDK.
pause
