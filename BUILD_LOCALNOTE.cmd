@echo off
setlocal EnableExtensions
cd /d "%~dp0"
title LocalNote Build and Publish
echo ============================================================
echo LocalNote V0.8.5 - Windows x64 Build
echo ============================================================
echo Project folder: %CD%
echo.
where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet

echo [1/4] Validate repository...
powershell -NoProfile -ExecutionPolicy Bypass -File "%CD%\validate-project.ps1"
if errorlevel 1 goto :failed

echo [2/4] Restore and compile Release...
dotnet restore "%CD%\src\LocalNote.csproj"
if errorlevel 1 goto :failed
dotnet build "%CD%\src\LocalNote.csproj" -c Release --no-restore
if errorlevel 1 goto :failed

echo [3/4] Publish self-contained win-x64 EXE...
if exist "%CD%\dist\win-x64" rmdir /s /q "%CD%\dist\win-x64"
REM Do not add --no-restore here: publish needs the win-x64 runtime packs.
dotnet publish "%CD%\src\LocalNote.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o "%CD%\dist\win-x64"
if errorlevel 1 goto :failed

echo [4/4] Verify output...
if not exist "%CD%\dist\win-x64\LocalNote.exe" goto :noexe

echo.
echo ============================================================
echo SUCCESS
echo EXE: %CD%\dist\win-x64\LocalNote.exe
echo ============================================================
start "" "%CD%\dist\win-x64"
pause
exit /b 0

:nodotnet
echo ERROR: .NET 10 SDK not found.
echo Install Microsoft .NET 10 SDK x64, then retry.
pause
exit /b 2

:noexe
echo ERROR: build completed but LocalNote.exe was not found.
goto :failed

:failed
echo.
echo ============================================================
echo BUILD FAILED - review the first ERROR above.
echo ============================================================
pause
exit /b 1
