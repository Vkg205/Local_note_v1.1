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
    private readonly Canvas _shapeLayer;
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
    private DrawingShapeControl? _selectedShape;
    private RichTextBox? _activeRichText;
    private Point? _panStart;
    private double _startH;
    private double _startV;
    private MouseButton? _panButton;
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
    private CanvasObjectControl? _shapeParent;
    private CanvasStateSnapshot? _pendingManipulationSnapshot;
    private CanvasStateSnapshot? _pendingInkSnapshot;
    private string? _pendingInkHistoryDescription;
    private readonly Dictionary<string, CanvasStateSnapshot> _textEditStarts = new();
    private readonly Stack<UndoUnit> _undoStack = new();
    private readonly Stack<UndoUnit> _redoStack = new();
    private bool _restoringHistory;

    public PageCanvasView()
    {
        _paper = new Border { Width = 6000, Height = 4000, Background = Brushes.White };
        _objects = new Canvas { Width = 6000, Height = 4000, Background = Brushes.Transparent, AllowDrop = true };
        _shapeLayer = new Canvas { Width = 6000, Height = 4000, Background = null, IsHitTestVisible = true };
        _ink = new InkCanvas { Width = 6000, Height = 4000, Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.None, IsHitTestVisible = false };
        ApplyInkAttributes(false, false);

        _surface = new Grid { Width = 6000, Height = 4000, RenderTransformOrigin = new Point(0, 0), LayoutTransform = _scale };
        _surface.Children.Add(_paper); _surface.Children.Add(_objects); _surface.Children.Add(_shapeLayer); _surface.Children.Add(_ink);
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
        _scroll.PreviewMouseUp += Scroll_PreviewMouseUp;
        _objects.MouseLeftButtonDown += Objects_MouseLeftButtonDown;
        _objects.MouseMove += Objects_MouseMove;
        _objects.MouseLeftButtonUp += Objects_MouseLeftButtonUp;
        _objects.Drop += Objects_Drop; _objects.DragOver += Objects_DragOver;

        _inkSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _inkSaveTimer.Tick += (_, _) =>
        {
            _inkSaveTimer.Stop();
            RunSafe(SaveInkAsync(), "保存墨迹");
        };
        _ink.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (!_restoringHistory && _currentTool is CanvasTool.Pen or CanvasTool.Pencil or CanvasTool.Highlighter or CanvasTool.Eraser or CanvasTool.InkSelect)
            {
                _pendingInkSnapshot = CaptureSnapshot();
                _pendingInkHistoryDescription = null;
            }
        };
        _ink.StrokeCollected += (_, _) => MarkInkChanged("书写墨迹");
        _ink.StrokeErased += (_, _) => MarkInkChanged("擦除墨迹");
        _ink.SelectionMoved += (_, _) => MarkInkChanged("移动墨迹");
        _ink.SelectionResized += (_, _) => MarkInkChanged("调整墨迹");
        _ink.PreviewMouseLeftButtonUp += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CommitPendingInkHistory));
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler? DirtyChanged;
    public event Action<CanvasTool>? ToolChanged;
    public event Action<ContentObject?>? SelectionChanged;
    public event Action<double>? ZoomChanged;
    public double Zoom => _scale.ScaleX;
    public ContentObject? SelectedObject => _selectedShape?.Model ?? _selected?.Model;

    public async Task BindAsync(string? pageId, ContentObjectRepository? contentRepository, InkRepository? inkRepository, MediaStoreService? mediaStore)
    {
        await FlushAsync();
        _pageId = pageId; _contentRepository = contentRepository; _inkRepository = inkRepository; _mediaStore = mediaStore;
        _objects.Children.Clear(); _shapeLayer.Children.Clear(); _ink.Strokes.Clear(); _selected = null; _selectedShape = null; _activeRichText = null;
        _undoStack.Clear(); _redoStack.Clear(); _pendingManipulationSnapshot = null; _pendingInkSnapshot = null; _pendingInkHistoryDescription = null; _textEditStarts.Clear();
        SelectionChanged?.Invoke(null);
        SetTool(CanvasTool.Select);
        foreach (var timer in _textTimers.Values) timer.Stop(); _textTimers.Clear(); _pendingObjectSaves.Clear();
        if (pageId is null || contentRepository is null || inkRepository is null)
        {
            StatusChanged?.Invoke(this, "请选择或新建页面"); return;
        }
        var pageObjects = await contentRepository.GetByPageAsync(pageId);
        foreach (var item in pageObjects.Where(x => x.Type != ContentObjectType.Shape)) AddObjectVisual(item);
        foreach (var item in pageObjects.Where(x => x.Type == ContentObjectType.Shape)) AddShapeVisual(item);
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
            _shapeLayer.Children.Remove(_shapePreview);
            _shapePreview = null;
            _shapeStart = null;
            _shapeParent = null;
            _pendingManipulationSnapshot = null;
            if (_objects.IsMouseCaptured) _objects.ReleaseMouseCapture();
        }
        _currentTool = tool;
        var inkMode = tool is CanvasTool.Pen or CanvasTool.Pencil or CanvasTool.Highlighter or CanvasTool.Eraser or CanvasTool.InkSelect;
        var shapeMode = tool == CanvasTool.Shape;
        _ink.IsHitTestVisible = inkMode;
        _shapeLayer.IsHitTestVisible = tool == CanvasTool.Select;
        _objects.IsHitTestVisible = !inkMode;
        _objects.Cursor = shapeMode ? Cursors.Cross : Cursors.Arrow;
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>())
        {
            child.IsHitTestVisible = tool == CanvasTool.Select;
            child.OverlayLayer.IsHitTestVisible = tool == CanvasTool.Select;
        }
        foreach (var child in _shapeLayer.Children.OfType<DrawingShapeControl>()) child.IsHitTestVisible = tool == CanvasTool.Select;
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
        if (_selectedShape is not null)
        {
            var before = CaptureSnapshot();
            var current = ParseShapePayload(_selectedShape.Model.Payload);
            var payload = current with { Kind = kind.ToString(), Stroke = ToHex(_shapeStrokeColor), Fill = ToHex(_shapeFillColor), Thickness = _shapeThickness };
            _selectedShape.Model.Payload = JsonSerializer.Serialize(payload);
            _selectedShape.UpdateStyle(kind, _shapeStrokeColor, _shapeFillColor, _shapeThickness);
            RunSafe(SaveShapeAsync(_selectedShape), "保存形状");
            RegisterHistory("更改形状类型", before);
            StatusChanged?.Invoke(this, $"当前形状已改为{ShapeKindName(kind)}");
        }
        else if (_currentTool == CanvasTool.Shape)
            StatusChanged?.Invoke(this, $"形状工具 · 拖动绘制{ShapeKindName(kind)}");
        else
            StatusChanged?.Invoke(this, $"形状类型已设为{ShapeKindName(kind)} · 点击“形状”工具后开始绘制");
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
        if (_selectedShape is null) return;
        var before = CaptureSnapshot();
        var current = ParseShapePayload(_selectedShape.Model.Payload);
        var payload = current with { Kind = _shapeKind.ToString(), Stroke = ToHex(_shapeStrokeColor), Fill = ToHex(_shapeFillColor), Thickness = _shapeThickness };
        _selectedShape.Model.Payload = JsonSerializer.Serialize(payload);
        _selectedShape.UpdateStyle(_shapeKind, _shapeStrokeColor, _shapeFillColor, _shapeThickness);
        RunSafe(SaveShapeAsync(_selectedShape), "保存形状");
        RegisterHistory("修改形状样式", before);
    }

    public void ClearInk()
    {
        if (_pageId is null) { StatusChanged?.Invoke(this, "请先选择页面"); return; }
        if (_ink.Strokes.Count == 0) { StatusChanged?.Invoke(this, "当前页没有墨迹可清除"); return; }
        var before = CaptureSnapshot();
        _ink.Strokes.Clear(); QueueInkSave(); RegisterHistory("清除本页墨迹", before);
        StatusChanged?.Invoke(this, "本页墨迹已清除 · 可撤销");
    }

    public async Task AddTextAsync() => await AddTextAtAsync(GetInsertionPoint());
    public async Task AddTableAsync()
    {
        if (_pageId is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        var before = CaptureSnapshot();
        SetTool(CanvasTool.Select);
        var data = TableEditorControl.CreateDefaultData(3, 3);
        var (x, y) = GetInsertionPoint();
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Table, x, y, 520, 260, JsonSerializer.Serialize(data));
        var visual = AddObjectVisual(model); SelectObject(visual); ScrollObjectIntoView(visual); DirtyChanged?.Invoke(this, EventArgs.Empty);
        RegisterHistory("插入表格", before);
        StatusChanged?.Invoke(this, "已插入表格 · 已预留表头名称行；选中表格后可在“表格”工具中编辑格式");
    }

    public async Task AddImageAsync(string sourcePath, Point? point = null)
    {
        if (_pageId is null || _contentRepository is null || _mediaStore is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        var before = CaptureSnapshot();
        SetTool(CanvasTool.Select);
        var relative = await _mediaStore.ImportAsync(sourcePath, "images");
        var payload = JsonSerializer.Serialize(new MediaPayload(Path.GetFileName(sourcePath), relative));
        var pos = point is null ? GetInsertionPoint() : (point.Value.X, point.Value.Y);
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Image, pos.Item1, pos.Item2, 420, 300, payload);
        var visual = AddObjectVisual(model); SelectObject(visual); ExpandSurfaceToContent(); ScrollObjectIntoView(visual); DirtyChanged?.Invoke(this, EventArgs.Empty);
        RegisterHistory("插入图片", before);
        StatusChanged?.Invoke(this, $"已插入图片：{Path.GetFileName(sourcePath)}");
    }

    public async Task AddAttachmentAsync(string sourcePath, Point? point = null)
    {
        if (_pageId is null || _contentRepository is null || _mediaStore is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        var before = CaptureSnapshot();
        SetTool(CanvasTool.Select);
        var relative = await _mediaStore.ImportAsync(sourcePath, "attachments");
        var payload = JsonSerializer.Serialize(new MediaPayload(Path.GetFileName(sourcePath), relative));
        var pos = point is null ? GetInsertionPoint() : (point.Value.X, point.Value.Y);
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Attachment, pos.Item1, pos.Item2, 360, 100, payload, Path.GetFileName(sourcePath));
        var visual = AddObjectVisual(model); SelectObject(visual); ScrollObjectIntoView(visual); DirtyChanged?.Invoke(this, EventArgs.Empty);
        RegisterHistory("插入附件", before);
        StatusChanged?.Invoke(this, $"已插入附件：{Path.GetFileName(sourcePath)}");
    }

    public async Task DeleteSelectedAsync()
    {
        if (_contentRepository is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象"); return; }
        var model = _selectedShape?.Model ?? _selected?.Model;
        if (model is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象"); return; }
        var before = CaptureSnapshot();
        if (_selected is not null)
        {
            foreach (var childShape in _selected.OverlayLayer.Children.OfType<DrawingShapeControl>().ToList())
                await _contentRepository.DeleteAsync(childShape.Model.Id);
        }
        await _contentRepository.DeleteAsync(model.Id);
        if (_selectedShape is not null)
        {
            if (_selectedShape.Parent is Panel shapeParent) shapeParent.Children.Remove(_selectedShape);
            _selectedShape = null;
        }
        if (_selected is not null)
        {
            _objects.Children.Remove(_selected); _selected = null; _activeRichText = null;
        }
        SelectionChanged?.Invoke(null);
        RegisterHistory("删除对象", before);
        DirtyChanged?.Invoke(this, EventArgs.Empty); StatusChanged?.Invoke(this, "对象已删除 · 可撤销");
    }

    public async Task DuplicateSelectedAsync()
    {
        if (_contentRepository is null || _pageId is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象再创建副本"); return; }
        var source = _selectedShape?.Model ?? _selected?.Model;
        if (source is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象再创建副本"); return; }
        var before = CaptureSnapshot();
        if (source.Type == ContentObjectType.Text && _selected is not null && FindDescendant<RichTextBox>(_selected) is { } rich)
        {
            source.Payload = XamlWriter.Save(rich.Document);
            source.SearchText = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Trim();
        }
        if (source.Type == ContentObjectType.Table && _selected is not null && FindDescendant<TableEditorControl>(_selected) is { } table)
        {
            source.Payload = table.Payload; source.SearchText = table.SearchText;
        }
        var copy = await _contentRepository.CreateAsync(_pageId, source.Type, source.X + 24, source.Y + 24, source.Width, source.Height, source.Payload, source.SearchText);
        copy.StyleJson = source.StyleJson; copy.IsTodo = false; copy.TodoCompleted = false; copy.IsImportant = source.IsImportant;
        await _contentRepository.UpsertAsync(copy);
        if (copy.Type == ContentObjectType.Shape)
        {
            SelectShape(AddShapeVisual(copy));
        }
        else
        {
            var copiedControl = AddObjectVisual(copy);
            if (_selected is not null)
            {
                foreach (var childShape in _selected.OverlayLayer.Children.OfType<DrawingShapeControl>())
                {
                    var childCopy = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Shape, childShape.Model.X, childShape.Model.Y, childShape.Model.Width, childShape.Model.Height, childShape.Model.Payload);
                    childCopy.StyleJson = JsonSerializer.Serialize(new ShapeLayerMetadata(copy.Id));
                    childCopy.ZIndex = childShape.Model.ZIndex; await _contentRepository.UpsertAsync(childCopy); AddShapeVisual(childCopy);
                }
            }
            SelectObject(copiedControl);
        }
        ExpandSurfaceToContent(); RegisterHistory("创建对象副本", before); DirtyChanged?.Invoke(this, EventArgs.Empty); StatusChanged?.Invoke(this, "对象副本已创建");
    }

    public async Task ToggleImportantSelectedAsync()
    {
        if (_selected is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先选择一个内容对象，再添加重要标记"); return; }
        var before = CaptureSnapshot();
        _selected.Model.IsImportant = !_selected.Model.IsImportant; _selected.RefreshTagVisuals(); await SaveObjectAsync(_selected);
        RegisterHistory("切换重要标记", before);
        StatusChanged?.Invoke(this, _selected.Model.IsImportant ? "已标记为重要" : "已移除重要标记");
    }

    public void ToggleParagraphTodo()
    {
        var editor = ResolveRichTextEditor();
        if (editor is null) { StatusChanged?.Invoke(this, "请先把光标放到文本块的某一行，再设置待办项"); return; }
        var paragraph = editor.CaretPosition.Paragraph;
        if (paragraph is null) { StatusChanged?.Invoke(this, "当前光标位置不是可设置待办的段落"); return; }
        var before = CaptureSnapshot();
        var existing = FindTodoContainer(paragraph);
        if (existing is not null)
        {
            // A completed todo applies a completion decoration to the paragraph. Remove
            // that visual state together with the checkbox so ordinary text is not left
            // struck through after the todo marker is removed.
            ApplyTodoCompletionVisual(paragraph, false);
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
        RegisterHistory(existing is null ? "设置段落待办" : "移除段落待办", before);
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
        hyperlink.RequestNavigate += (_, e) => TryOpenUri(e.Uri);
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
        var before = CaptureSnapshot();
        action(table);
        RegisterHistory(message, before);
        StatusChanged?.Invoke(this, message + " · 可撤销");
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

    public void SetPaperStyle(PaperStyle style, bool notify = true)
    {
        _paperStyle = style;
        _paper.Background = CreatePaperBrush(style);
        if (notify) StatusChanged?.Invoke(this, $"纸张样式：{PaperStyleName(style)}");
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
        foreach (var shape in _shapeLayer.Children.OfType<DrawingShapeControl>()) await SaveShapeAsync(shape);
        foreach (var parent in _objects.Children.OfType<CanvasObjectControl>())
            foreach (var shape in parent.OverlayLayer.Children.OfType<DrawingShapeControl>()) await SaveShapeAsync(shape);
        await SaveInkAsync();
    }

    public RenderTargetBitmap RenderPageBitmap()
    {
        var wasSelected = _selected; var wasShape = _selectedShape;
        if (wasSelected is not null) wasSelected.IsSelected = false; if (wasShape is not null) wasShape.IsSelected = false;
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
        finally { if (wasSelected is not null) wasSelected.IsSelected = true; if (wasShape is not null) wasShape.IsSelected = true; }
    }

    private async Task AddTextAtAsync((double X, double Y) point)
    {
        if (_pageId is null || _contentRepository is null) { StatusChanged?.Invoke(this, "请先创建或选择页面"); return; }
        var before = CaptureSnapshot();
        SetTool(CanvasTool.Select);
        var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Text, point.X, point.Y, 420, 72, string.Empty);
        var control = AddObjectVisual(model); SelectObject(control); ScrollObjectIntoView(control);
        if (FindDescendant<RichTextBox>(control) is { } editor) { _activeRichText = editor; editor.Focus(); Keyboard.Focus(editor); }
        RegisterHistory("插入文本块", before);
        DirtyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, "文本块已创建 · 高度会随内容自动增长");
    }

    private CanvasObjectControl AddObjectVisual(ContentObject item)
    {
        UIElement content = item.Type switch
        {
            ContentObjectType.Text => CreateRichTextEditor(item),
            ContentObjectType.Image => CreateImage(item),
            ContentObjectType.Attachment => CreateAttachment(item),
            ContentObjectType.Table => CreateTableEditor(item),
            _ => new TextBlock { Text = "不支持的对象", Margin = new Thickness(8) }
        };
        var control = new CanvasObjectControl(item, content) { SnapToGrid = false, IsHitTestVisible = _currentTool == CanvasTool.Select };
        control.OverlayLayer.IsHitTestVisible = _currentTool == CanvasTool.Select;
        control.ObjectManipulationStarted += (_, _) => _pendingManipulationSnapshot = CaptureSnapshot();
        control.Changed += (_, _) => RunSafe(HandleObjectChangedAsync(control), "保存对象");
        control.HostSizeChanged += (_, _) =>
        {
            // Text blocks can shrink after text deletion without a drag operation. Keep
            // embedded shapes inside the resized host so they never become clipped and
            // impossible to select.
            foreach (var childShape in control.OverlayLayer.Children.OfType<DrawingShapeControl>())
                if (childShape.EnsureWithinHostBounds()) RunSafe(SaveShapeAsync(childShape), "调整嵌入形状");
        };
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FontFamily = new FontFamily("Segoe UI"), FontSize = 16, Padding = new Thickness(8, 5, 8, 8), AcceptsTab = true,
            IsUndoEnabled = true, IsDocumentEnabled = true
        };
        SpellCheck.SetIsEnabled(editor, false);
        editor.GotKeyboardFocus += (_, _) =>
        {
            _activeRichText = editor;
            if (!_restoringHistory && !_textEditStarts.ContainsKey(item.Id)) _textEditStarts[item.Id] = CaptureSnapshot();
        };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (_textEditStarts.Remove(item.Id, out var before)) RegisterHistory("编辑文本", before);
        };
        editor.PreviewKeyDown += (_, e) => RichText_PreviewKeyDown(editor, item, e);
        HookInteractiveDocumentElements(editor, item);
        editor.TextChanged += (_, _) =>
        {
            // Keep the keystroke path lightweight. UpdateLayout() plus full XAML
            // serialization on every character can stall the UI, especially together
            // with auto-height layout. Serialize only after the debounce interval.
            QueueObjectSave(item.Id, async () =>
            {
                item.Payload = XamlWriter.Save(editor.Document);
                item.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
                if (_contentRepository is not null) await _contentRepository.UpsertAsync(item);
                DirtyChanged?.Invoke(this, EventArgs.Empty);
            });
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
        table.StatusMessage += message => StatusChanged?.Invoke(this, message);
        return table;
    }

    private DrawingShapeControl AddShapeVisual(ContentObject item)
    {
        var payload = ParseShapePayload(item.Payload);
        var kind = Enum.TryParse<ShapeKind>(payload.Kind, true, out var parsed) ? parsed : ShapeKind.Rectangle;
        var stroke = ParseColor(payload.Stroke, Color.FromRgb(108, 42, 165));
        var fill = ParseColor(payload.Fill, Color.FromArgb(24, 108, 42, 165));
        var metadata = ParseShapeLayerMetadata(item.StyleJson);
        var control = new DrawingShapeControl(
            item, kind, stroke, fill, Math.Clamp(payload.Thickness, 0.5, 16),
            payload.StartAtRight ?? false, payload.StartAtBottom ?? true)
        {
            ParentObjectId = metadata.ParentObjectId,
            IsHitTestVisible = _currentTool == CanvasTool.Select
        };
        control.ObjectManipulationStarted += (_, _) => _pendingManipulationSnapshot = CaptureSnapshot();
        control.Changed += (_, _) => RunSafe(HandleShapeChangedAsync(control), "保存形状");
        control.Activated += (_, _) => SelectShape(control);
        control.ContextMenu = BuildShapeContextMenu(control);

        Canvas host = _shapeLayer;
        var repairedParentReference = false;
        if (!string.IsNullOrWhiteSpace(metadata.ParentObjectId))
        {
            var parent = _objects.Children.OfType<CanvasObjectControl>().FirstOrDefault(x => x.Model.Id == metadata.ParentObjectId);
            if (parent is not null) host = parent.OverlayLayer;
            else
            {
                // Parent object may have been removed by an older/broken copy operation.
                // Fall back to the page drawing layer and repair metadata in memory.
                control.ParentObjectId = null;
                item.StyleJson = JsonSerializer.Serialize(new ShapeLayerMetadata(null));
                repairedParentReference = true;
            }
        }
        Canvas.SetLeft(control, item.X); Canvas.SetTop(control, item.Y); Panel.SetZIndex(control, item.ZIndex);
        host.Children.Add(control);
        if (host != _shapeLayer && host is Canvas childLayer)
            childLayer.IsHitTestVisible = _currentTool == CanvasTool.Select;
        if (repairedParentReference) RunSafe(SaveShapeAsync(control), "修复形状图层");
        return control;
    }

    private ContextMenu BuildShapeContextMenu(DrawingShapeControl control)
    {
        var menu = new ContextMenu();
        var duplicate = new MenuItem { Header = "创建副本    Ctrl+D" };
        duplicate.Click += async (_, _) => { SelectShape(control); await DuplicateSelectedAsync(); };
        var delete = new MenuItem { Header = "删除形状    Delete" };
        delete.Click += async (_, _) => { SelectShape(control); await DeleteSelectedAsync(); };
        menu.Items.Add(duplicate); menu.Items.Add(delete);
        return menu;
    }

    private async Task HandleObjectChangedAsync(CanvasObjectControl control)
    {
        await SaveObjectAsync(control);
        foreach (var childShape in control.OverlayLayer.Children.OfType<DrawingShapeControl>())
        {
            if (childShape.EnsureWithinHostBounds()) await SaveShapeAsync(childShape);
        }
        ExpandSurfaceToContent();
        DirtyChanged?.Invoke(this, EventArgs.Empty);
        if (_pendingManipulationSnapshot is { } before)
        {
            RegisterHistory("移动/调整对象", before);
            _pendingManipulationSnapshot = null;
        }
    }

    private async Task HandleShapeChangedAsync(DrawingShapeControl control)
    {
        await SaveShapeAsync(control);
        ExpandSurfaceToContent();
        DirtyChanged?.Invoke(this, EventArgs.Empty);
        if (_pendingManipulationSnapshot is { } before)
        {
            RegisterHistory("移动/调整形状", before);
            _pendingManipulationSnapshot = null;
        }
    }

    private async Task SaveShapeAsync(DrawingShapeControl control)
    {
        if (_contentRepository is null) return;
        control.Model.X = Canvas.GetLeft(control);
        control.Model.Y = Canvas.GetTop(control);
        control.Model.Width = control.ActualWidth > 0 ? control.ActualWidth : control.Width;
        control.Model.Height = control.ActualHeight > 0 ? control.ActualHeight : control.Height;
        control.Model.StyleJson = JsonSerializer.Serialize(new ShapeLayerMetadata(control.ParentObjectId));
        await _contentRepository.UpsertAsync(control.Model);
    }

    private static ShapeLayerMetadata ParseShapeLayerMetadata(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? new ShapeLayerMetadata(null) : JsonSerializer.Deserialize<ShapeLayerMetadata>(json) ?? new ShapeLayerMetadata(null); }
        catch { return new ShapeLayerMetadata(null); }
    }

    private UIElement CreateImage(ContentObject item)
    {
        var payload = ParsePayload(item.Payload);
        if (_mediaStore is not null && payload is not null)
        {
            var path = _mediaStore.GetAbsolutePath(payload.RelativePath);
            if (File.Exists(path))
            {
                var image = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(6) };
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); image.Source = bitmap;
                return image;
            }
            return CreateMissingMediaPlaceholder($"图片文件缺失\n{payload.FileName}");
        }
        return CreateMissingMediaPlaceholder("图片数据无法读取");
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
            if (_mediaStore is null || payload is null) { StatusChanged?.Invoke(this, "附件信息无法读取"); return; }
            var path = _mediaStore.GetAbsolutePath(payload.RelativePath);
            if (!File.Exists(path)) { StatusChanged?.Invoke(this, $"附件文件缺失：{payload.FileName}"); return; }
            TryOpenFile(path, payload.FileName);
        };
        Grid.SetColumn(button, 1); panel.Children.Add(button); return panel;
    }

    private ContextMenu BuildContextMenu(CanvasObjectControl control)
    {
        var menu = new ContextMenu();
        var important = new MenuItem { Header = control.Model.IsImportant ? "移除重要标记" : "标记为重要" };
        important.Click += async (_, _) => { SelectObject(control); await ToggleImportantSelectedAsync(); important.Header = control.Model.IsImportant ? "移除重要标记" : "标记为重要"; };
        var front = new MenuItem { Header = "置于顶层" }; front.Click += async (_, _) => { var before = CaptureSnapshot(); control.Model.ZIndex = NextTopZ(); Panel.SetZIndex(control, control.Model.ZIndex); await SaveObjectAsync(control); RegisterHistory("置于顶层", before); };
        var back = new MenuItem { Header = "置于底层" }; back.Click += async (_, _) => { var before = CaptureSnapshot(); control.Model.ZIndex = NextBottomZ(); Panel.SetZIndex(control, control.Model.ZIndex); await SaveObjectAsync(control); RegisterHistory("置于底层", before); };
        var duplicate = new MenuItem { Header = "创建副本    Ctrl+D" }; duplicate.Click += async (_, _) => { SelectObject(control); await DuplicateSelectedAsync(); };
        var delete = new MenuItem { Header = "删除对象    Delete" }; delete.Click += async (_, _) => { SelectObject(control); await DeleteSelectedAsync(); };
        menu.Items.Add(important); menu.Items.Add(new Separator()); menu.Items.Add(front); menu.Items.Add(back); menu.Items.Add(new Separator()); menu.Items.Add(duplicate); menu.Items.Add(delete); return menu;
    }

    private async Task SaveObjectAsync(CanvasObjectControl control)
    {
        if (_contentRepository is null) return;
        control.Model.X = Canvas.GetLeft(control); control.Model.Y = Canvas.GetTop(control); control.Model.Width = control.ActualWidth > 0 ? control.ActualWidth : control.Width; control.Model.Height = control.ActualHeight > 0 ? control.ActualHeight : control.Height;
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
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (_pendingObjectSaves.Remove(id, out var action)) RunSafe(action(), "自动保存");
            };
            _textTimers[id] = timer;
        }
        timer.Stop(); timer.Start();
    }

    private void MarkInkChanged(string description)
    {
        QueueInkSave();
        _pendingInkHistoryDescription = description;
    }

    private void CommitPendingInkHistory()
    {
        if (_pendingInkSnapshot is { } before && _pendingInkHistoryDescription is { Length: > 0 } description)
            RegisterHistory(description, before);
        _pendingInkSnapshot = null;
        _pendingInkHistoryDescription = null;
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
        if (_selectedShape is not null) _selectedShape.IsSelected = false;
        _selectedShape = null;
        _selected = control; _activeRichText = null;
        if (_selected is not null)
        {
            _selected.IsSelected = true;
            if (_selected.Model.Type == ContentObjectType.Text) _activeRichText = FindDescendant<RichTextBox>(_selected);
        }
        SelectionChanged?.Invoke(_selected?.Model);
    }

    private void SelectShape(DrawingShapeControl? control)
    {
        if (_selected is not null) _selected.IsSelected = false;
        if (_selectedShape is not null) _selectedShape.IsSelected = false;
        _selected = null; _activeRichText = null; _selectedShape = control;
        if (_selectedShape is not null)
        {
            _selectedShape.IsSelected = true;
            // Keep the drawing toolbar synchronized with the selected shape. Without
            // this, changing only stroke/fill/thickness can accidentally reuse the
            // previous creation defaults and overwrite the selected shape's kind.
            try
            {
                var payload = ParseShapePayload(_selectedShape.Model.Payload);
                if (Enum.TryParse<ShapeKind>(payload.Kind, true, out var kind)) _shapeKind = kind;
                _shapeStrokeColor = ParseColor(payload.Stroke, _shapeStrokeColor);
                _shapeFillColor = ParseColor(payload.Fill, _shapeFillColor);
                _shapeThickness = Math.Clamp(payload.Thickness, 0.5, 16);
            }
            catch { }
        }
        SelectionChanged?.Invoke(_selectedShape?.Model);
    }

    private void Objects_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool == CanvasTool.Shape)
        {
            if (_pageId is null || _contentRepository is null) return;
            var point = e.GetPosition(_objects);
            _shapeStart = point;
            _shapeParent = FindContentObjectAt(point);
            _pendingManipulationSnapshot = CaptureSnapshot();
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
            _shapeLayer.Children.Add(_shapePreview);
            _objects.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (_currentTool != CanvasTool.Select || e.OriginalSource != _objects) return;
        var selectPoint = e.GetPosition(_objects);
        if (e.ClickCount >= 2) { RunSafe(AddTextAtAsync((selectPoint.X, selectPoint.Y)), "创建文本块"); e.Handled = true; return; }
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
        _shapePreview.UpdateDirection(_shapeStart.Value.X > now.X, _shapeStart.Value.Y > now.Y);
        _shapePreview.InvalidateVisual();
    }

    private void Objects_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool != CanvasTool.Shape || _shapeStart is null || _shapePreview is null || _pageId is null || _contentRepository is null) return;
        var now = e.GetPosition(_objects);
        e.Handled = true;
        RunSafe(CompleteShapeAsync(now), "创建形状");
    }

    private async Task CompleteShapeAsync(Point now)
    {
        if (_shapeStart is null || _shapePreview is null || _pageId is null || _contentRepository is null) return;
        var start = _shapeStart.Value;
        _shapeStart = null;
        if (_objects.IsMouseCaptured) _objects.ReleaseMouseCapture();
        var preview = _shapePreview;
        _shapePreview = null;
        _shapeLayer.Children.Remove(preview);

        var parent = _shapeParent;
        try
        {
            var globalX = Math.Min(start.X, now.X);
            var globalY = Math.Min(start.Y, now.Y);
            var w = Math.Max(36, Math.Abs(now.X - start.X));
            var h = Math.Max(28, Math.Abs(now.Y - start.Y));
            if (_shapeKind is ShapeKind.Line or ShapeKind.Arrow) h = Math.Max(34, h);

            string? parentObjectId = null;
            var x = globalX;
            var y = globalY;
            if (parent is not null)
            {
                parentObjectId = parent.Model.Id;
                var parentX = Canvas.GetLeft(parent);
                var parentY = Canvas.GetTop(parent);
                x = Math.Max(0, globalX - parentX);
                y = Math.Max(0, globalY - parentY);
                var parentW = parent.ActualWidth > 0 ? parent.ActualWidth : parent.Width;
                var parentH = parent.ActualHeight > 0 ? parent.ActualHeight : parent.Model.Height;
                w = Math.Min(w, Math.Max(28, parentW - x));
                h = Math.Min(h, Math.Max(24, parentH - y));
            }

            var startAtRight = start.X > now.X;
            var startAtBottom = start.Y > now.Y;
            var payload = JsonSerializer.Serialize(new ShapePayload(
                _shapeKind.ToString(), ToHex(_shapeStrokeColor), ToHex(_shapeFillColor), _shapeThickness,
                startAtRight, startAtBottom));
            var model = await _contentRepository.CreateAsync(_pageId, ContentObjectType.Shape, x, y, w, h, payload);
            model.StyleJson = JsonSerializer.Serialize(new ShapeLayerMetadata(parentObjectId));
            await _contentRepository.UpsertAsync(model);
            var visual = AddShapeVisual(model);
            SelectShape(visual);
            ExpandSurfaceToContent();
            if (_pendingManipulationSnapshot is { } before) RegisterHistory("绘制形状", before);
            DirtyChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, parentObjectId is null
                ? $"已在绘图层创建{ShapeKindName(_shapeKind)} · 已返回选择模式"
                : $"已在内容块内创建{ShapeKindName(_shapeKind)} · 会随内容块一起移动");
        }
        finally
        {
            _pendingManipulationSnapshot = null;
            _shapeParent = null;
            SetTool(CanvasTool.Select);
        }
    }

    private CanvasObjectControl? FindContentObjectAt(Point point)
    {
        // Prefer the visually top-most eligible object. This allows shapes to be
        // embedded in text/table/image layers without creating another canvas card.
        return _objects.Children.OfType<CanvasObjectControl>()
            .Where(c => c.Model.Type is ContentObjectType.Text or ContentObjectType.Table or ContentObjectType.Image)
            .OrderByDescending(Panel.GetZIndex)
            .FirstOrDefault(c =>
            {
                var x = Canvas.GetLeft(c); var y = Canvas.GetTop(c);
                var w = c.ActualWidth > 0 ? c.ActualWidth : c.Width;
                var h = c.ActualHeight > 0 ? c.ActualHeight : c.Model.Height;
                return point.X >= x && point.X <= x + w && point.Y >= y && point.Y <= y + h;
            });
    }

    private void Objects_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var point = e.GetPosition(_objects);
        e.Handled = true;
        RunSafe(ImportDroppedFilesAsync(files, point), "导入拖放文件");
    }

    private async Task ImportDroppedFilesAsync(IEnumerable<string> files, Point point)
    {
        var offset = 0d;
        foreach (var file in files.Take(20))
        {
            var p = new Point(point.X + offset, point.Y + offset);
            offset += 18;
            if (IsImage(file)) await AddImageAsync(file, p);
            else await AddAttachmentAsync(file, p);
        }
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
            var next = new Paragraph
            {
                Margin = paragraph.Margin,
                Padding = paragraph.Padding,
                FontFamily = paragraph.FontFamily,
                FontSize = paragraph.FontSize,
                FontWeight = paragraph.FontWeight,
                FontStyle = paragraph.FontStyle,
                FontStretch = paragraph.FontStretch,
                Foreground = paragraph.Foreground,
                Background = paragraph.Background,
                TextAlignment = paragraph.TextAlignment,
                LineHeight = paragraph.LineHeight,
                TextIndent = paragraph.TextIndent
            };
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
            var before = CaptureSnapshot();
            box.IsChecked = box.IsChecked != true;
            e.Handled = true;
            PersistTodoCheckState(editor, item, box, box.IsChecked == true);
            RegisterHistory("切换待办状态", before);
        }), true);
    }

    private void PersistTodoCheckState(RichTextBox editor, ContentObject? item, CheckBox box, bool completed)
    {
        var paragraph = FindParagraphForTodo(editor, box);
        if (paragraph is not null) ApplyTodoCompletionVisual(paragraph, completed);
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

    private static Paragraph? FindParagraphForTodo(RichTextBox editor, CheckBox target)
    {
        foreach (var paragraph in editor.Document.Blocks.OfType<Paragraph>())
            foreach (var inline in paragraph.Inlines)
                if (inline is InlineUIContainer container && ReferenceEquals(container.Child, target)) return paragraph;
        return null;
    }

    private static void ApplyTodoCompletionVisual(Paragraph paragraph, bool completed)
    {
        // Paragraph is a TextElement/FrameworkContentElement rather than a UIElement,
        // so it does not expose UIElement.Opacity. Keep the completion visual at the
        // paragraph TextDecorations level; inline formatting remains intact.
        paragraph.TextDecorations = completed ? TextDecorations.Strikethrough : null;
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
                    if (FindParagraphForTodo(editor, box) is { } todoParagraph) ApplyTodoCompletionVisual(todoParagraph, box.IsChecked == true);
                    AttachTodoCheckBoxBehavior(box, editor, item);
                }
                else if (inline is Hyperlink link)
                {
                    link.RequestNavigate += (_, e) => TryOpenUri(e.Uri);
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

    private void RunSafe(Task task, string action) => _ = ObserveAsync(task, action);

    private async Task ObserveAsync(Task task, string action)
    {
        try { await task; }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"{action}失败：{ex.Message}");
        }
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
    private int NextTopZ() => _objects.Children.OfType<CanvasObjectControl>().Select(x => x.Model.ZIndex)
        .Concat(_shapeLayer.Children.OfType<DrawingShapeControl>().Select(x => x.Model.ZIndex)).DefaultIfEmpty(0).Max() + 1;
    private int NextBottomZ() => _objects.Children.OfType<CanvasObjectControl>().Select(x => x.Model.ZIndex)
        .Concat(_shapeLayer.Children.OfType<DrawingShapeControl>().Select(x => x.Model.ZIndex)).DefaultIfEmpty(0).Min() - 1;

    private void ExpandSurfaceToContent()
    {
        var maxX = 1600d; var maxY = 1000d;
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>())
        {
            var w = child.ActualWidth > 0 ? child.ActualWidth : child.Width; var h = child.ActualHeight > 0 ? child.ActualHeight : child.Model.Height;
            maxX = Math.Max(maxX, Canvas.GetLeft(child) + w + 900); maxY = Math.Max(maxY, Canvas.GetTop(child) + h + 900);
        }
        foreach (var child in _shapeLayer.Children.OfType<DrawingShapeControl>())
        {
            maxX = Math.Max(maxX, Canvas.GetLeft(child) + child.Width + 900); maxY = Math.Max(maxY, Canvas.GetTop(child) + child.Height + 900);
        }
        var width = Math.Max(6000, maxX); var height = Math.Max(4000, maxY);
        _objects.Width = _shapeLayer.Width = _ink.Width = _surface.Width = _paper.Width = width;
        _objects.Height = _shapeLayer.Height = _ink.Height = _surface.Height = _paper.Height = height;
    }

    private Rect GetContentBounds()
    {
        Rect? result = null;
        foreach (var child in _objects.Children.OfType<CanvasObjectControl>())
        {
            var rect = new Rect(Canvas.GetLeft(child), Canvas.GetTop(child), child.ActualWidth > 0 ? child.ActualWidth : child.Width, child.ActualHeight > 0 ? child.ActualHeight : child.Model.Height);
            result = result is null ? rect : Rect.Union(result.Value, rect);
        }
        foreach (var child in _shapeLayer.Children.OfType<DrawingShapeControl>())
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
        var viewportPoint = e.GetPosition(_scroll);
        var oldZoom = Zoom;
        var logicalX = (_scroll.HorizontalOffset + viewportPoint.X) / oldZoom;
        var logicalY = (_scroll.VerticalOffset + viewportPoint.Y) / oldZoom;
        var next = Math.Clamp(Zoom + (e.Delta > 0 ? 0.1 : -0.1), 0.1, 4.0);
        SetZoom(next);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _scroll.ScrollToHorizontalOffset(Math.Max(0, logicalX * next - viewportPoint.X));
            _scroll.ScrollToVerticalOffset(Math.Max(0, logicalY * next - viewportPoint.Y));
        }));
        e.Handled = true;
    }
    private void SetZoom(double zoom)
    {
        _scale.ScaleX = _scale.ScaleY = zoom;
        ZoomChanged?.Invoke(zoom);
        StatusChanged?.Invoke(this, $"缩放 {zoom:P0}");
    }
    private void Scroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var spacePan = e.ChangedButton == MouseButton.Left && Keyboard.IsKeyDown(Key.Space);
        var middlePan = e.ChangedButton == MouseButton.Middle;
        if (!spacePan && !middlePan) return;
        _panButton = e.ChangedButton;
        _panStart = e.GetPosition(_scroll); _startH = _scroll.HorizontalOffset; _startV = _scroll.VerticalOffset;
        _scroll.CaptureMouse(); _scroll.Cursor = Cursors.Hand; e.Handled = true;
        StatusChanged?.Invoke(this, spacePan ? "画布平移 · 松开鼠标继续编辑" : "画布平移");
    }
    private void Scroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is null || _panButton is null) return;
        if (_panButton == MouseButton.Middle && e.MiddleButton != MouseButtonState.Pressed) return;
        if (_panButton == MouseButton.Left && e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(_scroll); var delta = now - _panStart.Value;
        _scroll.ScrollToHorizontalOffset(_startH - delta.X); _scroll.ScrollToVerticalOffset(_startV - delta.Y);
    }
    private void Scroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panButton is not null && e.ChangedButton != _panButton) return;
        _panStart = null; _panButton = null;
        if (_scroll.IsMouseCaptured) _scroll.ReleaseMouseCapture();
        _scroll.Cursor = Cursors.Arrow;
    }

    public async Task UndoAsync()
    {
        if (Keyboard.FocusedElement is RichTextBox focused && focused.CanUndo)
        {
            focused.Undo(); QueueSelectedTextSave(); StatusChanged?.Invoke(this, "已撤销文本编辑"); return;
        }
        if (_undoStack.Count == 0) { StatusChanged?.Invoke(this, "没有可撤销的操作"); return; }
        var unit = _undoStack.Pop();
        _redoStack.Push(unit);
        await RestoreSnapshotAsync(unit.Before);
        StatusChanged?.Invoke(this, $"已撤销：{unit.Description}");
    }

    public async Task RedoAsync()
    {
        if (Keyboard.FocusedElement is RichTextBox focused && focused.CanRedo)
        {
            focused.Redo(); QueueSelectedTextSave(); StatusChanged?.Invoke(this, "已重做文本编辑"); return;
        }
        if (_redoStack.Count == 0) { StatusChanged?.Invoke(this, "没有可重做的操作"); return; }
        var unit = _redoStack.Pop();
        _undoStack.Push(unit);
        await RestoreSnapshotAsync(unit.After);
        StatusChanged?.Invoke(this, $"已重做：{unit.Description}");
    }

    private CanvasStateSnapshot CaptureSnapshot()
    {
        SyncVisualModels();
        var items = new List<ContentObject>();
        items.AddRange(_objects.Children.OfType<CanvasObjectControl>().Select(x => CloneObject(x.Model)));
        items.AddRange(_shapeLayer.Children.OfType<DrawingShapeControl>().Select(x => CloneObject(x.Model)));
        foreach (var parent in _objects.Children.OfType<CanvasObjectControl>())
            items.AddRange(parent.OverlayLayer.Children.OfType<DrawingShapeControl>().Select(x => CloneObject(x.Model)));
        using var stream = new MemoryStream();
        _ink.Strokes.Save(stream);
        return new CanvasStateSnapshot(items, stream.ToArray());
    }

    private void SyncVisualModels()
    {
        foreach (var control in _objects.Children.OfType<CanvasObjectControl>())
        {
            control.Model.X = Canvas.GetLeft(control);
            control.Model.Y = Canvas.GetTop(control);
            control.Model.Width = control.ActualWidth > 0 ? control.ActualWidth : control.Width;
            control.Model.Height = control.ActualHeight > 0 ? control.ActualHeight : control.Model.Height;
            if (control.Model.Type == ContentObjectType.Text && FindDescendant<RichTextBox>(control) is { } editor)
            {
                control.Model.Payload = XamlWriter.Save(editor.Document);
                control.Model.SearchText = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text.Trim();
            }
            else if (control.Model.Type == ContentObjectType.Table && FindDescendant<TableEditorControl>(control) is { } table)
            {
                control.Model.Payload = table.Payload; control.Model.SearchText = table.SearchText;
            }
        }
        foreach (var shape in _shapeLayer.Children.OfType<DrawingShapeControl>()) SyncShapeModel(shape);
        foreach (var parent in _objects.Children.OfType<CanvasObjectControl>())
            foreach (var shape in parent.OverlayLayer.Children.OfType<DrawingShapeControl>()) SyncShapeModel(shape);
    }

    private static void SyncShapeModel(DrawingShapeControl shape)
    {
        shape.Model.X = Canvas.GetLeft(shape); shape.Model.Y = Canvas.GetTop(shape);
        shape.Model.Width = shape.ActualWidth > 0 ? shape.ActualWidth : shape.Width;
        shape.Model.Height = shape.ActualHeight > 0 ? shape.ActualHeight : shape.Height;
        shape.Model.StyleJson = JsonSerializer.Serialize(new ShapeLayerMetadata(shape.ParentObjectId));
    }

    private void RegisterHistory(string description, CanvasStateSnapshot before)
    {
        if (_restoringHistory) return;
        var after = CaptureSnapshot();
        if (SnapshotsEquivalent(before, after)) return;
        _undoStack.Push(new UndoUnit(description, before, after));
        while (_undoStack.Count > 80)
        {
            // Stack has no RemoveBottom API; rebuilding is acceptable at this small cap.
            var trimmed = _undoStack.Reverse().Skip(1).Reverse().ToArray();
            _undoStack.Clear(); foreach (var item in trimmed.Reverse()) _undoStack.Push(item);
        }
        _redoStack.Clear();
    }

    private static bool SnapshotsEquivalent(CanvasStateSnapshot a, CanvasStateSnapshot b)
    {
        if (a.Objects.Count != b.Objects.Count || a.InkData.Length != b.InkData.Length) return false;
        if (!a.InkData.AsSpan().SequenceEqual(b.InkData)) return false;
        return a.Objects.OrderBy(x => x.Id).Zip(b.Objects.OrderBy(x => x.Id)).All(pair =>
        {
            var (x, y) = pair;
            return x.Id == y.Id && x.X == y.X && x.Y == y.Y && x.Width == y.Width && x.Height == y.Height &&
                   x.ZIndex == y.ZIndex && x.Payload == y.Payload && x.StyleJson == y.StyleJson && x.IsImportant == y.IsImportant;
        });
    }

    private async Task RestoreSnapshotAsync(CanvasStateSnapshot snapshot)
    {
        if (_pageId is null || _contentRepository is null || _inkRepository is null) return;
        _restoringHistory = true;
        try
        {
            foreach (var timer in _textTimers.Values) timer.Stop();
            _textTimers.Clear(); _pendingObjectSaves.Clear(); _textEditStarts.Clear(); _pendingInkSnapshot = null; _pendingInkHistoryDescription = null; _inkSaveTimer.Stop();
            await _contentRepository.DeleteAllForPageAsync(_pageId);
            foreach (var source in snapshot.Objects)
                await _contentRepository.UpsertAsync(CloneObject(source));
            await _inkRepository.SaveAsync(_pageId, snapshot.InkData);

            _objects.Children.Clear(); _shapeLayer.Children.Clear(); _ink.Strokes.Clear();
            _selected = null; _selectedShape = null; _activeRichText = null; SelectionChanged?.Invoke(null);
            foreach (var item in snapshot.Objects.Where(x => x.Type != ContentObjectType.Shape)) AddObjectVisual(CloneObject(item));
            foreach (var item in snapshot.Objects.Where(x => x.Type == ContentObjectType.Shape)) AddShapeVisual(CloneObject(item));
            if (snapshot.InkData.Length > 0) using (var stream = new MemoryStream(snapshot.InkData)) _ink.Strokes = new StrokeCollection(stream);
            ExpandSurfaceToContent(); DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _restoringHistory = false; }
    }

    private static ContentObject CloneObject(ContentObject x) => new()
    {
        Id = x.Id, PageId = x.PageId, Type = x.Type, X = x.X, Y = x.Y, Width = x.Width, Height = x.Height,
        ZIndex = x.ZIndex, Payload = x.Payload, StyleJson = x.StyleJson, IsTodo = false, TodoCompleted = false,
        IsImportant = x.IsImportant, SearchText = x.SearchText, LocalVersion = x.LocalVersion,
        CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt
    };

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

    private static ShapePayload ParseShapePayload(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new ShapePayload("Rectangle", "#6C2AA5", "#186C2AA5", 2)
                : JsonSerializer.Deserialize<ShapePayload>(json) ?? new ShapePayload("Rectangle", "#6C2AA5", "#186C2AA5", 2);
        }
        catch
        {
            return new ShapePayload("Rectangle", "#6C2AA5", "#186C2AA5", 2);
        }
    }

    private Border CreateMissingMediaPlaceholder(string text) => new()
    {
        Margin = new Thickness(6),
        Background = new SolidColorBrush(Color.FromRgb(248, 247, 250)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(221, 214, 225)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Child = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(120, 113, 126)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12)
        }
    };

    private void TryOpenUri(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"无法打开链接：{ex.Message}"); }
    }

    private void TryOpenFile(string path, string displayName)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusChanged?.Invoke(this, $"无法打开附件“{displayName}”：{ex.Message}"); }
    }

    private sealed record ShapePayload(
        string Kind, string Stroke, string Fill, double Thickness,
        bool? StartAtRight = null, bool? StartAtBottom = null);
    private sealed record ShapeLayerMetadata(string? ParentObjectId);
    private sealed record CanvasStateSnapshot(IReadOnlyList<ContentObject> Objects, byte[] InkData);
    private sealed record UndoUnit(string Description, CanvasStateSnapshot Before, CanvasStateSnapshot After);
    private sealed record MediaPayload(string FileName, string RelativePath);
}
