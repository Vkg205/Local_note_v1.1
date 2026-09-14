# LocalNote V0.7.6 Build Audit

This package is based on the flattened Web-upload repository and incorporates all prior compile-foundation fixes.

## Critical change in V0.7.6

The previous CI mixed RID-specific restore/build with `--no-restore` publish. GitHub reported missing:

- `Microsoft.WindowsDesktop.App.Runtime.win-x64`
- `Microsoft.NETCore.App.Runtime.win-x64`

V0.7.6 separates the phases:

1. `dotnet restore src/LocalNote.csproj`
2. `dotnet build ... --no-restore` (normal compile, no RID runtime-pack dependency)
3. `dotnet publish ... -r win-x64 --self-contained true` **without `--no-restore`**

This allows `dotnet publish` to restore the Windows x64 runtime packs itself.

## Diagnostics

CI now always uploads `LocalNote-build-diagnostics-*` containing text logs and MSBuild `.binlog` files. If a future run fails, the actual failing phase is visible instead of only `exit code 1`.

## Static audit performed

- Repository file structure
- XML/XAML well-formedness
- XAML `x:Class` to code-behind mapping
- XAML event-handler existence
- `StaticResource` key resolution for local resources
- `.csproj` / `.props` / manifest XML parsing
- Solution project path
- Global using foundation
- Duplicate C# type-name scan
- GitHub workflow YAML parse
- CI project path and output path checks
- Windows CMD CRLF check
- ZIP integrity

A real Windows/.NET compilation cannot be executed in the current Linux container; GitHub Actions remains the authoritative compile gate.
