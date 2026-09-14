$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\validate-project.ps1

$out = Join-Path $PSScriptRoot 'dist\win-x64'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Important: no --no-restore here. dotnet publish must restore win-x64 runtime packs.
dotnet publish .\src\LocalNote.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $out 'LocalNote.exe'
if (!(Test-Path $exe)) { throw 'LocalNote.exe was not produced.' }
Write-Host "Published: $exe" -ForegroundColor Green
