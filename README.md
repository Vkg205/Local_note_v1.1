# LocalNote V0.7.6

Windows x64 local OneNote-style notebook prototype.

## GitHub build

Run **Actions → LocalNote CI - Windows x64 → Run workflow**.

The build pipeline performs:

1. repository validation
2. normal .NET restore
3. Release compile
4. win-x64 self-contained single-file publish (with runtime-pack restore enabled)
5. EXE verification
6. ZIP artifact upload
7. diagnostics upload

Output: `LocalNote-win-x64-<commit>.zip`.

See `BUILD_AUDIT_V0_7_6.md` and `GITHUB_WEB_UPLOAD_GUIDE.md`.
