# LocalNote V0.8.0 Static / Build-chain Audit

本环境没有 Windows/.NET WPF 编译器，因此本文件只记录打包前静态审查；真实编译仍由 Windows 本机或 GitHub Actions 完成。

检查项：

- 完整 `src` 源码存在，扁平目录结构。
- `LocalNote.csproj`：`net10.0-windows` / WPF / x64 / nullable / implicit usings。
- XAML / csproj / manifest XML well-formedness。
- XAML `x:Class` code-behind 映射。
- MainWindow / Dialog XAML Event Handler 映射。
- StaticResource / DynamicResource 引用存在。
- V0.8 新增 `DrawingShapeControl.cs` 已包含在 SDK-style project 自动编译范围。
- Shape：页面绘图层 + 内容对象 OverlayLayer 两种宿主模型。
- PageCanvas / MainWindow changed-file private field reference audit。
- GitHub CI：普通 Build 与 win-x64 self-contained Publish 分阶段，Publish 允许 Runtime Pack restore。
- CI 手工触发，失败时上传 log/binlog diagnostics。
