# LocalNote V0.8.5 Stability Final

Windows x64 本地 OneNote 类自由笔记工具。V0.8.5 在 V0.8.x UX Cleanup 基线上集中修复数据一致性、导航竞态、纸张样式持久化和回收站层级恢复问题。

## 本版重点

- `UserSettingsService` 改为进程级串行读改写，并使用唯一临时文件原子替换，避免布局与导航状态互相覆盖。
- 搜索/结果跳转改为单一导航事务，避免 Notebook / Section / Page 重复异步加载导致页面跳回。
- 页面纸张样式（空白 / 网格 / 横线 / 点阵）正式进入 Page 数据模型，切页和重启后保持。
- 回收站增加删除来源 `deleted_by`，恢复 Notebook / Section 时只恢复由该次父级删除带走的后代，独立删除的子项继续留在回收站。
- 保留 V0.8.4 已完成的仓库切换安全、Ink 擦除 Undo、Shape 交互、Todo、备份临时文件清理等稳定性修复。

## 编译

本机：双击 `BUILD_LOCALNOTE.cmd`。

GitHub Web：上传完整仓库后，进入 `Actions -> LocalNote CI - Windows x64 -> Run workflow`。

目标输出：`dist/win-x64/LocalNote.exe`。
