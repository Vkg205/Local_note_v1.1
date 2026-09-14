using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalNote.Domain.Entities;
using LocalNote.Storage.Repositories;
using LocalNote.Storage.Services;

namespace LocalNote.App.Controls;

public enum CanvasTool { Select, Pen, Pencil, Highlighter, Eraser, InkSelect, Shape }
public enum PaperStyle { Blank, Grid, Lines, Dots }

public sealed class PageCanvasView : UserControl
{
    private readonly ScrollViewer _scroll;
    private readonly Grid _surface;
    private readonly Border _paper;
    private readonly Canvas _objects;
    private readonly InkCanvas _ink;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly DispatcherTimer _inkSaveTimer;
    private readonly Dictionary<string, DispatcherTimer> _textTimers = new();
    private readonly Dictionary<string, Func<Task>> _pendingObjectSaves = new();
    private ContentObjectRepository? _contentRepository;
    private InkRepository? _inkRepository;
    private MediaStoreService? _mediaStore;
    private string? _pageId;
    private CanvasObjectControl? _selected;
    private RichTextBox? _activeRichText;
    private Point? _panStart;
    private double _startH;
    private double _startV;
    private CanvasTool _currentTool = CanvasTool.Select;
    private Color _inkColor = Colors.Black;
    private double _inkSize = 2.2;
    private PaperStyle _paperStyle = PaperStyle.Blank;
    private ShapeKind _shapeKind = ShapeKind.Rectangle;
    private Color _shapeStrokeColor = Color.FromRgb(108, 42, 165);
    private Color _shapeFillColor = Color.FromArgb(24, 108, 42, 165);
    private double _shapeThickness = 2.0;
    private Point? _shapeStart;
    private ShapePresenter? _shapePreview;

    public PageCanvasView()
    {
        _paper = new Border { Width = 6000, Height = 4000, Background = Brushes.White };
        _objects = new Canvas { Width = 6000, Height = 4000, Background = Brushes.Transparent, AllowDrop = true };
        _ink = new InkCanvas { Width = 6000, Height = 4000, Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.None, IsHitTestVisible = false };
        ApplyInkAttributes(false, false);

        _surface = new Grid { Width = 6000, Height = 4000, RenderTransformOrigin = new Point(0, 0), LayoutTransform = _scale };
        _surface.Children.Add(_paper); _surface.Children.Add(_objects); _surface.Children.Add(_ink);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false, Background = new SolidColorBrush(Color.FromRgb(239, 241, 246)), Content = _surface,
            PanningMode = PanningMode.Both
        };
        Content = _scroll;

        _scroll.PreviewMouseWheel += Scroll_PreviewMouseWheel;
        _scroll.PreviewMouseDown += Scroll_PreviewMouseDown;
        _scroll.PreviewMouseMove += Scroll_PreviewMouseMove;
        _scroll.PreviewMouseUp += (_, _) => { _panStart = null; if (_scroll.IsMouseCaptured) _scroll.ReleaseMouseCapture(); _scroll.Cursor = Cursors.Arrow; };
        _objects.MouseLeftButtonDown += Objects_MouseLeftButtonDown;
        _objects.MouseMove += Objects_MouseMove;
        _objects.MouseLeftButtonUp += Objects_MouseLeftButtonUp;
        _objects.Drop += Objects_Drop; _objects.DragOver += Objects_DragOver;

        _inkSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _inkSaveTimer.Tick += async (_, _) => { _inkSaveTimer.Stop(); await SaveInkAsync(); };
        _ink.StrokeCollected += (_, _) => QueueInkSave(); _ink.StrokeErased += (_, _) => QueueInkSave();
        _ink.SelectionMoved += (_, _) => QueueInkSave(); _ink.SelectionResized += (_, _) => QueueInkSave();
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? DirtyChanged;
    public event Action<CanvasTool>? ToolChanged;
    public event Action<ContentObject?>? SelectionChanged;
    public event Action<double>? ZoomChanged;
    public double Zoom => _scale.ScaleX;
    public ContentObject? SelectedObject => _selected?.Model;

    public async Task BindAsync(string? pageId, ContentObjectRepository? contentRepository, InkRepository? inkRepository, MediaStoreService? mediaStore)
    {
        await FlushAsync();
        _pageId = pageId; _contentRepository = contentRepository; _inkRepository = inkRepository; _mediaStore = mediaStore;
        _objects.Children.Clear(); _ink.Strokes.Clear(); _selected = null; _activeRichText = null;
        SelectionChanged?.Invoke(null);
        SetTool(CanvasTool.Select);
        foreach (var timer in _textTimers.Values) timer.Stop(); _textTimers.Clear(); _pendingObjectSaves.Clear();
        if (pageId is null || contentRepository is null || inkRepository is null)
        {
            StatusChanged?.Invoke(this, "请选择或新建页面"); return;
        }
        foreach (var item in await contentRepository.GetByPageAsync(pageId)) AddObjectVisual(item);
        var inkData = await inkRepository.GetAsync(pageId);
        if (inkData is { Length: > 0 })
        {
            using var stream = new MemoryStream(inkData); _ink.Strokes = new StrokeCollection(stream);
        }
        ExpandSurfaceToContent(); StatusChanged?.Invoke(this, $"页面已载入 · {_objects.Children.Count} 个对象");
    }

