using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LocalNote.App.Controls;

public enum TableVisualStyle
{
    SoftPurple,
    OneNotePurple,
    Neutral,
    Blue,
    Minimal
}

public sealed class TableEditorControl : UserControl
{
    public sealed class TableData
    {
        // Rows includes the header row when HasHeader is true.
        public int Rows { get; set; } = 4;
        public int Columns { get; set; } = 3;
        public bool HasHeader { get; set; } = true;
        public bool BandedRows { get; set; }
        public bool ShowBorders { get; set; } = true;
        public string HeaderBackground { get; set; } = "#F0E5F8";
        public string HeaderForeground { get; set; } = "#4A1764";
        public string BodyBackground { get; set; } = "#FFFFFF";
        public string AlternateBackground { get; set; } = "#FAF7FC";
        public string BorderColor { get; set; } = "#DDD6E1";
        public string TextAlignment { get; set; } = "Left";
        public List<double> ColumnWidths { get; set; } = [];
        public List<List<string>> Cells { get; set; } = [];
    }

    private readonly Grid _cells = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<(int Row, int Column), TextBox> _editors = new();
    private TableData _data;
    private bool _building;
    private int _activeRow;
    private int _activeColumn;

    public TableEditorControl(string payload)
    {
        _data = Parse(payload);
        Normalize();
        _activeRow = _data.HasHeader && _data.Rows > 1 ? 1 : 0;
        _activeColumn = 0;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ContentChanged?.Invoke(this, EventArgs.Empty); };

