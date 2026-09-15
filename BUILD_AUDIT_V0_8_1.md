# LocalNote V0.8.1 CI / Web Upload Audit

- `SOURCE_MANIFEST.txt` lists every file expected under the flattened `src` tree.
- Repository pre-check is advisory and cannot hide real compiler diagnostics.
- `dotnet restore` and `dotnet build` are the authoritative compile gates.
- `dotnet publish -r win-x64 --self-contained true` may restore runtime packs itself.
- Build/publish failures print the tail of the real log directly into the GitHub job and upload `.log` + `.binlog` diagnostics.
- CI records `${{ github.ref_name }}` and `${{ github.sha }}` so Web users can detect building the wrong branch.
- Release workflow uses the same non-blocking pre-check policy.
