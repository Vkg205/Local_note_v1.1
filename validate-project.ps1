$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Write-Host "[validate] Repository root: $root"

$required = @(
  'LocalNote.sln',
  'global.json',
  'src/LocalNote.csproj',
  'src/GlobalUsings.cs',
  'src/App.xaml',
  'src/App.xaml.cs',
  'src/MainWindow.xaml',
  'src/MainWindow.xaml.cs',
  'src/PageCanvasView.cs',
  'src/SqliteDataStore.cs',
  'src/LocalNote.ico'
)
foreach ($item in $required) {
  $path = Join-Path $root $item
  if (!(Test-Path $path)) { throw "Missing required file: $item" }
}

# Deterministic XML/XAML well-formedness only. Actual XAML/code-behind binding
# is validated by the WPF compiler in dotnet build.
Get-ChildItem $root -Recurse -File -Include *.xaml,*.csproj,*.props,*.manifest | ForEach-Object {
  try { [xml](Get-Content $_.FullName -Raw) | Out-Null }
  catch { throw "XML/XAML parse failed: $($_.FullName) :: $($_.Exception.Message)" }
}

$project = Get-Content (Join-Path $root 'src/LocalNote.csproj') -Raw
$requiredSettings = @(
  '<TargetFramework>net10.0-windows</TargetFramework>',
  '<UseWPF>true</UseWPF>',
  '<EnableWindowsTargeting>true</EnableWindowsTargeting>',
  '<PlatformTarget>x64</PlatformTarget>',
  '<ImplicitUsings>enable</ImplicitUsings>',
  '<Nullable>enable</Nullable>'
)
foreach ($setting in $requiredSettings) {
  if ($project -notmatch [regex]::Escape($setting)) { throw "Required project setting missing: $setting" }
}

$globalUsings = Get-Content (Join-Path $root 'src/GlobalUsings.cs') -Raw
foreach ($ns in @('System.IO','System.Threading.Tasks','System.Collections.Generic','System.Text.Json')) {
  if ($globalUsings -notmatch "global using $([regex]::Escape($ns));") { throw "Required global using missing: $ns" }
}

Write-Host '[validate] PASS - required files, project settings and XML/XAML well-formedness verified.' -ForegroundColor Green
