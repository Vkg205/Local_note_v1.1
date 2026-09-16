# LocalNote V0.8.5 Build / Stability Audit

This package was re-audited after the V0.8.4 stability fixes and the final V0.8.5 data-consistency changes.

## Fixed in V0.8.5

- Serialized `UserSettingsService` read-modify-write operations across the process and switched to unique atomic temp files.
- Removed duplicate Notebook / Section / Page loading during search-result navigation.
- Persisted Page paper style (`Blank / Grid / Lines / Dots`) in SQLite and restored it when switching/reopening pages.
- Added deletion provenance so restoring a Notebook/Section does not revive children that were deleted independently.
- Advanced SQLite schema to version 5 with automatic migration for existing vaults.
- About dialog now reads the executable assembly version instead of a hard-coded release string.

## Static checks

- XML / XAML / project / manifest parsing: PASS
- XAML code-behind event mapping: PASS
- StaticResource reference audit: PASS
- Modified C# delimiter scan: PASS
- Source manifest completeness: PASS
- Project settings (`net10.0-windows`, WPF, x64, 0.8.5): PASS
- Navigation single-transaction audit: PASS
- User settings atomic-write audit: PASS
- SQLite schema v5 creation smoke test: PASS
- Trash independent-child restore smoke test: PASS
- GitHub workflow YAML parse: PASS

The current environment does not include the Windows .NET/WPF toolchain, so the authoritative compile gate remains the included GitHub Actions Windows workflow.