        var root = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false,
                Content = _cells
            }
        };
        Content = root;
        Rebuild();
    }

    public event EventHandler? ContentChanged;
    public event EventHandler? ActiveCellChanged;
    public event Action<string>? StatusMessage;
    public string Payload => JsonSerializer.Serialize(_data);
    public string SearchText => string.Join(" ", _data.Cells.SelectMany(x => x).Where(x => !string.IsNullOrWhiteSpace(x)));
    public int ActiveRow => _activeRow;
    public int ActiveColumn => _activeColumn;
    public bool HasHeader => _data.HasHeader;
    public bool BandedRows => _data.BandedRows;
    public bool ShowBorders => _data.ShowBorders;

    public void InsertRowAbove()
    {
        var index = Math.Clamp(_activeRow, 0, _data.Rows);
        if (_data.HasHeader) index = Math.Max(1, index);
        InsertRow(index);
    }

    public void InsertRowBelow()
    {
        var index = Math.Clamp(_activeRow + 1, 0, _data.Rows);
        if (_data.HasHeader) index = Math.Max(1, index);
        InsertRow(index);
    }

    public void DeleteActiveRow()
    {
        var minimum = _data.HasHeader ? 2 : 1;
        if (_data.Rows <= minimum) { StatusMessage?.Invoke("表格至少需要保留一行数据"); return; }
        if (_data.HasHeader && _activeRow == 0) { StatusMessage?.Invoke("表头名称行不能作为数据行删除；可在表格工具中关闭表头"); return; } // header is structural, not a data row.
        var index = Math.Clamp(_activeRow, 0, _data.Rows - 1);
        _data.Cells.RemoveAt(index);
        _data.Rows--;
        _activeRow = Math.Clamp(index, 0, _data.Rows - 1);
        RebuildAndSave(focus: true);
    }

    public void InsertColumnLeft()
    {
        var index = Math.Clamp(_activeColumn, 0, _data.Columns);
        InsertColumn(index);
    }

    public void InsertColumnRight()
    {
        var index = Math.Clamp(_activeColumn + 1, 0, _data.Columns);
        InsertColumn(index);
    }

    public void DeleteActiveColumn()
    {
        if (_data.Columns <= 1) { StatusMessage?.Invoke("表格至少需要保留一列"); return; }
        var index = Math.Clamp(_activeColumn, 0, _data.Columns - 1);
        foreach (var row in _data.Cells) row.RemoveAt(index);
        if (_data.ColumnWidths.Count > index) _data.ColumnWidths.RemoveAt(index);
        _data.Columns--;
        _activeColumn = Math.Clamp(index, 0, _data.Columns - 1);
        RebuildAndSave(focus: true);
    }

    public void SetHeaderEnabled(bool enabled)
    {
        if (_data.HasHeader == enabled) return;
        _data.HasHeader = enabled;
        if (enabled)
        {
            var header = Enumerable.Range(1, _data.Columns).Select(i => $"列 {i}").ToList();
            _data.Cells.Insert(0, header);
            _data.Rows++;
            _activeRow++;
        }
        else if (_data.Rows > 1)
        {
            _data.Cells.RemoveAt(0);
            _data.Rows--;
            _activeRow = Math.Max(0, _activeRow - 1);
        }
        RebuildAndSave();
    }

    public void SetBandedRows(bool enabled)
    {
        _data.BandedRows = enabled;
        RebuildAndSave(focus: true);
    }

    public void SetBordersVisible(bool visible)
    {
        _data.ShowBorders = visible;
        RebuildAndSave(focus: true);
    }

    public void SetHeaderBackground(Color color)
    {
        _data.HeaderBackground = ToHex(color);
        RebuildAndSave(focus: true);
    }

    public void SetBorderColor(Color color)
    {
        _data.BorderColor = ToHex(color);
        RebuildAndSave(focus: true);
    }

    public void SetTextAlignment(TextAlignment alignment)
    {
        _data.TextAlignment = alignment.ToString();
        RebuildAndSave(focus: true);
    }

    public void AdjustActiveColumnWidth(double delta)
    {
        Normalize();
        var index = Math.Clamp(_activeColumn, 0, _data.Columns - 1);
        _data.ColumnWidths[index] = Math.Clamp(_data.ColumnWidths[index] + delta, 72, 420);
        RebuildAndSave(focus: true);
    }

    public void ApplyStyle(TableVisualStyle style)
    {
        switch (style)
        {
            case TableVisualStyle.OneNotePurple:
                _data.HeaderBackground = "#6B21A8";
                _data.HeaderForeground = "#FFFFFF";
                _data.BodyBackground = "#FFFFFF";
                _data.AlternateBackground = "#F8F3FB";
                _data.BorderColor = "#DED5E4";
                _data.ShowBorders = true;
                _data.BandedRows = false;
                break;
            case TableVisualStyle.Neutral:
                _data.HeaderBackground = "#F3F4F6";
                _data.HeaderForeground = "#242424";
                _data.BodyBackground = "#FFFFFF";
                _data.AlternateBackground = "#F8F9FA";
                _data.BorderColor = "#D8DADC";
                _data.ShowBorders = true;
                _data.BandedRows = false;
                break;
            case TableVisualStyle.Blue:
                _data.HeaderBackground = "#DCEBFF";
                _data.HeaderForeground = "#163A63";
                _data.BodyBackground = "#FFFFFF";
                _data.AlternateBackground = "#F3F8FF";
                _data.BorderColor = "#C9D8EC";
                _data.ShowBorders = true;
                _data.BandedRows = true;
                break;
            case TableVisualStyle.Minimal:
                _data.HeaderBackground = "#FFFFFF";
                _data.HeaderForeground = "#25222A";
                _data.BodyBackground = "#FFFFFF";
                _data.AlternateBackground = "#FFFFFF";
                _data.BorderColor = "#E8E4EA";
                _data.ShowBorders = false;
                _data.BandedRows = false;
                break;
            default:
                _data.HeaderBackground = "#F0E5F8";
                _data.HeaderForeground = "#4A1764";
                _data.BodyBackground = "#FFFFFF";
                _data.AlternateBackground = "#FAF7FC";
                _data.BorderColor = "#DDD6E1";
                _data.ShowBorders = true;
                _data.BandedRows = false;
                break;
        }
        RebuildAndSave(focus: true);
    }

    private bool InsertRow(int index)
    {
        if (_data.Rows >= 50)
        {
            StatusMessage?.Invoke("表格最多支持 50 行");
            return false;
        }
        _data.Cells.Insert(index, Enumerable.Repeat(string.Empty, _data.Columns).ToList());
        _data.Rows++;
        _activeRow = index;
        RebuildAndSave(focus: true);
        return true;
    }

    private bool InsertColumn(int index)
    {
        if (_data.Columns >= 20)
        {
            StatusMessage?.Invoke("表格最多支持 20 列");
            return false;
        }
        for (var row = 0; row < _data.Cells.Count; row++)
        {
            var defaultText = _data.HasHeader && row == 0 ? $"列 {_data.Columns + 1}" : string.Empty;
            _data.Cells[row].Insert(index, defaultText);
        }
        _data.ColumnWidths.Insert(Math.Min(index, _data.ColumnWidths.Count), 146);
        _data.Columns++;
        RenameDefaultHeaders();
        _activeColumn = index;
        RebuildAndSave(focus: true);
        return true;
    }

    private void RebuildAndSave(bool focus = false)
    {
        Rebuild();
        QueueSave();
        if (focus) Dispatcher.BeginInvoke(() => FocusCell(_activeRow, _activeColumn), DispatcherPriority.Input);
    }

    private void Rebuild()
    {
        _building = true;
        Normalize();
        _editors.Clear();
        _cells.Children.Clear();
        _cells.RowDefinitions.Clear();
        _cells.ColumnDefinitions.Clear();

        for (var r = 0; r < _data.Rows; r++) _cells.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < _data.Columns; c++) _cells.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_data.ColumnWidths[c]) });

        var borderBrush = BrushFrom(_data.BorderColor, Color.FromRgb(221, 214, 225));
        var headerBrush = BrushFrom(_data.HeaderBackground, Color.FromRgb(240, 229, 248));
        var headerForeground = BrushFrom(_data.HeaderForeground, Color.FromRgb(74, 23, 100));
        var bodyBrush = BrushFrom(_data.BodyBackground, Colors.White);
        var alternateBrush = BrushFrom(_data.AlternateBackground, Color.FromRgb(250, 247, 252));
        var alignment = Enum.TryParse<TextAlignment>(_data.TextAlignment, out var parsed) ? parsed : TextAlignment.Left;

        for (var r = 0; r < _data.Rows; r++)
        for (var c = 0; c < _data.Columns; c++)
        {
            var rr = r;
            var cc = c;
            var isHeader = _data.HasHeader && r == 0;
            var isBand = _data.BandedRows && !isHeader && ((r - (_data.HasHeader ? 1 : 0)) % 2 == 1);
            var box = new TextBox
            {
                Text = _data.Cells[r][c],
                BorderBrush = borderBrush,
                BorderThickness = _data.ShowBorders ? new Thickness(0.6) : new Thickness(0),
                Background = isHeader ? headerBrush : isBand ? alternateBrush : bodyBrush,
                Foreground = isHeader ? headerForeground : new SolidColorBrush(Color.FromRgb(37, 34, 42)),
                FontWeight = isHeader ? FontWeights.SemiBold : FontWeights.Normal,
                Padding = isHeader ? new Thickness(10, 8, 10, 8) : new Thickness(10, 7, 10, 7),
                MinHeight = isHeader ? 38 : 36,
                AcceptsReturn = true,
                AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = alignment,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0),
                Tag = (rr, cc)
            };
            box.TextChanged += (_, _) =>
            {
                if (_building) return;
                _data.Cells[rr][cc] = box.Text;
                QueueSave();
            };
            box.GotKeyboardFocus += (_, _) =>
            {
                _activeRow = rr;
                _activeColumn = cc;
                ActiveCellChanged?.Invoke(this, EventArgs.Empty);
            };
            box.PreviewKeyDown += Cell_PreviewKeyDown;
            box.ContextMenu = BuildCellContextMenu(rr, cc);
            _editors[(r, c)] = box;
            Grid.SetRow(box, r);
            Grid.SetColumn(box, c);
            _cells.Children.Add(box);
        }
        _building = false;
    }

    private ContextMenu BuildCellContextMenu(int row, int col)
    {
        var menu = new ContextMenu();
        var addAbove = new MenuItem { Header = "在上方插入行" };
        var addBelow = new MenuItem { Header = "在下方插入行" };
        var addLeft = new MenuItem { Header = "在左侧插入列" };
        var addRight = new MenuItem { Header = "在右侧插入列" };
        var deleteRow = new MenuItem { Header = "删除当前行", IsEnabled = !(_data.HasHeader && row == 0) };
        var deleteColumn = new MenuItem { Header = "删除当前列", IsEnabled = _data.Columns > 1 };
        addAbove.Click += (_, _) => { _activeRow = row; _activeColumn = col; InsertRowAbove(); };
        addBelow.Click += (_, _) => { _activeRow = row; _activeColumn = col; InsertRowBelow(); };
        addLeft.Click += (_, _) => { _activeRow = row; _activeColumn = col; InsertColumnLeft(); };
        addRight.Click += (_, _) => { _activeRow = row; _activeColumn = col; InsertColumnRight(); };
        deleteRow.Click += (_, _) => { _activeRow = row; _activeColumn = col; DeleteActiveRow(); };
        deleteColumn.Click += (_, _) => { _activeRow = row; _activeColumn = col; DeleteActiveColumn(); };
        menu.Items.Add(addAbove);
        menu.Items.Add(addBelow);
        menu.Items.Add(new Separator());
        menu.Items.Add(addLeft);
        menu.Items.Add(addRight);
        menu.Items.Add(new Separator());
        menu.Items.Add(deleteRow);
        menu.Items.Add(deleteColumn);
        return menu;
    }

    private void Cell_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || sender is not TextBox box || box.Tag is not ValueTuple<int, int> pos) return;
        _activeRow = pos.Item1;
        _activeColumn = pos.Item2;
        var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (backwards)
        {
            var c = _activeColumn - 1;
            var r = _activeRow;
            if (c < 0) { r--; c = _data.Columns - 1; }
            if (r >= 0) FocusCell(r, c);
        }
        else
        {
            var c = _activeColumn + 1;
            var r = _activeRow;
            if (c >= _data.Columns) { r++; c = 0; }
            if (r >= _data.Rows)
            {
                _activeRow = _data.Rows - 1;
                _activeColumn = _data.Columns - 1;
                if (!InsertRow(_data.Rows))
                {
                    Dispatcher.BeginInvoke(() => FocusCell(_activeRow, _activeColumn), DispatcherPriority.Input);
                    e.Handled = true;
                    return;
                }
                r = _data.Rows - 1;
                c = 0;
            }
            Dispatcher.BeginInvoke(() => FocusCell(r, c), DispatcherPriority.Input);
        }
        e.Handled = true;
    }

    private void FocusCell(int row, int col)
    {
        row = Math.Clamp(row, 0, Math.Max(0, _data.Rows - 1));
        col = Math.Clamp(col, 0, Math.Max(0, _data.Columns - 1));
        if (_editors.TryGetValue((row, col), out var editor))
        {
            editor.Focus();
            editor.CaretIndex = editor.Text.Length;
            _activeRow = row;
            _activeColumn = col;
        }
    }

    private void RenameDefaultHeaders()
    {
        if (!_data.HasHeader || _data.Cells.Count == 0) return;
        var header = _data.Cells[0];
        for (var c = 0; c < _data.Columns; c++)
        {
            if (string.IsNullOrWhiteSpace(header[c]) || header[c].StartsWith("列 ", StringComparison.Ordinal)) header[c] = $"列 {c + 1}";
        }
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Normalize()
    {
        _data.Columns = Math.Clamp(_data.Columns, 1, 20);
        _data.Rows = Math.Clamp(_data.Rows, _data.HasHeader ? 2 : 1, 50);
        while (_data.Cells.Count < _data.Rows) _data.Cells.Add([]);
        if (_data.Cells.Count > _data.Rows) _data.Cells.RemoveRange(_data.Rows, _data.Cells.Count - _data.Rows);
        foreach (var row in _data.Cells)
        {
            while (row.Count < _data.Columns) row.Add(string.Empty);
            if (row.Count > _data.Columns) row.RemoveRange(_data.Columns, row.Count - _data.Columns);
        }
        while (_data.ColumnWidths.Count < _data.Columns) _data.ColumnWidths.Add(146);
        if (_data.ColumnWidths.Count > _data.Columns) _data.ColumnWidths.RemoveRange(_data.Columns, _data.ColumnWidths.Count - _data.Columns);
        for (var i = 0; i < _data.ColumnWidths.Count; i++) _data.ColumnWidths[i] = Math.Clamp(_data.ColumnWidths[i], 72, 420);
        if (_data.HasHeader) RenameDefaultHeaders();
    }

    private static TableData Parse(string payload)
    {
        try
        {
            var data = JsonSerializer.Deserialize<TableData>(payload);
            if (data is not null)
            {
                // Backward compatibility: V0.3/V0.4 tables had no header metadata.
                if (data.Cells.Count > 0 && data.Rows <= 3 && data.Cells[0].All(string.IsNullOrWhiteSpace))
                {
                    data.HasHeader = true;
                    data.Cells.Insert(0, Enumerable.Range(1, Math.Max(1, data.Columns)).Select(i => $"列 {i}").ToList());
                    data.Rows = data.Cells.Count;
                }
                return data;
            }
        }
        catch { }
        return CreateDefaultData();
    }

    public static TableData CreateDefaultData(int dataRows = 3, int columns = 3)
    {
        columns = Math.Clamp(columns, 1, 20);
        dataRows = Math.Clamp(dataRows, 1, 49);
        var cells = new List<List<string>>
        {
            Enumerable.Range(1, columns).Select(i => $"列 {i}").ToList()
        };
        for (var i = 0; i < dataRows; i++) cells.Add(Enumerable.Repeat(string.Empty, columns).ToList());
        return new TableData { Rows = dataRows + 1, Columns = columns, HasHeader = true, Cells = cells };
    }

    private static SolidColorBrush BrushFrom(string? value, Color fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value ?? string.Empty)!); }
        catch { return new SolidColorBrush(fallback); }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