    public void SetTool(CanvasTool tool)
    {
        if (_pageId is null && tool != CanvasTool.Select)
        {
            StatusChanged?.Invoke(this, "请先创建或选择页面，再使用绘图工具");
            tool = CanvasTool.Select;
        }

        if (_shapePreview is not null && tool != CanvasTool.Shape)
        {
            _objects.Children.Remove(_shapePreview);
            _shapePreview = null;
            _shapeStart = null;
            if (_objects.IsMouseCaptured) _objects.ReleaseMouseCapture();
        }
        _currentTool = tool;
        var inkMode = tool is CanvasTool.Pen or CanvasTool.Pencil or CanvasTool.Highlighter or CanvasTool.Eraser or CanvasTool.InkSelect;
        var shapeMode = tool == CanvasTool.Shape;
        _ink.IsHitTestVisible = inkMode;
        _objects.IsHitTestVisible = !inkMode;
        _objects.Cursor = shapeMode ? Cursors.Cross : Cursors.Arrow;
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>()) child.IsHitTestVisible = tool == CanvasTool.Select;
        _ink.EditingMode = tool switch
        {
            CanvasTool.Pen or CanvasTool.Pencil or CanvasTool.Highlighter => InkCanvasEditingMode.Ink,
            CanvasTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            CanvasTool.InkSelect => InkCanvasEditingMode.Select,
            _ => InkCanvasEditingMode.None
        };
        if (tool is CanvasTool.Pen or CanvasTool.Pencil or CanvasTool.Highlighter) ApplyInkAttributes(tool == CanvasTool.Highlighter, tool == CanvasTool.Pencil);
        ToolChanged?.Invoke(tool);
        StatusChanged?.Invoke(this, tool switch
        {
            CanvasTool.Pen => "钢笔模式 · 在画布上书写",
            CanvasTool.Pencil => "铅笔模式 · 适合草图与轻线条",
            CanvasTool.Highlighter => "荧光笔模式 · 在画布上标注",
            CanvasTool.Eraser => "橡皮擦模式 · 点击笔画即可整笔擦除",
            CanvasTool.InkSelect => "墨迹套索模式 · 圈选后可移动/缩放",
            CanvasTool.Shape => $"形状模式 · 拖动绘制{ShapeKindName(_shapeKind)}",
            _ => "选择模式 · 可编辑对象；双击空白创建文本"
        });
    }

    public void SetInkColor(Color color) { _inkColor = color; ApplyInkAttributes(_currentTool == CanvasTool.Highlighter, _currentTool == CanvasTool.Pencil); }
    public void SetInkSize(double size) { _inkSize = Math.Clamp(size, 0.5, 24); ApplyInkAttributes(_currentTool == CanvasTool.Highlighter, _currentTool == CanvasTool.Pencil); }
    public void SetShapeKind(ShapeKind kind)
    {
        _shapeKind = kind;
        SetTool(CanvasTool.Shape);
        StatusChanged?.Invoke(this, $"形状模式 · 拖动绘制{ShapeKindName(kind)}");
    }

    public void SetShapeStrokeColor(Color color)
    {
        _shapeStrokeColor = color;
        ApplyCurrentShapeStyleToSelection();
    }

    public void SetShapeFillColor(Color color)
    {
        _shapeFillColor = Color.FromArgb(42, color.R, color.G, color.B);
        ApplyCurrentShapeStyleToSelection();
    }

    public void SetShapeThickness(double thickness)
    {
        _shapeThickness = Math.Clamp(thickness, 0.5, 16);
        ApplyCurrentShapeStyleToSelection();
    }

    private void ApplyCurrentShapeStyleToSelection()
    {
        if (_selected is not { Model.Type: ContentObjectType.Shape } control) return;
        var kind = _shapeKind;
        try
        {
            var current = JsonSerializer.Deserialize<ShapePayload>(control.Model.Payload);
            if (current is not null && Enum.TryParse<ShapeKind>(current.Kind, true, out var parsed)) kind = parsed;
        }
        catch { }
        var payload = new ShapePayload(kind.ToString(), ToHex(_shapeStrokeColor), ToHex(_shapeFillColor), _shapeThickness);
        control.Model.Payload = JsonSerializer.Serialize(payload);
        if (FindDescendant<ShapePresenter>(control) is { } presenter)
            presenter.Update(kind, _shapeStrokeColor, _shapeFillColor, _shapeThickness);
        _ = SaveObjectAsync(control);
    }

    public void ClearInk()
    {
        if (_pageId is null) { StatusChanged?.Invoke(this, "请先选择页面"); return; }
        if (_ink.Strokes.Count == 0) { StatusChanged?.Invoke(this, "当前页没有墨迹可清除"); return; }
        _ink.Strokes.Clear(); QueueInkSave(); StatusChanged?.Invoke(this, "本页墨迹已清除");
    }

    public async Task AddTextAsync() => await AddTextAtAsync(GetInsertionPoint());
    public async Task AddTableAsync()
    {
        if (_pageId is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        SetTool(CanvasTool.Select);
        var data = TableEditorControl.CreateDefaultData(3, 3);
        var (x, y) = GetInsertionPoint();
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Table, x, y, 520, 260, JsonSerializer.Serialize(data));
        var visual = AddObjectVisual(model); SelectObject(visual); ScrollObjectIntoView(visual); DirtyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, "已插入表格 · 已预留表头名称行；选中表格后可在“表格”工具中编辑格式");
    }

    public async Task AddImageAsync(string sourcePath, Point? point = null)
    {
        if (_pageId is null || _contentRepository is null || _mediaStore is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        SetTool(CanvasTool.Select);
        var relative = await _mediaStore.ImportAsync(sourcePath, "images");
        var payload = JsonSerializer.Serialize(new MediaPayload(Path.GetFileName(sourcePath), relative));
        var pos = point is null ? GetInsertionPoint() : (point.Value.X, point.Value.Y);
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Image, pos.Item1, pos.Item2, 420, 300, payload);
        var visual = AddObjectVisual(model); SelectObject(visual); ExpandSurfaceToContent(); ScrollObjectIntoView(visual); DirtyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"已插入图片：{Path.GetFileName(sourcePath)}");
    }

    public async Task AddAttachmentAsync(string sourcePath, Point? point = null)
    {
        if (_pageId is null || _contentRepository is null || _mediaStore is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        SetTool(CanvasTool.Select);
        var relative = await _mediaStore.ImportAsync(sourcePath, "attachments");
        var payload = JsonSerializer.Serialize(new MediaPayload(Path.GetFileName(sourcePath), relative));
        var pos = point is null ? GetInsertionPoint() : (point.Value.X, point.Value.Y);
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Attachment, pos.Item1, pos.Item2, 360, 100, payload, Path.GetFileName(sourcePath));
        var visual = AddObjectVisual(model); SelectObject(visual); ScrollObjectIntoView(visual); DirtyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"已插入附件：{Path.GetFileName(sourcePath)}");
    }

    public async Task DeleteSelectedAsync()
    {
        if (_selected is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象"); return; }
        await _contentRepository.DeleteAsync(_selected.Model.Id); _objects.Children.Remove(_selected); _selected = null; _activeRichText = null;
        SelectionChanged?.Invoke(null);
        DirtyChanged?.Invoke(this, EventArgs.Empty); StatusChanged?.Invoke(this, "对象已删除");
    }

    public async Task DuplicateSelectedAsync()
    {
        if (_selected is null || _contentRepository is null || _pageId is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象再创建副本"); return; }
        var source = _selected.Model;
        if (source.Type == ContentObjectType.Text && FindDescendant<RichTextBox>(_selected) is { } rich)
        {
            source.Payload = XamlWriter.Save(rich.Document);
            source.SearchText = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Trim();
        }
        if (source.Type == ContentObjectType.Table && FindDescendant<TableEditorControl>(_selected) is { } table)
        {
            source.Payload = table.Payload; source.SearchText = table.SearchText;
        }
        var copy = await _contentRepository.CreateAsync(_pageId, source.Type, source.X + 24, source.Y + 24, source.Width, source.Height, source.Payload, source.SearchText);
        copy.StyleJson = source.StyleJson; copy.IsTodo = source.IsTodo; copy.TodoCompleted = source.TodoCompleted; copy.IsImportant = source.IsImportant;
        await _contentRepository.UpsertAsync(copy);
        SelectObject(AddObjectVisual(copy)); ExpandSurfaceToContent(); DirtyChanged?.Invoke(this, EventArgs.Empty); StatusChanged?.Invoke(this, "对象副本已创建");
    }

    public async Task ToggleTodoSelectedAsync()
    {
        if (_selected is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象，再添加待办标记"); return; }
        _selected.Model.IsTodo = !_selected.Model.IsTodo; if (!_selected.Model.IsTodo) _selected.Model.TodoCompleted = false;
        _selected.RefreshTagVisuals(); await SaveObjectAsync(_selected); StatusChanged?.Invoke(this, _selected.Model.IsTodo ? "已标记为待办" : "已移除待办标记");
    }
    public async Task ToggleImportantSelectedAsync()
    {
        if (_selected is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象，再添加重要标记"); return; }
        _selected.Model.IsImportant = !_selected.Model.IsImportant; _selected.RefreshTagVisuals(); await SaveObjectAsync(_selected);
        StatusChanged?.Invoke(this, _selected.Model.IsImportant ? "已标记为重要" : "已移除重要标记");
    }

    public void ToggleParagraphTodo()
    {
        var editor = ResolveRichTextEditor();
        if (editor is null) { StatusChanged?.Invoke(this, "请先把光标放到文本块的某一行，再设置待办项"); return; }
        var paragraph = editor.CaretPosition.Paragraph;
        if (paragraph is null) { StatusChanged?.Invoke(this, "当前光标位置不是可设置待办的段落"); return; }
        var existing = FindTodoContainer(paragraph);
        if (existing is not null)
        {
            paragraph.Inlines.Remove(existing);
            StatusChanged?.Invoke(this, "当前段落已恢复为普通文本；回车后将创建普通段落");
        }
        else
        {
            var todoInline = CreateTodoInline(editor, _selected?.Model);
            if (paragraph.Inlines.FirstInline is { } first) paragraph.Inlines.InsertBefore(first, todoInline);
            else paragraph.Inlines.Add(todoInline);
            StatusChanged?.Invoke(this, "当前段落已设为待办；回车将自动创建下一条待办");
        }
        editor.Focus();
        QueueSelectedTextSave();
    }

    public void SetParagraphAlignment(TextAlignment alignment) => ApplyRichCommand(editor =>
    {
        if (editor.CaretPosition.Paragraph is { } paragraph) paragraph.TextAlignment = alignment;
    }, alignment switch { TextAlignment.Center => "段落已居中", TextAlignment.Right => "段落已右对齐", _ => "段落已左对齐" });

    public void ChangeIndent(double delta) => ApplyRichCommand(editor =>
    {
        if (editor.CaretPosition.Paragraph is { } paragraph)
        {
            var left = Math.Max(0, paragraph.Margin.Left + delta);
            paragraph.Margin = new Thickness(left, paragraph.Margin.Top, paragraph.Margin.Right, paragraph.Margin.Bottom);
        }
    }, delta > 0 ? "段落缩进已增加" : "段落缩进已减少");

    public void SetLineSpacing(double factor) => ApplyRichCommand(editor =>
    {
        if (editor.CaretPosition.Paragraph is { } paragraph)
        {
            var fontSize = paragraph.FontSize > 0 ? paragraph.FontSize : 16;
            paragraph.LineHeight = Math.Max(fontSize + 2, fontSize * Math.Clamp(factor, 1.0, 2.5));
        }
    }, $"行距：{factor:0.##}");

    public void ApplyTextStyle(string style) => ApplyRichCommand(editor =>
    {
        if (editor.CaretPosition.Paragraph is not { } paragraph) return;
        switch (style)
        {
            case "H1": paragraph.FontSize = 30; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 10, 0, 5); break;
            case "H2": paragraph.FontSize = 24; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 8, 0, 4); break;
            case "H3": paragraph.FontSize = 19; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 6, 0, 3); break;
            case "H4": paragraph.FontSize = 17; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 5, 0, 2); break;
            case "H5": paragraph.FontSize = 15; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 4, 0, 2); break;
            case "H6": paragraph.FontSize = 14; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 3, 0, 2); break;
            case "Quote": paragraph.FontSize = 16; paragraph.FontStyle = FontStyles.Italic; paragraph.Foreground = new SolidColorBrush(Color.FromRgb(92, 83, 101)); paragraph.Margin = new Thickness(18, 5, 0, 5); break;
            default: paragraph.FontSize = 16; paragraph.FontWeight = FontWeights.Normal; paragraph.FontStyle = FontStyles.Normal; paragraph.Foreground = Brushes.Black; paragraph.Margin = new Thickness(0); break;
        }
    }, $"段落样式：{style}");

    public void ToggleSuperscript() => ApplyRichCommand(editor =>
    {
        var value = editor.Selection.GetPropertyValue(Inline.BaselineAlignmentProperty);
        var next = value is BaselineAlignment a && a == BaselineAlignment.Superscript ? BaselineAlignment.Baseline : BaselineAlignment.Superscript;
        editor.Selection.ApplyPropertyValue(Inline.BaselineAlignmentProperty, next);
    }, "上标已切换");

    public void ToggleSubscript() => ApplyRichCommand(editor =>
    {
        var value = editor.Selection.GetPropertyValue(Inline.BaselineAlignmentProperty);
        var next = value is BaselineAlignment a && a == BaselineAlignment.Subscript ? BaselineAlignment.Baseline : BaselineAlignment.Subscript;
        editor.Selection.ApplyPropertyValue(Inline.BaselineAlignmentProperty, next);
    }, "下标已切换");

    public void ToggleStrikethrough() => ApplyRichCommand(editor =>
    {
        var current = editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var decorations = current is TextDecorationCollection collection && collection.Any(d => d.Location == TextDecorationLocation.Strikethrough)
            ? null : TextDecorations.Strikethrough;
        editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, decorations);
    }, "删除线已切换");

    public void ClearTextFormatting() => ApplyRichCommand(editor =>
    {
        // Reset the common character properties explicitly. This is intentionally
        // conservative so that paragraph/list structure is not destroyed.
        editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI"));
        editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, 16d);
        editor.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
        editor.Selection.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
        editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
        editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        editor.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
        editor.Selection.ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Baseline);
    }, "已清除所选文本格式");

    public void InsertHyperlink(string url)
    {
        var editor = ResolveRichTextEditor();
        if (editor is null) { StatusChanged?.Invoke(this, "请先在文本块中选择文字，再插入链接"); return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { StatusChanged?.Invoke(this, "请输入有效的完整网址，例如 https://example.com"); return; }
        var text = editor.Selection.Text;
        if (string.IsNullOrWhiteSpace(text)) { StatusChanged?.Invoke(this, "请先选择要添加链接的文字"); return; }
        var start = editor.Selection.Start;
        editor.Selection.Text = string.Empty;
        var hyperlink = new Hyperlink(new Run(text), start) { NavigateUri = uri, Foreground = new SolidColorBrush(Color.FromRgb(78, 45, 160)), TextDecorations = TextDecorations.Underline };
        hyperlink.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        };
        editor.Focus();
        QueueSelectedTextSave();
        StatusChanged?.Invoke(this, "超链接已插入");
    }

    public void ToggleBold() => ApplyRichCommand(editor => EditingCommands.ToggleBold.Execute(null, editor), "粗体格式已应用");
    public void ToggleItalic() => ApplyRichCommand(editor => EditingCommands.ToggleItalic.Execute(null, editor), "斜体格式已应用");
    public void ToggleUnderline() => ApplyRichCommand(editor => EditingCommands.ToggleUnderline.Execute(null, editor), "下划线格式已应用");
    public void ToggleBullets() => ApplyRichCommand(editor => EditingCommands.ToggleBullets.Execute(null, editor), "项目符号已切换");
    public void ToggleNumbering() => ApplyRichCommand(editor => EditingCommands.ToggleNumbering.Execute(null, editor), "编号列表已切换");
    public void UndoRichText() => ApplyRichCommand(editor => { if (editor.CanUndo) editor.Undo(); }, "已撤销文本编辑");
    public void RedoRichText() => ApplyRichCommand(editor => { if (editor.CanRedo) editor.Redo(); }, "已重做文本编辑");
    public void SetFontSize(double size) => ApplyRichCommand(editor => editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size), $"字号已设为 {size:0.#}");
    public void SetTextColor(Color color) => ApplyRichCommand(editor => editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color)), "文字颜色已应用");
    public void SetTextHighlight(Color color) => ApplyRichCommand(editor => editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(color)), "文字高亮已应用");


    public bool HasSelectedTable => _selected?.Model.Type == ContentObjectType.Table;
    public bool SelectedTableHasHeader => ResolveTableEditor()?.HasHeader ?? false;
    public bool SelectedTableBandedRows => ResolveTableEditor()?.BandedRows ?? false;
    public bool SelectedTableShowBorders => ResolveTableEditor()?.ShowBorders ?? false;

    public void TableInsertRowAbove() => ApplyTableCommand(table => table.InsertRowAbove(), "已在上方插入行");
    public void TableInsertRowBelow() => ApplyTableCommand(table => table.InsertRowBelow(), "已在下方插入行");
    public void TableDeleteRow() => ApplyTableCommand(table => table.DeleteActiveRow(), "已删除当前数据行");
    public void TableInsertColumnLeft() => ApplyTableCommand(table => table.InsertColumnLeft(), "已在左侧插入列");
    public void TableInsertColumnRight() => ApplyTableCommand(table => table.InsertColumnRight(), "已在右侧插入列");
    public void TableDeleteColumn() => ApplyTableCommand(table => table.DeleteActiveColumn(), "已删除当前列");
    public void TableNarrowColumn() => ApplyTableCommand(table => table.AdjustActiveColumnWidth(-18), "当前列已缩窄");
    public void TableWidenColumn() => ApplyTableCommand(table => table.AdjustActiveColumnWidth(18), "当前列已加宽");
    public void TableSetHeader(bool enabled) => ApplyTableCommand(table => table.SetHeaderEnabled(enabled), enabled ? "表头名称行已显示" : "表头名称行已隐藏");
    public void TableSetBandedRows(bool enabled) => ApplyTableCommand(table => table.SetBandedRows(enabled), enabled ? "交替行底纹已启用" : "交替行底纹已关闭");
    public void TableSetBorders(bool visible) => ApplyTableCommand(table => table.SetBordersVisible(visible), visible ? "表格边框已显示" : "表格边框已隐藏");
    public void TableSetHeaderColor(Color color) => ApplyTableCommand(table => table.SetHeaderBackground(color), "表头颜色已更新");
    public void TableSetBorderColor(Color color) => ApplyTableCommand(table => table.SetBorderColor(color), "表格边框颜色已更新");
    public void TableSetAlignment(TextAlignment alignment) => ApplyTableCommand(table => table.SetTextAlignment(alignment), $"表格文字已{(alignment == TextAlignment.Left ? "左对齐" : alignment == TextAlignment.Center ? "居中" : "右对齐")}");
    public void TableApplyStyle(TableVisualStyle style) => ApplyTableCommand(table => table.ApplyStyle(style), "表格样式已应用");

    private void ApplyTableCommand(Action<TableEditorControl> action, string message)
    {
        var table = ResolveTableEditor();
        if (table is null)
        {
            StatusChanged?.Invoke(this, "请先单击表格中的任意单元格，再使用表格工具");
            return;
        }
        action(table);
        StatusChanged?.Invoke(this, message);
    }

    private TableEditorControl? ResolveTableEditor()
    {
        if (_selected is not { Model.Type: ContentObjectType.Table }) return null;
        return FindDescendant<TableEditorControl>(_selected);
    }

    public void ZoomIn() => SetZoom(Math.Min(4.0, Zoom + 0.1));
    public void ZoomOut() => SetZoom(Math.Max(0.1, Zoom - 0.1));
    public void ResetZoom() => SetZoom(1.0);
    public void FitToContent()
    {
        var bounds = GetContentBounds();
        var vw = _scroll.ViewportWidth > 10 ? _scroll.ViewportWidth : ActualWidth;
        var vh = _scroll.ViewportHeight > 10 ? _scroll.ViewportHeight : ActualHeight;
        if (vw <= 10 || vh <= 10) return;
        var zoom = Math.Clamp(Math.Min((vw - 60) / Math.Max(300, bounds.Width), (vh - 60) / Math.Max(200, bounds.Height)), 0.1, 2.0);
        SetZoom(zoom); _scroll.ScrollToHorizontalOffset(Math.Max(0, bounds.X * zoom - 20)); _scroll.ScrollToVerticalOffset(Math.Max(0, bounds.Y * zoom - 20));
    }

    public void SetPaperStyle(PaperStyle style)
    {
        _paperStyle = style; _paper.Background = CreatePaperBrush(style); StatusChanged?.Invoke(this, $"纸张样式：{PaperStyleName(style)}");
    }

    public void HighlightObject(string? objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId)) return;
        var control = _objects.Children.OfType<CanvasObjectControl>().FirstOrDefault(x => x.Model.Id == objectId);
        if (control is null) return; SelectObject(control);
        _scroll.ScrollToHorizontalOffset(Math.Max(0, Canvas.GetLeft(control) * Zoom - 100));
        _scroll.ScrollToVerticalOffset(Math.Max(0, Canvas.GetTop(control) * Zoom - 100));
        control.Focus();
    }

    public async Task FlushAsync()
    {
        foreach (var timer in _textTimers.Values) timer.Stop();
        _pendingObjectSaves.Clear();
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>()) await SaveObjectAsync(child);
        await SaveInkAsync();
    }

    public RenderTargetBitmap RenderPageBitmap()
    {
        var wasSelected = _selected; if (wasSelected is not null) wasSelected.IsSelected = false;
        try
        {
            var bounds = GetContentBounds(); if (bounds.Width < 20 || bounds.Height < 20) bounds = new Rect(0, 0, 1200, 900);
            const double maxSide = 3600; var scale = Math.Min(1.0, maxSide / Math.Max(bounds.Width, bounds.Height));
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)); var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new VisualBrush(_surface) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
                dc.PushTransform(new TranslateTransform(-bounds.X * scale, -bounds.Y * scale)); dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRectangle(Brushes.White, null, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)); dc.DrawRectangle(brush, null, new Rect(0, 0, _surface.Width, _surface.Height));
            }
            bitmap.Render(visual); return bitmap;
        }
        finally { if (wasSelected is not null) wasSelected.IsSelected = true; }
    }

    private async Task AddTextAtAsync((double X, double Y) point)
    {
        if (_pageId is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        SetTool(CanvasTool.Select);
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Text, point.X, point.Y, 420, 190, string.Empty);
        var control = AddObjectVisual(model); SelectObject(control); ScrollObjectIntoView(control);
        if (FindDescendant<RichTextBox>(control) is { } editor) { _activeRichText = editor; editor.Focus(); Keyboard.Focus(editor); }
        DirtyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, "文本块已创建 · 直接输入即可自动保存");
    }

    private CanvasObjectControl AddObjectVisual(ContentObject item)
    {
        UIElement content = item.Type switch
        {
            ContentObjectType.Text => CreateRichTextEditor(item),
            ContentObjectType.Image => CreateImage(item),
            ContentObjectType.Attachment => CreateAttachment(item),
            ContentObjectType.Table => CreateTableEditor(item),
            ContentObjectType.Shape => CreateShape(item),
            _ => new TextBlock { Text = "不支持的对象", Margin = new Thickness(8) }
        };
        var control = new CanvasObjectControl(item, content) { SnapToGrid = true, IsHitTestVisible = _currentTool == CanvasTool.Select };
        control.Changed += async (_, _) => { await SaveObjectAsync(control); ExpandSurfaceToContent(); DirtyChanged?.Invoke(this, EventArgs.Empty); };
        control.TagsChanged += async (_, _) => { await SaveObjectAsync(control); DirtyChanged?.Invoke(this, EventArgs.Empty); };
        control.Activated += (_, _) => { SelectObject(control); _activeRichText = item.Type == ContentObjectType.Text ? FindDescendant<RichTextBox>(control) : null; };
        control.ContextMenu = BuildContextMenu(control);
        Canvas.SetLeft(control, item.X); Canvas.SetTop(control, item.Y); Panel.SetZIndex(control, item.ZIndex); _objects.Children.Add(control);
        return control;
    }

    private RichTextBox CreateRichTextEditor(ContentObject item)
    {
        var editor = new RichTextBox
        {
            Document = LoadDocument(item.Payload), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 16, Padding = new Thickness(8, 5, 8, 8), AcceptsTab = true,
            IsUndoEnabled = true, IsDocumentEnabled = true
        };
        SpellCheck.SetIsEnabled(editor, false);
        editor.GotKeyboardFocus += (_, _) => _activeRichText = editor;
        editor.PreviewKeyDown += (_, e) => RichText_PreviewKeyDown(editor, item, e);
        HookInteractiveDocumentElements(editor, item);
        editor.TextChanged += (_, _) =>
        {
            item.Payload = XamlWriter.Save(editor.Document); item.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
            QueueObjectSave(item.Id, async () => { if (_contentRepository is not null) await _contentRepository.UpsertAsync(item); DirtyChanged?.Invoke(this, EventArgs.Empty); });
        };
        return editor;
    }

    private TableEditorControl CreateTableEditor(ContentObject item)
    {
        var table = new TableEditorControl(item.Payload);
        table.ContentChanged += (_, _) =>
        {
            item.Payload = table.Payload; item.SearchText = table.SearchText;
            QueueObjectSave(item.Id, async () => { if (_contentRepository is not null) await _contentRepository.UpsertAsync(item); DirtyChanged?.Invoke(this, EventArgs.Empty); });
        };
        return table;
    }

    private UIElement CreateShape(ContentObject item)
    {
        ShapePayload payload;
        try { payload = JsonSerializer.Deserialize<ShapePayload>(item.Payload) ?? new ShapePayload("Rectangle", "#6C2AA5", "#186C2AA5", 2); }
        catch { payload = new ShapePayload("Rectangle", "#6C2AA5", "#186C2AA5", 2); }
        var kind = Enum.TryParse<ShapeKind>(payload.Kind, true, out var parsed) ? parsed : ShapeKind.Rectangle;
        var stroke = ParseColor(payload.Stroke, Color.FromRgb(108, 42, 165));
        var fill = ParseColor(payload.Fill, Color.FromArgb(24, 108, 42, 165));
        return new ShapePresenter
        {
            Kind = kind,
            StrokeColor = stroke,
            FillColor = fill,
            StrokeThickness = Math.Clamp(payload.Thickness, 0.5, 16),
            Margin = new Thickness(4),
            IsHitTestVisible = false
        };
    }

    private UIElement CreateImage(ContentObject item)
    {
        var payload = ParsePayload(item.Payload); var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
        if (_mediaStore is not null && payload is not null)
        {
            var path = _mediaStore.GetAbsolutePath(payload.RelativePath);
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); image.Source = bitmap;
            }
        }
        return image;
    }

    private UIElement CreateAttachment(ContentObject item)
    {
        var payload = ParsePayload(item.Payload);
        var panel = new Grid { Margin = new Thickness(8) }; panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(237, 235, 255)), Child = new TextBlock { Text = "📎", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        panel.Children.Add(icon);
        var button = new Button
        {
            Content = payload is null ? "附件" : payload.FileName, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(8, 0, 8, 0), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand
        };
        button.Click += (_, _) =>
        {
            if (_mediaStore is null || payload is null) return; var path = _mediaStore.GetAbsolutePath(payload.RelativePath);
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        };
        Grid.SetColumn(button, 1); panel.Children.Add(button); return panel;
    }

    private ContextMenu BuildContextMenu(CanvasObjectControl control)
    {
        var menu = new ContextMenu();
        var todo = new MenuItem { Header = control.Model.IsTodo ? "移除待办标记" : "标记为待办" };
        todo.Click += async (_, _) => { SelectObject(control); await ToggleTodoSelectedAsync(); todo.Header = control.Model.IsTodo ? "移除待办标记" : "标记为待办"; };
        var important = new MenuItem { Header = control.Model.IsImportant ? "移除重要标记" : "标记为重要" };
        important.Click += async (_, _) => { SelectObject(control); await ToggleImportantSelectedAsync(); important.Header = control.Model.IsImportant ? "移除重要标记" : "标记为重要"; };
        var front = new MenuItem { Header = "置于顶层" }; front.Click += async (_, _) => { control.Model.ZIndex = NextTopZ(); Panel.SetZIndex(control, control.Model.ZIndex); await SaveObjectAsync(control); };
        var back = new MenuItem { Header = "置于底层" }; back.Click += async (_, _) => { control.Model.ZIndex = NextBottomZ(); Panel.SetZIndex(control, control.Model.ZIndex); await SaveObjectAsync(control); };
        var duplicate = new MenuItem { Header = "创建副本    Ctrl+D" }; duplicate.Click += async (_, _) => { SelectObject(control); await DuplicateSelectedAsync(); };
        var delete = new MenuItem { Header = "删除对象    Delete" }; delete.Click += async (_, _) => { SelectObject(control); await DeleteSelectedAsync(); };
        menu.Items.Add(todo); menu.Items.Add(important); menu.Items.Add(new Separator()); menu.Items.Add(front); menu.Items.Add(back); menu.Items.Add(new Separator()); menu.Items.Add(duplicate); menu.Items.Add(delete); return menu;
    }

    private async Task SaveObjectAsync(CanvasObjectControl control)
    {
        if (_contentRepository is null) return;
        control.Model.X = Canvas.GetLeft(control); control.Model.Y = Canvas.GetTop(control); control.Model.Width = control.Width; control.Model.Height = control.Height;
        if (control.Model.Type == ContentObjectType.Text && FindDescendant<RichTextBox>(control) is { } editor)
        {
            control.Model.Payload = XamlWriter.Save(editor.Document); control.Model.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
        }
        if (control.Model.Type == ContentObjectType.Table && FindDescendant<TableEditorControl>(control) is { } table)
        {
            control.Model.Payload = table.Payload; control.Model.SearchText = table.SearchText;
        }
        await _contentRepository.UpsertAsync(control.Model);
    }

    private void QueueObjectSave(string id, Func<Task> save)
    {
        _pendingObjectSaves[id] = save;
        if (!_textTimers.TryGetValue(id, out var timer))
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (_pendingObjectSaves.Remove(id, out var action)) await action();
            };
            _textTimers[id] = timer;
        }
        timer.Stop(); timer.Start();
    }

    private void QueueInkSave() { _inkSaveTimer.Stop(); _inkSaveTimer.Start(); }
    private async Task SaveInkAsync()
    {
        if (_pageId is null || _inkRepository is null) return;
        using var stream = new MemoryStream(); _ink.Strokes.Save(stream); await _inkRepository.SaveAsync(_pageId, stream.ToArray()); DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SelectObject(CanvasObjectControl? control)
    {
        if (_selected is not null) _selected.IsSelected = false;
        _selected = control; _activeRichText = null;
        if (_selected is not null)
        {
            _selected.IsSelected = true;
            if (_selected.Model.Type == ContentObjectType.Text) _activeRichText = FindDescendant<RichTextBox>(_selected);
        }
        SelectionChanged?.Invoke(_selected?.Model);
    }

    private void Objects_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool == CanvasTool.Shape)
        {
            if (_pageId is null || _contentRepository is null) return;
            var point = e.GetPosition(_objects);
            _shapeStart = point;
            _shapePreview = new ShapePresenter
            {
                Kind = _shapeKind,
                StrokeColor = _shapeStrokeColor,
                FillColor = _shapeFillColor,
                StrokeThickness = _shapeThickness,
                Width = 1,
                Height = 1,
                IsHitTestVisible = false,
                Opacity = 0.82
            };
            Canvas.SetLeft(_shapePreview, point.X);
            Canvas.SetTop(_shapePreview, point.Y);
            Panel.SetZIndex(_shapePreview, int.MaxValue);
            _objects.Children.Add(_shapePreview);
            _objects.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_currentTool != CanvasTool.Select || e.OriginalSource != _objects) return;
        var selectPoint = e.GetPosition(_objects);
        if (e.ClickCount >= 2) { _ = AddTextAtAsync((selectPoint.X, selectPoint.Y)); e.Handled = true; return; }
        SelectObject(null);
    }

    private void Objects_MouseMove(object sender, MouseEventArgs e)
    {
        if (_currentTool != CanvasTool.Shape || _shapeStart is null || _shapePreview is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(_objects);
        var x = Math.Min(_shapeStart.Value.X, now.X);
        var y = Math.Min(_shapeStart.Value.Y, now.Y);
        var w = Math.Max(8, Math.Abs(now.X - _shapeStart.Value.X));
        var h = Math.Max(8, Math.Abs(now.Y - _shapeStart.Value.Y));
        Canvas.SetLeft(_shapePreview, x);
        Canvas.SetTop(_shapePreview, y);
        _shapePreview.Width = w;
        _shapePreview.Height = h;
        _shapePreview.InvalidateVisual();
    }

    private async void Objects_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool != CanvasTool.Shape || _shapeStart is null || _shapePreview is null || _pageId is null || _contentRepository is null) return;
        var now = e.GetPosition(_objects);
        var start = _shapeStart.Value;
        _shapeStart = null;
        _objects.ReleaseMouseCapture();
        var preview = _shapePreview;
        _shapePreview = null;
        _objects.Children.Remove(preview);
        var x = Math.Min(start.X, now.X);
        var y = Math.Min(start.Y, now.Y);
        var w = Math.Max(36, Math.Abs(now.X - start.X));
        var h = Math.Max(28, Math.Abs(now.Y - start.Y));
        if (_shapeKind is ShapeKind.Line or ShapeKind.Arrow) h = Math.Max(34, h);
        var payload = JsonSerializer.Serialize(new ShapePayload(_shapeKind.ToString(), ToHex(_shapeStrokeColor), ToHex(_shapeFillColor), _shapeThickness));
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Shape, x, y, w, h, payload);
        var visual = AddObjectVisual(model);
        SelectObject(visual);
        ExpandSurfaceToContent();
        DirtyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"已绘制{ShapeKindName(_shapeKind)} · 可继续绘制，按 Esc 返回选择模式");
        e.Handled = true;
    }

    private async void Objects_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var point = e.GetPosition(_objects); var offset = 0d;
        foreach (var file in files.Take(20))
        {
            var p = new Point(point.X + offset, point.Y + offset); offset += 18;
            if (IsImage(file)) await AddImageAsync(file, p); else await AddAttachmentAsync(file, p);
        }
        e.Handled = true;
    }
    private void Objects_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }

    private void ApplyRichCommand(Action<RichTextBox> action, string successMessage)
    {
        var editor = ResolveRichTextEditor();
        if (editor is null) { StatusChanged?.Invoke(this, "请先选择文本块，并将光标放入文本后再使用格式工具"); return; }
        _activeRichText = editor;
        action(editor);
        editor.Focus();
        if (_selected is { Model.Type: ContentObjectType.Text } control)
            QueueObjectSave(control.Model.Id, async () => await SaveObjectAsync(control));
        StatusChanged?.Invoke(this, successMessage);
    }

    private RichTextBox? ResolveRichTextEditor()
    {
        if (_activeRichText is not null && _activeRichText.IsVisible) return _activeRichText;
        if (_selected is { Model.Type: ContentObjectType.Text }) return FindDescendant<RichTextBox>(_selected);
        return null;
    }

    private void ApplyInkAttributes(bool highlighter, bool pencil)
    {
        var baseSize = pencil ? Math.Max(0.6, _inkSize * 0.78) : _inkSize;
        var color = pencil ? Color.FromArgb(185, _inkColor.R, _inkColor.G, _inkColor.B) : _inkColor;
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = color,
            Width = highlighter ? Math.Max(10, _inkSize * 4.2) : baseSize,
            Height = highlighter ? Math.Max(10, _inkSize * 4.2) : baseSize,
            FitToCurve = true,
            IgnorePressure = false,
            IsHighlighter = highlighter,
            StylusTip = pencil ? StylusTip.Rectangle : StylusTip.Ellipse
        };
    }

    private void RichText_PreviewKeyDown(RichTextBox editor, ContentObject item, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && editor.CaretPosition.Paragraph is { } paragraph && paragraph.Parent is FlowDocument && FindTodoContainer(paragraph) is not null)
        {
            e.Handled = true;
            var next = new Paragraph { Margin = paragraph.Margin, FontSize = paragraph.FontSize, FontWeight = paragraph.FontWeight };
            next.Inlines.Add(CreateTodoInline(editor, item));
            var run = new Run(string.Empty);
            next.Inlines.Add(run);
            editor.Document.Blocks.InsertAfter(paragraph, next);
            editor.CaretPosition = run.ContentStart;
            editor.Focus();
            QueueObjectSave(item.Id, async () =>
            {
                item.Payload = XamlWriter.Save(editor.Document);
                item.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
                if (_contentRepository is not null) await _contentRepository.UpsertAsync(item);
                DirtyChanged?.Invoke(this, EventArgs.Empty);
            });
            StatusChanged?.Invoke(this, "已自动创建下一条待办");
        }
    }

    private InlineUIContainer CreateTodoInline(RichTextBox editor, ContentObject? item = null)
    {
        var box = new CheckBox
        {
            Tag = "LocalNoteTodo",
            Width = 17,
            Height = 17,
            Margin = new Thickness(1, 0, 6, -2),
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = false,
            IsHitTestVisible = true,
            ToolTip = "点击切换待办完成状态"
        };
        AttachTodoCheckBoxBehavior(box, editor, item);
return new InlineUIContainer(box) { BaselineAlignment = BaselineAlignment.Center };
    }

    private void AttachTodoCheckBoxBehavior(CheckBox box, RichTextBox editor, ContentObject? item)
    {
        // RichTextBox is an editable host and may consume mouse input before an embedded
        // CheckBox receives its normal Click. Handle the preview event even when already
        // marked handled, toggle explicitly once, then persist immediately.
        box.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            box.IsChecked = box.IsChecked != true;
            e.Handled = true;
            PersistTodoCheckState(editor, item, box.IsChecked == true);
        }), true);
    }

    private void PersistTodoCheckState(RichTextBox editor, ContentObject? item, bool completed)
    {
        if (item is not null)
        {
            item.Payload = XamlWriter.Save(editor.Document);
            item.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
            QueueObjectSave(item.Id, async () =>
            {
                if (_contentRepository is not null) await _contentRepository.UpsertAsync(item);
                DirtyChanged?.Invoke(this, EventArgs.Empty);
            });
        }
        else
        {
            QueueSelectedTextSave();
        }
        StatusChanged?.Invoke(this, completed ? "待办已完成" : "待办已恢复未完成");
    }

    private static InlineUIContainer? FindTodoContainer(Paragraph paragraph)
    {
        foreach (var inline in paragraph.Inlines)
            if (inline is InlineUIContainer container && container.Child is CheckBox box && Equals(box.Tag?.ToString(), "LocalNoteTodo")) return container;
        return null;
    }

    private void HookInteractiveDocumentElements(RichTextBox editor, ContentObject item)
    {
        foreach (var paragraph in editor.Document.Blocks.OfType<Paragraph>())
        {
            foreach (var inline in paragraph.Inlines.ToList())
            {
                if (inline is InlineUIContainer container && container.Child is CheckBox box && Equals(box.Tag?.ToString(), "LocalNoteTodo"))
                {
                    box.ToolTip = "点击切换待办完成状态";
                    box.IsHitTestVisible = true;
                    AttachTodoCheckBoxBehavior(box, editor, item);
                }
                else if (inline is Hyperlink link)
                {
                    link.RequestNavigate += (_, e) =>
                    {
                        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                    };
                }
            }
        }
    }

    private void QueueSelectedTextSave()
    {
        if (_selected is not { Model.Type: ContentObjectType.Text } control || FindDescendant<RichTextBox>(control) is not { } editor) return;
        control.Model.Payload = XamlWriter.Save(editor.Document);
        control.Model.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
        QueueObjectSave(control.Model.Id, async () => { if (_contentRepository is not null) await _contentRepository.UpsertAsync(control.Model); DirtyChanged?.Invoke(this, EventArgs.Empty); });
    }

    private static string ToHex(Color color) => color.A == 255
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ParseColor(string? value, Color fallback)
    {
        try { return value is null ? fallback : (Color)ColorConverter.ConvertFromString(value)!; }
        catch { return fallback; }
    }

    private static string ShapeKindName(ShapeKind kind) => kind switch
    {
        ShapeKind.Line => "直线",
        ShapeKind.Arrow => "箭头",
        ShapeKind.Rectangle => "矩形",
        ShapeKind.RoundedRectangle => "圆角矩形",
        ShapeKind.Ellipse => "椭圆",
        ShapeKind.Triangle => "三角形",
        ShapeKind.Diamond => "菱形",
        ShapeKind.Flowchart => "流程框",
        _ => "形状"
    };

    private void ScrollObjectIntoView(CanvasObjectControl control)
    {
        var x = Canvas.GetLeft(control); var y = Canvas.GetTop(control);
        _scroll.ScrollToHorizontalOffset(Math.Max(0, x * Zoom - 80));
        _scroll.ScrollToVerticalOffset(Math.Max(0, y * Zoom - 70));
    }

    private (double X, double Y) GetInsertionPoint() => (_scroll.HorizontalOffset / Zoom + 120, _scroll.VerticalOffset / Zoom + 100);
    private int NextTopZ() => _objects.Children.OfType<CanvasObjectControl>().Select(x => x.Model.ZIndex).DefaultIfEmpty(0).Max() + 1;
    private int NextBottomZ() => _objects.Children.OfType<CanvasObjectControl>().Select(x => x.Model.ZIndex).DefaultIfEmpty(0).Min() - 1;

    private void ExpandSurfaceToContent()
    {
        var maxX = 1600d; var maxY = 1000d;
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>())
        {
            maxX = Math.Max(maxX, Canvas.GetLeft(child) + child.Width + 900); maxY = Math.Max(maxY, Canvas.GetTop(child) + child.Height + 900);
        }
        var width = Math.Max(6000, maxX); var height = Math.Max(4000, maxY);
        _objects.Width = _ink.Width = _surface.Width = _paper.Width = width; _objects.Height = _ink.Height = _surface.Height = _paper.Height = height;
    }

    private Rect GetContentBounds()
    {
        Rect? result = null;
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>())
        {
            var rect = new Rect(Canvas.GetLeft(child), Canvas.GetTop(child), child.ActualWidth > 0 ? child.ActualWidth : child.Width, child.ActualHeight > 0 ? child.ActualHeight : child.Height);
            result = result is null ? rect : Rect.Union(result.Value, rect);
        }
        if (_ink.Strokes.Count > 0) result = result is null ? _ink.Strokes.GetBounds() : Rect.Union(result.Value, _ink.Strokes.GetBounds());
        var bounds = result ?? new Rect(0, 0, 1200, 900); bounds.Inflate(80, 80); bounds.X = Math.Max(0, bounds.X); bounds.Y = Math.Max(0, bounds.Y); return bounds;
    }

    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetZoom(Math.Clamp(Zoom + (e.Delta > 0 ? 0.1 : -0.1), 0.1, 4.0)); e.Handled = true;
    }
    private void SetZoom(double zoom)
    {
        _scale.ScaleX = _scale.ScaleY = zoom;
        ZoomChanged?.Invoke(zoom);
        StatusChanged?.Invoke(this, $"缩放 {zoom:P0}");
    }
    private void Scroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panStart = e.GetPosition(_scroll); _startH = _scroll.HorizontalOffset; _startV = _scroll.VerticalOffset; _scroll.CaptureMouse(); _scroll.Cursor = Cursors.Hand; e.Handled = true;
    }
    private void Scroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is null || e.MiddleButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(_scroll); var delta = now - _panStart.Value; _scroll.ScrollToHorizontalOffset(_startH - delta.X); _scroll.ScrollToVerticalOffset(_startV - delta.Y);
    }

    private static FlowDocument LoadDocument(string payload)
    {
        if (!string.IsNullOrWhiteSpace(payload) && payload.TrimStart().StartsWith("<FlowDocument", StringComparison.Ordinal))
        {
            try { if (XamlReader.Parse(payload) is FlowDocument doc) return doc; } catch { }
        }
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 16 };
        document.Blocks.Add(new Paragraph(new Run(payload ?? string.Empty)) { Margin = new Thickness(0) }); return document;
    }

    private static Brush CreatePaperBrush(PaperStyle style)
    {
        if (style == PaperStyle.Blank) return Brushes.White;
        var group = new DrawingGroup();
        if (style == PaperStyle.Grid)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(229, 232, 239)), 0.7);
            group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 24), new Point(24, 24))));
            group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(24, 0), new Point(24, 24))));
        }
        else if (style == PaperStyle.Lines)
        {
            group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromRgb(225, 230, 239)), 0.8), new LineGeometry(new Point(0, 28), new Point(28, 28))));
        }
        else
        {
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(205, 211, 223)), null, new EllipseGeometry(new Point(12, 12), 1.1, 1.1)));
        }
        var tile = style == PaperStyle.Lines ? new Size(28, 28) : new Size(24, 24);
        return new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, tile.Width, tile.Height), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
    }

    private static string PaperStyleName(PaperStyle style) => style switch { PaperStyle.Grid => "网格", PaperStyle.Lines => "横线", PaperStyle.Dots => "点阵", _ => "空白" };
    private static bool IsImage(string path) => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T found) return found; if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static MediaPayload? ParsePayload(string json) { try { return JsonSerializer.Deserialize<MediaPayload>(json); } catch { return null; } }
    private sealed record ShapePayload(string Kind, string Stroke, string Fill, double Thickness);
    private sealed record MediaPayload(string FileName, string RelativePath);
}
