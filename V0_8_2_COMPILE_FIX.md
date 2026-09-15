# LocalNote V0.8.2 Compile Fix

本版本针对 GitHub Actions #38 的真实 C# 编译错误进行修复：

- 修复 `MainWindow.xaml.cs` 中 `Page` 类型歧义：明确使用 `LocalNote.Domain.Entities.Page` 别名 `NotePage`。
- 将自定义事件 `ManipulationStarted` 重命名为 `ObjectManipulationStarted`，避免覆盖 WPF `UIElement.ManipulationStarted`。
- 移除已不再使用的 `TagsChanged` 事件及订阅，清除无效 warning。
- 保留 V0.8.1 的 CI diagnostics / SOURCE_MANIFEST / runtime restore 策略。

建议 Web 上传至少替换：
1. `src/MainWindow.xaml.cs`
2. `src/CanvasObjectControl.cs`
3. `src/DrawingShapeControl.cs`
4. `src/PageCanvasView.cs`
5. `src/LocalNote.csproj`（版本更新为 0.8.2）

为了避免旧文件残留，推荐直接用完整 V0.8.2 包作为新基线。
