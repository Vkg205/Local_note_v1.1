using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LocalNote.Domain.Entities;

namespace LocalNote.App.Controls;

public sealed class CanvasObjectControl : ContentControl
{
    private readonly Border _border;
    private readonly Grid _header;
    private readonly TextBlock _grip;
    private readonly Thumb _moveThumb;
    private readonly Thumb _resizeThumb;
    private readonly CheckBox _todoCheck;
    private readonly TextBlock _importantBadge;
    private bool _suppressTagEvent;
    private bool _isSelected;
    private readonly double _minWidth;
    private readonly double _minHeight;

    public CanvasObjectControl(ContentObject model, UIElement content)
    {
        Model = model;
        (_minWidth, _minHeight) = model.Type switch
        {
            ContentObjectType.Shape => (30d, 24d),
            ContentObjectType.Attachment => (180d, 70d),
            ContentObjectType.Table => (220d, 120d),
            _ => (140d, 80d)
        };
        Width = Math.Max(_minWidth, model.Width);
        Height = Math.Max(_minHeight, model.Height);
        Focusable = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _header = new Grid { Background = Brushes.Transparent };
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _moveThumb = new Thumb { Cursor = Cursors.SizeAll, Background = Brushes.Transparent, ToolTip = "拖动内容块" };
        _header.Children.Add(_moveThumb);

        _grip = new TextBlock
        {
            Text = "⠿", Foreground = new SolidColorBrush(Color.FromRgb(124, 113, 132)), FontSize = 14,
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false, Opacity = 0
        };
        _header.Children.Add(_grip);

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _importantBadge = new TextBlock { Text = "★", Foreground = new SolidColorBrush(Color.FromRgb(218, 145, 22)), FontSize = 13, Margin = new Thickness(3, 0, 5, 0) };
        _todoCheck = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), ToolTip = "待办状态" };
        _todoCheck.Checked += TodoCheck_Changed;
        _todoCheck.Unchecked += TodoCheck_Changed;
        badges.Children.Add(_importantBadge);
        badges.Children.Add(_todoCheck);
        Grid.SetColumn(badges, 1);
        _header.Children.Add(badges);
        Grid.SetRow(_header, 0);
        root.Children.Add(_header);

        var host = new Grid { Background = Brushes.Transparent, Margin = new Thickness(3, 0, 3, 3) };
        host.Children.Add(content);
        Grid.SetRow(host, 1);
        root.Children.Add(host);

        _resizeThumb = new Thumb
        {
            Width = 11, Height = 11, Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(107, 33, 168)), BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 3, 3), Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_resizeThumb, 1);
        root.Children.Add(_resizeThumb);

        var isFloating = model.Type is ContentObjectType.Text or ContentObjectType.Shape;
        _border = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = isFloating ? Brushes.Transparent : Brushes.White,
            Child = root,
            Padding = new Thickness(0),
            SnapsToDevicePixels = true,
            Effect = isFloating ? null : new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.08, Color = Colors.Black }
        };
        base.Content = _border;

        _moveThumb.DragDelta += MoveThumb_DragDelta;
        _moveThumb.DragCompleted += (_, _) => { SnapPosition(); Changed?.Invoke(this, EventArgs.Empty); };
        _resizeThumb.DragDelta += ResizeThumb_DragDelta;
        _resizeThumb.DragCompleted += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        PreviewMouseLeftButtonDown += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        MouseEnter += (_, _) => UpdateHover(true);
        MouseLeave += (_, _) => UpdateHover(false);

        RefreshTagVisuals();
        IsSelected = false;
    }

    public ContentObject Model { get; }
    public bool SnapToGrid { get; set; } = true;
    public double SnapSize { get; set; } = 8;
    public event EventHandler? Changed;
    public event EventHandler? Activated;
    public event EventHandler? TagsChanged;

    public bool IsSelected
    {
        set
        {
            _isSelected = value;
            _border.BorderBrush = value ? new SolidColorBrush(Color.FromRgb(126, 34, 206)) : Brushes.Transparent;
            _border.BorderThickness = value ? new Thickness(1.5) : new Thickness(1);
            _resizeThumb.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _header.Background = value ? new SolidColorBrush(Color.FromRgb(247, 239, 252)) : Brushes.Transparent;
            _grip.Opacity = value ? 1 : 0;
        }
    }

    public void RefreshTagVisuals()
    {
        _suppressTagEvent = true;
        _importantBadge.Visibility = Model.IsImportant ? Visibility.Visible : Visibility.Collapsed;
        _todoCheck.Visibility = Model.IsTodo ? Visibility.Visible : Visibility.Collapsed;
        _todoCheck.IsChecked = Model.TodoCompleted;
        Opacity = Model.IsTodo && Model.TodoCompleted ? 0.68 : 1.0;
        _suppressTagEvent = false;
    }

    private void UpdateHover(bool hover)
    {
        if (_isSelected) return;
        _header.Background = hover ? new SolidColorBrush(Color.FromArgb(150, 248, 245, 250)) : Brushes.Transparent;
        _grip.Opacity = hover ? 0.78 : 0;
        _border.BorderBrush = hover ? new SolidColorBrush(Color.FromRgb(226, 218, 231)) : Brushes.Transparent;
    }

    private void TodoCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressTagEvent || !Model.IsTodo) return;
        Model.TodoCompleted = _todoCheck.IsChecked == true;
        Opacity = Model.TodoCompleted ? 0.68 : 1.0;
        TagsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MoveThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        var top = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        left = Math.Max(0, left + e.HorizontalChange);
        top = Math.Max(0, top + e.VerticalChange);
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Model.X = left;
        Model.Y = top;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(_minWidth, Width + e.HorizontalChange);
        Height = Math.Max(_minHeight, Height + e.VerticalChange);
        Model.Width = Width;
        Model.Height = Height;
    }

    private void SnapPosition()
    {
        if (!SnapToGrid || SnapSize <= 0) return;
        var left = Math.Round((double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this)) / SnapSize) * SnapSize;
        var top = Math.Round((double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this)) / SnapSize) * SnapSize;
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        Model.X = left;
        Model.Y = top;
    }
}
