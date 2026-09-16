# GitHub Web 上传指南 — LocalNote V0.8.5

本包继续使用扁平 `src/`，源码目录内部没有多级子文件夹，便于 GitHub Web 上传。

建议上传顺序：

1. 根目录全部普通文件。
2. 进入 `src/`，一次选择并上传 `src` 中全部文件。
3. 创建 `.github/workflows/`，上传 `ci.yml` 与 `release.yml`。

上传完成后进入：`Actions -> LocalNote CI - Windows x64 -> Run workflow`。

CI 为手动触发，不会因为 Web 端分批上传而连续运行。

## 重要：运行 Workflow 时确认分支

GitHub Actions 的 **Run workflow** 下拉框会让你选择分支。必须选择你刚刚上传最新源码的分支。
例如源码上传在 `main`，就不要在 `1.1` 旧分支上运行。CI 的 diagnostics 中会写入实际构建的 Branch 与 Commit。

V0.8.1 起，`Validate repository` 只做 Web 上传完整性提示，不会再阻断真实 `dotnet build`。
如果漏文件，它会直接列出 `Missing uploaded file: src/xxx.cs`，随后仍继续编译，让真正的 C#/XAML 错误显示出来。
