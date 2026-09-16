# LocalNote V0.8.0 UX Cleanup

## 优先修复项

1. Ribbon 从任意高度改为展开 / 紧凑 / 隐藏三态。
2. 统一“本地仓库”术语。
3. 删除重复的当前笔记本展示。
4. 页面移动/复制/置顶/删除改为页面就近菜单。
5. 新建 Notebook / Section / Page 后直接进入可记录状态。
6. 记忆并恢复最后活动 Notebook / Section / Page。
7. 支持 Space + 左键拖动画布；缩放围绕鼠标位置。
8. 内容块取消永久标题拖动栏，改为边缘直接移动 + 更大的 Resize Handle。
9. Shape 工具状态与 Shape 格式状态分离，单次绘制后返回选择模式。
10. 增加全局 Undo/Redo 基础，覆盖对象 / Shape / Ink / 表格主要操作。
11. 文本块默认自动增高，仅主要调整宽度。
12. Todo 统一为段落级模型；删除对象级 Todo UI；完成态可勾选并持久化。
13. 状态反馈从“永远绿色”改为状态语义颜色，保存状态独立显示。

## Shape 图层模型

- Page Drawing Layer：页面级 Shape 与 Ink 共享绘图语义，Shape 不使用 `CanvasObjectControl` 卡片包装。
- Nested Drawing Layer：当 Shape 起点位于 Text / Table / Image 内容对象内部时，Shape 保存 `ParentObjectId`，渲染到该对象的 OverlayLayer，并随父对象移动。
- 存储仍使用 ContentObject(Type=Shape) 以保留持久化、Undo/Redo、复制、删除能力，但 UI 不会额外生成一层内容卡片。
