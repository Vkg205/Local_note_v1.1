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
    private readonly Grid _root;
    private readonly Grid _interactionLayer;
    private readonly TextBlock _importantBadge;
    private readonly List<Thumb> _resizeThumbs = [];
    private readonly List<Thumb> _moveThumbs = [];
    private bool _isSelected;
    private bool _hover;
    private readonly double _minWidth;
    private readonly double _minHeight;
    private readonly bool _autoHeight;

    public CanvasObjectControl(ContentObject model, UIElement content)
    {
        Model = model;
        (_minWidth, _minHeight) = model.Type switch
        {
            ContentObjectType.Attachment => (180d, 64d),
            ContentObjectType.Table => (220d, 120d),
            _ => (140d, 56d)
        };
        _autoHeight = model.Type == ContentObjectType.Text;
        Width = Math.Max(_minWidth, model.Width);
        if (!_autoHeight) Height = Math.Max(_minHeight, model.Height);
        MinHeight = _minHeight;
        Focusable = true;

        _root = new Grid { Background = Brushes.Transparent };
        _root.Children.Add(content);

        // Child drawing layer. Shapes created while the pointer starts inside this
        // content object are hosted here and therefore move with the parent object.
        OverlayLayer = new Canvas
        {
            Background = null,
            IsHitTestVisible = false,
            ClipToBounds = true
        };
        _root.Children.Add(OverlayLayer);

        _interactionLayer = new Grid { Background = null };
        _root.Children.Add(_interactionLayer);

        _importantBadge = new TextBlock
        {
            Text = "★",
            Foreground = new SolidColorBrush(Color.FromRgb(218, 145, 22)),
            FontSize = 13,
            Margin = new Thickness(0, 4, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _interactionLayer.Children.Add(_importantBadge);

        AddMoveThumbs();
        AddResizeThumbs();

        var isFloating = model.Type == ContentObjectType.Text;
        _border = new Border
        {
            CornerRadius = new CornerRadius(isFloating ? 4 : 8),
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,
            Background = isFloating ? Brushes.Transparent : Brushes.White,
            Child = _root,
            Padding = new Thickness(0),
            SnapsToDevicePixels = true,
            Effect = isFloating ? null : new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.08, Color = Colors.Black }
        };
        base.Content = _border;

        PreviewMouseLeftButtonDown += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        MouseEnter += (_, _) => { _hover = true; UpdateVisualState(); };
        MouseLeave += (_, _) => { _hover = false; UpdateVisualState(); };
        SizeChanged += (_, _) =>
        {
            if (_autoHeight && ActualHeight > 0)
            {
                Model.Width = ActualWidth > 0 ? ActualWidth : Width;
                Model.Height = Math.Max(_minHeight, ActualHeight);
            }
            // OverlayLayer stretches with the host, but explicitly tracking the actual
            // size keeps embedded shapes predictable after text auto-grow/shrink and
            // manual table/image resize.
            if (ActualWidth > 0) OverlayLayer.Width = ActualWidth;
            if (ActualHeight > 0) OverlayLayer.Height = ActualHeight;
            HostSizeChanged?.Invoke(this, EventArgs.Empty);
        };

        RefreshTagVisuals();
        IsSelected = false;
    }

    public ContentObject Model { get; }
    public Canvas OverlayLayer { get; }
    public bool SnapToGrid { get; set; } = false;
    public double SnapSize { get; set; } = 8;
    public event EventHandler? Changed;
    public event EventHandler? Activated;
    public event EventHandler? ObjectManipulationStarted;
    public event EventHandler? HostSizeChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; UpdateVisualState(); }
    }

    public void RefreshTagVisuals()
    {
        _importantBadge.Visibility = Model.IsImportant ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddMoveThumbs()
    {
        // Thin edge strips provide OneNote-like direct movement without reserving a
        // permanent 24px header inside every content object.
        _moveThumbs.Add(CreateMoveThumb(HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, 9, Cursors.SizeAll));
        _moveThumbs.Add(CreateMoveThumb(HorizontalAlignment.Left, VerticalAlignment.Stretch, 7, double.NaN, Cursors.SizeAll));
        _moveThumbs.Add(CreateMoveThumb(HorizontalAlignment.Right, VerticalAlignment.Stretch, 7, double.NaN, Cursors.SizeAll));
        foreach (var thumb in _moveThumbs) _interactionLayer.Children.Add(thumb);
    }

    private Thumb CreateMoveThumb(HorizontalAlignment h, VerticalAlignment v, double width, double height, Cursor cursor)
    {
        var thumb = new Thumb
        {
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Background = Brushes.Transparent,
            Cursor = cursor,
            ToolTip = "拖动内容块",
            Opacity = 1
        };
        if (!double.IsNaN(width)) thumb.Width = width;
        if (!double.IsNaN(height)) thumb.Height = height;
        thumb.DragStarted += (_, _) => ObjectManipulationStarted?.Invoke(this, EventArgs.Empty);
        thumb.DragDelta += MoveThumb_DragDelta;
        thumb.DragCompleted += (_, _) => { SnapPosition(); Changed?.Invoke(this, EventArgs.Empty); };
        return thumb;
    }

    private void AddResizeThumbs()
    {
        AddResizeHandle("NW", HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE);
        AddResizeHandle("N", HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.SizeNS, 24, 8);
        AddResizeHandle("NE", HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW);
        AddResizeHandle("E", HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE, 8, 24);
        AddResizeHandle("SE", HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        AddResizeHandle("S", HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS, 24, 8);
        AddResizeHandle("SW", HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW);
        AddResizeHandle("W", HorizontalAlignment.Left, VerticalAlignment.Center, Cursors.SizeWE, 8, 24);
    }

    private void AddResizeHandle(string edge, HorizontalAlignment h, VerticalAlignment v, Cursor cursor, double width = 14, double height = 14)
    {
        // Text containers auto-grow vertically; their N/S handles are intentionally
        // hidden so users resize width while content controls height naturally.
        if (_autoHeight && edge is "N" or "S" or "NW" or "NE" or "SW" or "SE")
            return;
        var thumb = new Thumb
        {
            Tag = edge,
            Width = width,
            Height = height,
            Cursor = cursor,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Background = new SolidColorBrush(Color.FromRgb(126, 34, 206)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        thumb.DragStarted += (_, _) => ObjectManipulationStarted?.Invoke(this, EventArgs.Empty);
        thumb.DragDelta += ResizeThumb_DragDelta;
        thumb.DragCompleted += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _resizeThumbs.Add(thumb);
        _interactionLayer.Children.Add(thumb);
    }

    private void UpdateVisualState()
    {
        var accent = new SolidColorBrush(Color.FromRgb(126, 34, 206));
        var hover = new SolidColorBrush(Color.FromRgb(223, 214, 230));
        _border.BorderBrush = _isSelected ? accent : _hover ? hover : Brushes.Transparent;
        _border.BorderThickness = _isSelected ? new Thickness(1.5) : new Thickness(1);
        foreach (var thumb in _resizeThumbs) thumb.Visibility = _isSelected ? Visibility.Visible : Visibility.Collapsed;
        foreach (var thumb in _moveThumbs) thumb.Opacity = _isSelected || _hover ? 1 : 0.18;
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
        if (sender is not Thumb thumb || thumb.Tag is not string edge) return;
        var left = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        var top = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        if (edge.Contains('E')) width = Math.Max(_minWidth, width + e.HorizontalChange);
        if (edge.Contains('S') && !_autoHeight) height = Math.Max(_minHeight, height + e.VerticalChange);
        if (edge.Contains('W'))
        {
            var next = Math.Max(_minWidth, width - e.HorizontalChange);
            left += width - next;
            width = next;
        }
        if (edge.Contains('N') && !_autoHeight)
        {
            var next = Math.Max(_minHeight, height - e.VerticalChange);
            top += height - next;
            height = next;
        }

        Canvas.SetLeft(this, Math.Max(0, left));
        Canvas.SetTop(this, Math.Max(0, top));
        Width = width;
        if (!_autoHeight) Height = height;
        Model.X = Canvas.GetLeft(this);
        Model.Y = Canvas.GetTop(this);
        Model.Width = width;
        Model.Height = _autoHeight ? Math.Max(_minHeight, ActualHeight) : height;
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
