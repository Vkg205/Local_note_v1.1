$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$issues = New-Object System.Collections.Generic.List[string]

Write-Host "[validate] Repository root: $root"

# This validator is intentionally advisory. dotnet build is the authoritative gate.
# The manifest exists mainly to catch incomplete GitHub Web uploads and report the
# exact missing file names instead of a generic validation failure.
$manifestPath = Join-Path $root 'SOURCE_MANIFEST.txt'
if (Test-Path $manifestPath) {
    $expected = Get-Content $manifestPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($item in $expected) {
        if (!(Test-Path (Join-Path $root $item))) {
            $issues.Add("Missing uploaded file: $item")
        }
    }
} else {
    $issues.Add('SOURCE_MANIFEST.txt is missing; Web-upload completeness cannot be checked.')
}

$projectPath = Join-Path $root 'src/LocalNote.csproj'
if (!(Test-Path $projectPath)) {
    $issues.Add('Missing critical project file: src/LocalNote.csproj')
} else {
    try { [xml](Get-Content $projectPath -Raw) | Out-Null }
    catch { $issues.Add("LocalNote.csproj XML parse failed: $($_.Exception.Message)") }

    $project = Get-Content $projectPath -Raw
    foreach ($setting in @(
        '<TargetFramework>net10.0-windows</TargetFramework>',
        '<UseWPF>true</UseWPF>',
        '<PlatformTarget>x64</PlatformTarget>',
        '<ImplicitUsings>enable</ImplicitUsings>',
        '<Nullable>enable</Nullable>'
    )) {
        if ($project -notmatch [regex]::Escape($setting)) {
            $issues.Add("Project setting missing: $setting")
        }
    }
}

# Parse XML/XAML that is actually present. Do not hard-code individual source files;
# the WPF compiler is better at validating code-behind bindings and C# references.
Get-ChildItem $root -Recurse -File -Include *.xaml,*.csproj,*.props,*.manifest | ForEach-Object {
    try { [xml](Get-Content $_.FullName -Raw) | Out-Null }
    catch { $issues.Add("XML/XAML parse failed: $($_.FullName) :: $($_.Exception.Message)") }
}

if ($issues.Count -gt 0) {
    Write-Warning "Repository pre-check found $($issues.Count) issue(s):"
    foreach ($issue in $issues) {
        Write-Warning " - $issue"
        Write-Output "::warning title=Repository pre-check::$issue"
    }
    Write-Host '[validate] Advisory check completed with warnings. CI will continue to dotnet build.' -ForegroundColor Yellow
    exit 2
}

Write-Host '[validate] PASS - source manifest, project settings and XML/XAML well-formedness verified.' -ForegroundColor Green
exit 0
