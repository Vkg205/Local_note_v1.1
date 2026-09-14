$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
& .\validate-project.ps1

# First validate normal compilation without RID-specific runtime packs.
dotnet restore .\src\LocalNote.csproj
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build .\src\LocalNote.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host 'Release compile completed.' -ForegroundColor Green
