# GitHub Web upload - V0.7.6

For the cleanest result, replace the repository with this package as the new baseline.

If you do not want to replace every file, at minimum replace:

- `.github/workflows/ci.yml`
- `.github/workflows/release.yml`
- `BUILD_LOCALNOTE.cmd`
- `build.ps1`
- `publish-win-x64.ps1`
- `src/LocalNote.csproj`
- `src/GlobalUsings.cs`
- `validate-project.ps1`

But because earlier Web edits changed several source files, uploading the full `src` directory from this package is safer.

After upload:

1. Actions
2. LocalNote CI - Windows x64
3. Run workflow
4. If successful, download `LocalNote-win-x64-*`
5. If failed, also download `LocalNote-build-diagnostics-*`; it now contains `build.log` / `publish.log` and MSBuild binlogs.
