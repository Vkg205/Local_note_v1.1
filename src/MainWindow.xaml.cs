using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LocalNote.App.Controls;
using LocalNote.App.Dialogs;
using LocalNote.App.Services;
using LocalNote.App.ViewModels;
using LocalNote.Domain.Entities;
using LocalNote.Storage.Services;
using Microsoft.Win32;

namespace LocalNote.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly UserSettingsService _userSettings = new();
    private bool _allowClose;
    private bool _navigationVisible = true;
    private bool _pagesVisible = true;
    private bool _ribbonCollapsed;
    private bool _focusMode;
    private const double RibbonSafeMinHeight = 106;
    private const double RibbonDefaultHeight = 112;
    private const double RibbonMaxHeight = 170;
    private const double PageHeaderSafeMinHeight = 60;
    private const double PageHeaderDefaultHeight = 66;
    private const double PageHeaderMaxHeight = 120;

    private GridLength _lastRibbonHeight = new(RibbonDefaultHeight);
    private GridLength _lastPageHeaderHeight = new(PageHeaderDefaultHeight);
    private GridLength _lastNavigationWidth = new(220);
    private GridLength _lastPagesWidth = new(276);
    private int _pageLoadVersion;
    private string? _pendingHighlightObjectId;
    private Color _textColor = Color.FromRgb(37, 99, 235);
    private Color _highlightColor = Color.FromRgb(255, 238, 153);
    private Color _inkColor = Color.FromRgb(32, 33, 36);
    private Color _tableHeaderColor = Color.FromRgb(240, 229, 248);
    private Color _tableBorderColor = Color.FromRgb(221, 214, 225);
    private Color _shapeStrokeColor = Color.FromRgb(108, 42, 165);
    private Color _shapeFillColor = Color.FromRgb(189, 155, 224);

    public MainWindow()
    {
        InitializeComponent(); DataContext = _viewModel;
        Loaded += async (_, _) => { await RestoreUiLayoutAsync(); await _viewModel.InitializeAsync(); await LoadActivePageAsync(); };
        _viewModel.ActivePageChanged += async (_, _) =>
        {
            var version = Interlocked.Increment(ref _pageLoadVersion);
            await CanvasView.FlushAsync(); if (version != _pageLoadVersion) return;
            await LoadActivePageAsync();
            if (_pendingHighlightObjectId is { } id) { CanvasView.HighlightObject(id); _pendingHighlightObjectId = null; }
        };
        CanvasView.StatusChanged += (_, text) => _viewModel.StatusText = text;
        CanvasView.DirtyChanged += (_, _) => _viewModel.StatusText = $"已自动保存 · {DateTime.Now:HH:mm:ss}";
        CanvasView.ToolChanged += UpdateToolUi;
        CanvasView.SelectionChanged += UpdateSelectionUi;
        CanvasView.ZoomChanged += zoom => Dispatcher.Invoke(() => ZoomResetButton.Content = $"{zoom:P0}");
        Deactivated += async (_, _) => { try { await CanvasView.FlushAsync(); } catch { } };
        Closing += MainWindow_Closing;
    }

    private async Task LoadActivePageAsync() =>
        await CanvasView.BindAsync(_viewModel.SelectedPage?.Id, _viewModel.ContentRepository, _viewModel.InkRepository, _viewModel.MediaStore);

    private async void AddText_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePage("插入文本")) return;
        try { await CanvasView.AddTextAsync(); RibbonTabs.SelectedIndex = 0; }
        catch (Exception ex) { ShowOperationError("插入文本失败", ex); }
    }

    private async void AddTable_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePage("插入表格")) return;
        try { await CanvasView.AddTableAsync(); TableToolsTab.Visibility = Visibility.Visible; TableToolsTab.IsSelected = true; }
        catch (Exception ex) { ShowOperationError("插入表格失败", ex); }
    }

    private async void AddImage_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePage("插入图片")) return;
        var dialog = new OpenFileDialog { Title = "插入图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        try { foreach (var file in dialog.FileNames) await CanvasView.AddImageAsync(file); }
        catch (Exception ex) { ShowOperationError("插入图片失败", ex); }
    }

    private async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePage("插入附件")) return;
        var dialog = new OpenFileDialog { Title = "插入附件", Filter = "所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        try { foreach (var file in dialog.FileNames) await CanvasView.AddAttachmentAsync(file); }
        catch (Exception ex) { ShowOperationError("插入附件失败", ex); }
    }

    private async void DeleteObject_Click(object sender, RoutedEventArgs e) => await CanvasView.DeleteSelectedAsync();
    private void ClearInk_Click(object sender, RoutedEventArgs e)
    {
        if (!RequirePage("清除墨迹")) return;
        if (MessageBox.Show(this, "清除当前页全部墨迹？", "LocalNote", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) CanvasView.ClearInk();
    }

    private void SelectTool_Click(object sender, RoutedEventArgs e) => CanvasView.SetTool(CanvasTool.Select);
    private void PenTool_Click(object sender, RoutedEventArgs e) => CanvasView.SetTool(CanvasTool.Pen);
    private void PencilTool_Click(object sender, RoutedEventArgs e) => CanvasView.SetTool(CanvasTool.Pencil);
    private void HighlighterTool_Click(object sender, RoutedEventArgs e) => CanvasView.SetTool(CanvasTool.Highlighter);
    private void EraserTool_Click(object sender, RoutedEventArgs e) => CanvasView.SetTool(CanvasTool.Eraser);
    private void InkSelectTool_Click(object sender, RoutedEventArgs e) => CanvasView.SetTool(CanvasTool.InkSelect);
    private void InkColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("墨迹颜色", _inkColor, out var color)) return;
        _inkColor = color;
        CanvasView.SetInkColor(color);
        InkColorPreview.Background = new SolidColorBrush(color);
        _viewModel.StatusText = $"墨迹颜色：#{color.R:X2}{color.G:X2}{color.B:X2}";
    }
    private void InkSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || InkSizeCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        CanvasView.SetInkSize(size); _viewModel.StatusText = $"笔迹粗细：{size:0.#}";
    }

    private void ShapeKindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ShapeKindCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (Enum.TryParse<ShapeKind>(tag, true, out var kind)) CanvasView.SetShapeKind(kind);
    }

    private void ShapeStrokeColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("形状线条颜色", _shapeStrokeColor, out var color)) return;
        _shapeStrokeColor = color;
        ShapeStrokeColorPreview.Background = new SolidColorBrush(color);
        CanvasView.SetShapeStrokeColor(color);
    }

    private void ShapeFillColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("形状填充颜色", _shapeFillColor, out var color)) return;
        _shapeFillColor = color;
        ShapeFillColorPreview.Background = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B));
        CanvasView.SetShapeFillColor(color);
    }

    private void ShapeThicknessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ShapeThicknessCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        CanvasView.SetShapeThickness(size);
        _viewModel.StatusText = $"形状线宽：{size:0.#}";
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => CanvasView.UndoRichText();
    private void Redo_Click(object sender, RoutedEventArgs e) => CanvasView.RedoRichText();
    private void Bold_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleBold();
    private void Italic_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleItalic();
    private void Underline_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleUnderline();
    private void Bullets_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleBullets();
    private void Numbering_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleNumbering();
    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("文字颜色", _textColor, out var color)) return;
        _textColor = color;
        CanvasView.SetTextColor(color);
        TextColorPreview.Background = new SolidColorBrush(color);
    }

    private void HighlightColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("文本高亮颜色", _highlightColor, out var color)) return;
        _highlightColor = color;
        CanvasView.SetTextHighlight(color);
        HighlightColorPreview.Background = new SolidColorBrush(color);
    }
    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || FontSizeCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var size)) return;
        CanvasView.SetFontSize(size);
    }

    private void ToggleTodo_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleParagraphTodo();
    private async void ToggleImportant_Click(object sender, RoutedEventArgs e) => await CanvasView.ToggleImportantSelectedAsync();
    private void Strikethrough_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleStrikethrough();
    private void ClearFormatting_Click(object sender, RoutedEventArgs e) => CanvasView.ClearTextFormatting();
    private void Superscript_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleSuperscript();
    private void Subscript_Click(object sender, RoutedEventArgs e) => CanvasView.ToggleSubscript();
    private void AlignLeft_Click(object sender, RoutedEventArgs e) => CanvasView.SetParagraphAlignment(TextAlignment.Left);
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => CanvasView.SetParagraphAlignment(TextAlignment.Center);
    private void AlignRight_Click(object sender, RoutedEventArgs e) => CanvasView.SetParagraphAlignment(TextAlignment.Right);
    private void IndentIncrease_Click(object sender, RoutedEventArgs e) => CanvasView.ChangeIndent(20);
    private void IndentDecrease_Click(object sender, RoutedEventArgs e) => CanvasView.ChangeIndent(-20);
    private void LineSpacingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LineSpacingCombo.SelectedItem is not ComboBoxItem item || !double.TryParse(item.Content?.ToString(), out var factor)) return;
        CanvasView.SetLineSpacing(factor);
    }

    private void TextStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TextStyleCombo.SelectedItem is not ComboBoxItem item) return;
        var style = item.Content?.ToString() switch
        {
            "标题 1" => "H1", "标题 2" => "H2", "标题 3" => "H3", "标题 4" => "H4", "标题 5" => "H5", "标题 6" => "H6", "引用" => "Quote", _ => "Body"
        };
        CanvasView.ApplyTextStyle(style);
    }

    private void InsertHyperlink_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextInputDialog("插入超链接", "请输入完整网址（例如 https://example.com）", "https://") { Owner = this };
        if (dialog.ShowDialog() == true) CanvasView.InsertHyperlink(dialog.Value.Trim());
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => CanvasView.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => CanvasView.ZoomOut();
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => CanvasView.ResetZoom();
    private void FitContent_Click(object sender, RoutedEventArgs e) => CanvasView.FitToContent();
    private void PaperStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        CanvasView.SetPaperStyle(PaperStyleCombo.SelectedIndex switch { 1 => PaperStyle.Grid, 2 => PaperStyle.Lines, 3 => PaperStyle.Dots, _ => PaperStyle.Blank });
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => await RefreshSearchAsync();
    private async void SearchScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) await RefreshSearchAsync(); }
    private async Task RefreshSearchAsync()
    {
        if (_viewModel.SearchRepository is null || string.IsNullOrWhiteSpace(SearchBox.Text)) { SearchPopup.IsOpen = false; return; }
        string? notebook = null, section = null, page = null;
        switch (SearchScopeCombo.SelectedIndex)
        {
            case 1: notebook = _viewModel.SelectedNotebook?.Id; break;
            case 2: section = _viewModel.SelectedSection?.Id; break;
            case 3: page = _viewModel.SelectedPage?.Id; break;
        }
        var results = await _viewModel.SearchRepository.SearchAsync(SearchBox.Text.Trim(), notebook, section, page);
        SearchResults.ItemsSource = results; SearchPopup.IsOpen = results.Count > 0;
    }
    private async void SearchResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is not SearchHit hit) return;
        SearchPopup.IsOpen = false; _pendingHighlightObjectId = hit.ObjectId; await _viewModel.NavigateToPageAsync(hit.PageId);
    }

    private async void PageTitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => await _viewModel.RenameSelectedPageInlineAsync(PageTitleBox.Text);
    private async void PageTitleBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return; e.Handled = true; await _viewModel.RenameSelectedPageInlineAsync(PageTitleBox.Text); CanvasView.Focus();
    }
    private async void PinPage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPage is null) { _viewModel.StatusText = "请先选择页面"; return; }
        await _viewModel.ToggleSelectedPagePinAsync();
    }
    private async void MovePage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPage is null) { _viewModel.StatusText = "请先选择要移动的页面"; return; }
        var destination = await SelectPageDestinationAsync("移动页面到…"); if (destination is null) return;
        if (destination.SectionId == _viewModel.SelectedSection?.Id) { _viewModel.StatusText = "页面已在当前分区"; return; }
        await CanvasView.FlushAsync(); await _viewModel.MoveSelectedPageAsync(destination.SectionId);
    }

    private async void CopyPage_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPage is null) { _viewModel.StatusText = "请先选择要复制的页面"; return; }
        var destination = await SelectPageDestinationAsync("复制页面到…"); if (destination is null) return;
        await CanvasView.FlushAsync(); await _viewModel.CopySelectedPageAsync(destination.SectionId);
    }

    private async Task<PageDestination?> SelectPageDestinationAsync(string title)
    {
        var destinations = await _viewModel.GetPageDestinationsAsync(); if (destinations.Count == 0) return null;
        var combo = new ComboBox { ItemsSource = destinations, DisplayMemberPath = nameof(PageDestination.DisplayName), SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 14), MinHeight = 34 };
        var ok = new Button { Content = "确定", Style = (Style)FindResource("PrimaryButton"), MinWidth = 82, Margin = new Thickness(4) };
        var cancel = new Button { Content = "取消", Style = (Style)FindResource("CommandButton"), MinWidth = 82, Margin = new Thickness(4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(20) }; panel.Children.Add(new TextBlock { Text = "选择目标笔记本 / 分区", FontSize = 18, FontWeight = FontWeights.SemiBold }); panel.Children.Add(combo); panel.Children.Add(buttons);
        var window = new Window { Title = title, Owner = this, Width = 430, Height = 190, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = (Brush)FindResource("SurfaceBrush") };
        PageDestination? result = null; ok.Click += (_, _) => { result = combo.SelectedItem as PageDestination; window.DialogResult = true; }; cancel.Click += (_, _) => window.DialogResult = false;
        window.ShowDialog(); return result;
    }


    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode) { _viewModel.StatusText = "专注模式下已隐藏导航；按 F11 退出后再调整"; return; }
        if (_navigationVisible && NavigationColumn.Width.Value > 0) _lastNavigationWidth = NavigationColumn.Width;
        _navigationVisible = !_navigationVisible;
        NavigationColumn.Width = _navigationVisible ? (_lastNavigationWidth.Value > 0 ? _lastNavigationWidth : new GridLength(220)) : new GridLength(0);
        NavigationPanel.Visibility = _navigationVisible ? Visibility.Visible : Visibility.Collapsed;
        NavigationSplitter.Visibility = _navigationVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TogglePagesPane_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode) { _viewModel.StatusText = "专注模式下已隐藏页面列表；按 F11 退出后再调整"; return; }
        if (_pagesVisible && PagesColumn.Width.Value > 0) _lastPagesWidth = PagesColumn.Width;
        _pagesVisible = !_pagesVisible;
        PagesColumn.Width = _pagesVisible ? (_lastPagesWidth.Value > 0 ? _lastPagesWidth : new GridLength(276)) : new GridLength(0);
        PagesSplitter.Visibility = _pagesVisible ? Visibility.Visible : Visibility.Collapsed;
        PagesPaneButton.Content = _pagesVisible ? "▤ 页面" : "▥ 页面";
    }

    private void ToggleRibbon_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode) { _viewModel.StatusText = "专注模式下工具栏已隐藏；按 F11 退出后再调整"; return; }
        if (!_ribbonCollapsed)
        {
            if (RibbonRow.Height.Value >= RibbonSafeMinHeight) _lastRibbonHeight = RibbonRow.Height;
            _ribbonCollapsed = true;
            RibbonTabs.Visibility = Visibility.Collapsed;
            RibbonSplitter.Visibility = Visibility.Collapsed;
            RibbonRow.MinHeight = 0;
            RibbonRow.Height = new GridLength(0);
        }
        else
        {
            _ribbonCollapsed = false;
            RibbonRow.MinHeight = RibbonSafeMinHeight;
            RibbonTabs.Visibility = Visibility.Visible;
            RibbonSplitter.Visibility = Visibility.Visible;
            var restored = Math.Clamp(_lastRibbonHeight.Value <= 0 ? RibbonDefaultHeight : _lastRibbonHeight.Value, RibbonSafeMinHeight, RibbonMaxHeight);
            RibbonRow.Height = new GridLength(restored);
        }
        RibbonCompactButton.Content = _ribbonCollapsed ? "⌄ 工具" : "⌃ 工具";
        _viewModel.StatusText = _ribbonCollapsed ? "工具栏已折叠 · 画布空间增加" : "工具栏已展开";
    }

    private void ToggleFocusMode_Click(object sender, RoutedEventArgs e)
    {
        _focusMode = !_focusMode;
        if (_focusMode)
        {
            _lastRibbonHeight = RibbonRow.Height.Value > 0 ? RibbonRow.Height : _lastRibbonHeight;
            _lastPageHeaderHeight = PageHeaderRow.Height.Value > 0 ? PageHeaderRow.Height : _lastPageHeaderHeight;
            if (NavigationColumn.Width.Value > 0) _lastNavigationWidth = NavigationColumn.Width;
            if (PagesColumn.Width.Value > 0) _lastPagesWidth = PagesColumn.Width;
            RibbonRow.MinHeight = 0;
            RibbonTabs.Visibility = Visibility.Collapsed;
            RibbonSplitter.Visibility = Visibility.Collapsed;
            RibbonRow.Height = new GridLength(0);
            SectionTabsRow.Height = new GridLength(0);
            PageHeaderRow.MinHeight = 0;
            PageHeaderRow.Height = new GridLength(0);
            NavigationColumn.Width = new GridLength(0);
            PagesColumn.Width = new GridLength(0);
            NavigationPanel.Visibility = Visibility.Collapsed;
            NavigationSplitter.Visibility = Visibility.Collapsed;
            PagesSplitter.Visibility = Visibility.Collapsed;
            StatusBarBorder.Visibility = Visibility.Collapsed;
            FocusModeButton.Content = "⛶ 退出专注";
            _viewModel.StatusText = "已进入专注模式 · 按 F11 或点击按钮退出";
        }
        else
        {
            RibbonRow.MinHeight = RibbonSafeMinHeight;
            RibbonTabs.Visibility = Visibility.Visible;
            RibbonSplitter.Visibility = Visibility.Visible;
            RibbonRow.Height = new GridLength(Math.Clamp(_lastRibbonHeight.Value > 0 ? _lastRibbonHeight.Value : RibbonDefaultHeight, RibbonSafeMinHeight, RibbonMaxHeight));
            SectionTabsRow.Height = new GridLength(38);
            PageHeaderRow.MinHeight = PageHeaderSafeMinHeight;
            PageHeaderRow.Height = _lastPageHeaderHeight.Value > 0 ? _lastPageHeaderHeight : new GridLength(PageHeaderDefaultHeight);
            if (_navigationVisible) { NavigationColumn.Width = _lastNavigationWidth; NavigationPanel.Visibility = Visibility.Visible; NavigationSplitter.Visibility = Visibility.Visible; }
            if (_pagesVisible) { PagesColumn.Width = _lastPagesWidth; PagesSplitter.Visibility = Visibility.Visible; }
            StatusBarBorder.Visibility = Visibility.Visible;
            FocusModeButton.Content = "⛶ 专注";
            _viewModel.StatusText = "已退出专注模式";
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.BackupService is null) { _viewModel.StatusText = "请先创建或打开本地 Vault"; return; } await CanvasView.FlushAsync();
        var dialog = new SaveFileDialog { Title = "创建 LocalNote 备份", Filter = "LocalNote 备份|*.lnbackup", FileName = $"LocalNote_{DateTime.Now:yyyyMMdd_HHmm}.lnbackup" };
        if (dialog.ShowDialog() != true) return;
        try { _viewModel.StatusText = "正在创建备份…"; await _viewModel.BackupService.CreateAsync(dialog.FileName); _viewModel.StatusText = "备份完成并通过 SHA-256 清单记录"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "备份失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var file = new OpenFileDialog { Title = "选择 LocalNote 备份", Filter = "LocalNote 备份|*.lnbackup;*.zip|所有文件|*.*" };
        if (file.ShowDialog() != true) return;
        var folder = new OpenFolderDialog { Title = "选择一个空目录作为恢复目标" }; if (folder.ShowDialog() != true) return;
        try
        {
            await CanvasView.FlushAsync(); _viewModel.StatusText = "正在校验并恢复备份…";
            await BackupService.RestoreAsync(file.FileName, folder.FolderName); await _viewModel.OpenVaultPathAsync(folder.FolderName); _viewModel.StatusText = "备份完整性校验通过，恢复完成";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Integrity_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IntegrityService is null) { _viewModel.StatusText = "请先创建或打开本地 Vault"; return; } await CanvasView.FlushAsync();
        try
        {
            var report = await _viewModel.IntegrityService.CheckAsync();
            MessageBox.Show(this, report, "仓库完整性检查", MessageBoxButton.OK, report.Contains("结论: PASS") ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "检查失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPage is null) { _viewModel.StatusText = "请先选择页面再导出 PDF"; return; } await CanvasView.FlushAsync();
        var dialog = new SaveFileDialog { Title = "导出当前页 PDF", Filter = "PDF|*.pdf", FileName = Sanitize(_viewModel.SelectedPage.Title) + ".pdf" };
        if (dialog.ShowDialog() != true) return;
        try { SimplePdfImageWriter.Write(CanvasView.RenderPageBitmap(), dialog.FileName); _viewModel.StatusText = "当前页 PDF 已导出"; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Trash_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TrashRepository is null) { _viewModel.StatusText = "请先创建或打开本地 Vault"; return; }
        var items = await _viewModel.TrashRepository.GetAsync();
        var list = new ListBox { ItemsSource = items, Margin = new Thickness(12) };
        var restore = new Button { Content = "恢复所选", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(4), Padding = new Thickness(14, 6, 14, 6) };
        var empty = new Button { Content = "清空回收站", Style = (Style)FindResource("CommandButton"), Margin = new Thickness(4), Padding = new Thickness(14, 6, 14, 6) };
        var panel = new DockPanel { Background = (Brush)FindResource("SurfaceBrush") };
        var title = new TextBlock { Text = "回收站", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 16, 16, 8) }; DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10) };
        buttons.Children.Add(restore); buttons.Children.Add(empty); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list);
        var window = new Window { Title = "LocalNote · 回收站", Owner = this, Width = 600, Height = 500, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("SurfaceBrush") };
        restore.Click += async (_, _) => { if (list.SelectedItem is TrashEntry item) { await _viewModel.TrashRepository.RestoreAsync(item); window.Close(); await _viewModel.RefreshNavigationAsync(); _viewModel.StatusText = "已按原结构恢复"; } };
        empty.Click += async (_, _) => { if (MessageBox.Show(window, "彻底清空回收站？此操作不可恢复。", "LocalNote", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { await _viewModel.TrashRepository.EmptyAsync(); window.Close(); await _viewModel.RefreshNavigationAsync(); } };
        window.ShowDialog();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFocusMode_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBoxBase) { CanvasView.SetTool(CanvasTool.Select); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.D1 && Keyboard.FocusedElement is RichTextBox) { CanvasView.ToggleParagraphTodo(); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.N && Keyboard.FocusedElement is not TextBoxBase)
        { if (_viewModel.AddPageCommand.CanExecute(null)) _viewModel.AddPageCommand.Execute(null); e.Handled = true; return; }
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt) && e.Key == Key.T)
        { _ = CanvasView.AddTableAsync(); e.Handled = true; return; }
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.D && Keyboard.FocusedElement is not TextBoxBase)
        { _ = CanvasView.DuplicateSelectedAsync(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Delete && Keyboard.FocusedElement is not TextBoxBase)
        { _ = CanvasView.DeleteSelectedAsync(); e.Handled = true; }
    }

    private async Task RestoreUiLayoutAsync()
    {
        try
        {
            var layout = await _userSettings.GetUiLayoutAsync();
            _lastRibbonHeight = new GridLength(Math.Clamp(layout.RibbonHeight, RibbonSafeMinHeight, RibbonMaxHeight));
            _lastPageHeaderHeight = new GridLength(Math.Clamp(layout.PageHeaderHeight, PageHeaderSafeMinHeight, PageHeaderMaxHeight));
            _lastNavigationWidth = new GridLength(Math.Clamp(layout.NavigationWidth, 150, 420));
            _lastPagesWidth = new GridLength(Math.Clamp(layout.PagesWidth, 180, 460));
            _navigationVisible = layout.NavigationVisible;
            _pagesVisible = layout.PagesVisible;
            RibbonRow.MinHeight = RibbonSafeMinHeight;
            RibbonRow.Height = _lastRibbonHeight;
            _ribbonCollapsed = false;
            RibbonTabs.Visibility = Visibility.Visible;
            RibbonSplitter.Visibility = Visibility.Visible;
            RibbonCompactButton.Content = "⌃ 工具";
            PageHeaderRow.Height = _lastPageHeaderHeight;
            NavigationColumn.Width = _navigationVisible ? _lastNavigationWidth : new GridLength(0);
            NavigationPanel.Visibility = _navigationVisible ? Visibility.Visible : Visibility.Collapsed;
            NavigationSplitter.Visibility = _navigationVisible ? Visibility.Visible : Visibility.Collapsed;
            PagesColumn.Width = _pagesVisible ? _lastPagesWidth : new GridLength(0);
            PagesSplitter.Visibility = _pagesVisible ? Visibility.Visible : Visibility.Collapsed;
            PagesPaneButton.Content = _pagesVisible ? "▤ 页面" : "▥ 页面";
        }
        catch { }
    }

    private async Task SaveUiLayoutAsync()
    {
        try
        {
            if (!_focusMode)
            {
                if (RibbonRow.Height.Value >= RibbonSafeMinHeight) _lastRibbonHeight = RibbonRow.Height;
                if (PageHeaderRow.Height.Value >= PageHeaderSafeMinHeight) _lastPageHeaderHeight = PageHeaderRow.Height;
                if (NavigationColumn.Width.Value > 0) _lastNavigationWidth = NavigationColumn.Width;
                if (PagesColumn.Width.Value > 0) _lastPagesWidth = PagesColumn.Width;
            }
            await _userSettings.SaveUiLayoutAsync(new UiLayoutSettings(
                _lastRibbonHeight.Value, _lastPageHeaderHeight.Value, _lastNavigationWidth.Value, _lastPagesWidth.Value,
                _navigationVisible, _pagesVisible));
        }
        catch { }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return; e.Cancel = true;
        try { await CanvasView.FlushAsync(); await SaveUiLayoutAsync(); await _viewModel.ShutdownAsync(); }
        finally { _allowClose = true; Close(); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this,
        "LocalNote V0.7.5 Simplified Core\n\nWindows x64 · 纯本地单机\nOneNote 类 Ribbon / 自由画布 / 富文本 / 上下文表格工具 / 自定义调色板 / 图片与附件 / Ink / 标签 / 搜索 / 回收站 / 本地备份恢复\n\n当前仍为 P0 功能补齐阶段，不含云同步与整库加密。",
        "关于 LocalNote", MessageBoxButton.OK, MessageBoxImage.Information);

    private bool RequirePage(string action)
    {
        if (!_viewModel.IsVaultOpen)
        {
            _viewModel.StatusText = $"{action}前，请先创建或打开本地 Vault";
            return false;
        }
        if (_viewModel.SelectedPage is null)
        {
            _viewModel.StatusText = $"{action}前，请先在左侧新建或选择一个页面";
            return false;
        }
        return true;
    }

    private void UpdateToolUi(CanvasTool tool)
    {
        Dispatcher.Invoke(() =>
        {
            SelectToolToggle.IsChecked = tool == CanvasTool.Select;
            PenToolToggle.IsChecked = tool == CanvasTool.Pen;
            PencilToolToggle.IsChecked = tool == CanvasTool.Pencil;
            HighlighterToolToggle.IsChecked = tool == CanvasTool.Highlighter;
            EraserToolToggle.IsChecked = tool == CanvasTool.Eraser;
            InkSelectToolToggle.IsChecked = tool == CanvasTool.InkSelect;
            CanvasModeText.Text = tool switch
            {
                CanvasTool.Pen => "钢笔",
                CanvasTool.Pencil => "铅笔",
                CanvasTool.Highlighter => "荧光笔",
                CanvasTool.Eraser => "橡皮擦",
                CanvasTool.InkSelect => "墨迹套索",
                CanvasTool.Shape => "形状绘制",
                _ => "选择模式"
            };
        });
    }

    private void UpdateSelectionUi(ContentObject? item)
    {
        Dispatcher.Invoke(() =>
        {
            SelectionStatusText.Text = item is null ? "未选择对象" : item.Type switch
            {
                ContentObjectType.Text => "已选择 · 文本块",
                ContentObjectType.Table => "已选择 · 表格",
                ContentObjectType.Image => "已选择 · 图片",
                ContentObjectType.Attachment => "已选择 · 附件",
                ContentObjectType.Shape => "已选择 · 形状",
                _ => "已选择对象"
            };

            var tableSelected = item?.Type == ContentObjectType.Table;
            TableToolsTab.Visibility = tableSelected ? Visibility.Visible : Visibility.Collapsed;
            if (tableSelected)
            {
                TableHeaderToggle.IsChecked = CanvasView.SelectedTableHasHeader;
                TableBandToggle.IsChecked = CanvasView.SelectedTableBandedRows;
                TableBordersToggle.IsChecked = CanvasView.SelectedTableShowBorders;
            }
            else if (TableToolsTab.IsSelected)
            {
                RibbonTabs.SelectedIndex = 0;
            }
        });
    }

    private void TableRowAbove_Click(object sender, RoutedEventArgs e) => CanvasView.TableInsertRowAbove();
    private void TableRowBelow_Click(object sender, RoutedEventArgs e) => CanvasView.TableInsertRowBelow();
    private void TableDeleteRow_Click(object sender, RoutedEventArgs e) => CanvasView.TableDeleteRow();
    private void TableColumnLeft_Click(object sender, RoutedEventArgs e) => CanvasView.TableInsertColumnLeft();
    private void TableColumnRight_Click(object sender, RoutedEventArgs e) => CanvasView.TableInsertColumnRight();
    private void TableDeleteColumn_Click(object sender, RoutedEventArgs e) => CanvasView.TableDeleteColumn();
    private void TableNarrowColumn_Click(object sender, RoutedEventArgs e) => CanvasView.TableNarrowColumn();
    private void TableWidenColumn_Click(object sender, RoutedEventArgs e) => CanvasView.TableWidenColumn();
    private void TableHeaderToggle_Click(object sender, RoutedEventArgs e) => CanvasView.TableSetHeader(TableHeaderToggle.IsChecked == true);
    private void TableBandToggle_Click(object sender, RoutedEventArgs e) => CanvasView.TableSetBandedRows(TableBandToggle.IsChecked == true);
    private void TableBordersToggle_Click(object sender, RoutedEventArgs e) => CanvasView.TableSetBorders(TableBordersToggle.IsChecked == true);
    private void TableAlignLeft_Click(object sender, RoutedEventArgs e) => CanvasView.TableSetAlignment(TextAlignment.Left);
    private void TableAlignCenter_Click(object sender, RoutedEventArgs e) => CanvasView.TableSetAlignment(TextAlignment.Center);
    private void TableAlignRight_Click(object sender, RoutedEventArgs e) => CanvasView.TableSetAlignment(TextAlignment.Right);

    private void TableStyleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TableToolsTab.Visibility != Visibility.Visible) return;
        var style = TableStyleCombo.SelectedIndex switch
        {
            1 => TableVisualStyle.OneNotePurple,
            2 => TableVisualStyle.Neutral,
            3 => TableVisualStyle.Blue,
            4 => TableVisualStyle.Minimal,
            _ => TableVisualStyle.SoftPurple
        };
        CanvasView.TableApplyStyle(style);
    }

    private void TableHeaderColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("表头底色", _tableHeaderColor, out var color)) return;
        _tableHeaderColor = color;
        TableHeaderColorPreview.Background = new SolidColorBrush(color);
        CanvasView.TableSetHeaderColor(color);
    }

    private void TableBorderColor_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPickColor("表格边框颜色", _tableBorderColor, out var color)) return;
        _tableBorderColor = color;
        TableBorderColorPreview.Background = new SolidColorBrush(color);
        CanvasView.TableSetBorderColor(color);
    }

    private async void SectionColor_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSection is null) { _viewModel.StatusText = "请先选择分区"; return; }
        Color current;
        try { current = (Color)ColorConverter.ConvertFromString(_viewModel.SelectedSection.Color)!; }
        catch { current = Color.FromRgb(108, 42, 165); }
        if (!TryPickColor("分区颜色", current, out var color)) return;
        await _viewModel.ChangeSelectedSectionColorAsync($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private bool TryPickColor(string title, Color initial, out Color color)
    {
        var dialog = new ColorPickerDialog(initial) { Owner = this, Title = title };
        if (dialog.ShowDialog() == true)
        {
            color = dialog.SelectedColor;
            return true;
        }
        color = initial;
        return false;
    }

    private void ShowOperationError(string title, Exception ex)
    {
        _viewModel.StatusText = title;
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "LocalNote_Page" : name;
    }
}
