using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LocalNote.Domain.Entities;

namespace LocalNote.App.Controls;

/// <summary>
/// Lightweight shape hosted directly on the drawing layer (or on a content object's
/// child drawing layer). It deliberately does not use CanvasObjectControl, so drawing
/// a shape does not create another content-card layer.
/// </summary>
public sealed class DrawingShapeControl : ContentControl
{
    private readonly Border _selectionBorder;
    private readonly ShapePresenter _presenter;
    private readonly Thumb _moveThumb;
    private readonly List<Thumb> _resizeThumbs = [];
    private bool _selected;

    public DrawingShapeControl(
        ContentObject model,
        ShapeKind kind,
        Color stroke,
        Color fill,
        double thickness,
        bool startAtRight = false,
        bool startAtBottom = true)
    {
        Model = model;
        Width = Math.Max(28, model.Width);
        Height = Math.Max(24, model.Height);
        Focusable = true;

        var root = new Grid { Background = Brushes.Transparent };
        _presenter = new ShapePresenter
        {
            Kind = kind,
            StrokeColor = stroke,
            FillColor = fill,
            StrokeThickness = thickness,
            StartAtRight = startAtRight,
            StartAtBottom = startAtBottom,
            IsHitTestVisible = false
        };
        root.Children.Add(_presenter);

        _selectionBorder = new Border
        {
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        root.Children.Add(_selectionBorder);

        _moveThumb = new Thumb { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Opacity = 0.01 };
        _moveThumb.PreviewMouseLeftButtonDown += (_, _) => { Activated?.Invoke(this, EventArgs.Empty); Focus(); };
        _moveThumb.DragStarted += (_, _) => { Activated?.Invoke(this, EventArgs.Empty); ObjectManipulationStarted?.Invoke(this, EventArgs.Empty); };
        _moveThumb.DragDelta += Move_DragDelta;
        _moveThumb.DragCompleted += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        root.Children.Add(_moveThumb);

        AddResize(root, "NW", HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE);
        AddResize(root, "N", HorizontalAlignment.Center, VerticalAlignment.Top, Cursors.SizeNS, 24, 10);
        AddResize(root, "NE", HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW);
        AddResize(root, "E", HorizontalAlignment.Right, VerticalAlignment.Center, Cursors.SizeWE, 10, 24);
        AddResize(root, "SE", HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        AddResize(root, "S", HorizontalAlignment.Center, VerticalAlignment.Bottom, Cursors.SizeNS, 24, 10);
        AddResize(root, "SW", HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW);
        AddResize(root, "W", HorizontalAlignment.Left, VerticalAlignment.Center, Cursors.SizeWE, 10, 24);

        base.Content = root;
        // Select the shape but do not mark the routed mouse event handled. Thumb needs
        // the same mouse-down event in order to start DragDelta for move/resize.
        PreviewMouseLeftButtonDown += (_, _) => { Activated?.Invoke(this, EventArgs.Empty); Focus(); };
        PreviewMouseRightButtonDown += (_, _) => Activated?.Invoke(this, EventArgs.Empty);
        MouseEnter += (_, _) => UpdateVisual(true);
        MouseLeave += (_, _) => UpdateVisual(false);
        IsSelected = false;
    }

    public ContentObject Model { get; }
    public string? ParentObjectId { get; set; }
    public event EventHandler? Changed;
    public event EventHandler? Activated;
    public event EventHandler? ObjectManipulationStarted;

    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; UpdateVisual(IsMouseOver); }
    }

    public void UpdateStyle(ShapeKind kind, Color stroke, Color fill, double thickness) =>
        _presenter.Update(kind, stroke, fill, thickness);

    public void UpdateDirection(bool startAtRight, bool startAtBottom) =>
        _presenter.UpdateDirection(startAtRight, startAtBottom);

    /// <summary>
    /// Keeps child-layer shapes inside their host canvas so they cannot become clipped
    /// and impossible to select after either the shape or its parent is resized.
    /// </summary>
    public bool EnsureWithinHostBounds()
    {
        // Page-level shapes live on the expandable drawing canvas and must be free to
        // move beyond its current right/bottom edge so PageCanvasView can grow it. Only
        // embedded shapes are constrained by a text/table/image host.
        if (string.IsNullOrWhiteSpace(ParentObjectId)) return false;
        if (Parent is not Canvas host) return false;
        var hostWidth = host.ActualWidth > 0 ? host.ActualWidth : host.Width;
        var hostHeight = host.ActualHeight > 0 ? host.ActualHeight : host.Height;
        if (!double.IsFinite(hostWidth) || !double.IsFinite(hostHeight) || hostWidth <= 0 || hostHeight <= 0) return false;

        var changed = false;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var maxWidth = Math.Max(28, hostWidth);
        var maxHeight = Math.Max(24, hostHeight);
        var nextWidth = Math.Min(width, maxWidth);
        var nextHeight = Math.Min(height, maxHeight);
        if (Math.Abs(nextWidth - width) > 0.01) { Width = nextWidth; width = nextWidth; changed = true; }
        if (Math.Abs(nextHeight - height) > 0.01) { Height = nextHeight; height = nextHeight; changed = true; }

        var left = SafeLeft();
        var top = SafeTop();
        var nextLeft = Math.Clamp(left, 0, Math.Max(0, hostWidth - width));
        var nextTop = Math.Clamp(top, 0, Math.Max(0, hostHeight - height));
        if (Math.Abs(nextLeft - left) > 0.01) { Canvas.SetLeft(this, nextLeft); changed = true; }
        if (Math.Abs(nextTop - top) > 0.01) { Canvas.SetTop(this, nextTop); changed = true; }

        if (changed) SyncModel();
        return changed;
    }

    private void AddResize(Grid root, string edge, HorizontalAlignment h, VerticalAlignment v, Cursor cursor, double width = 14, double height = 14)
    {
        var thumb = new Thumb
        {
            Tag = edge,
            Width = width,
            Height = height,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Cursor = cursor,
            Background = new SolidColorBrush(Color.FromRgb(126, 34, 206)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        thumb.PreviewMouseLeftButtonDown += (_, _) => { Activated?.Invoke(this, EventArgs.Empty); Focus(); };
        thumb.DragStarted += (_, _) => { Activated?.Invoke(this, EventArgs.Empty); ObjectManipulationStarted?.Invoke(this, EventArgs.Empty); };
        thumb.DragDelta += Resize_DragDelta;
        thumb.DragCompleted += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _resizeThumbs.Add(thumb);
        root.Children.Add(thumb);
    }

    private void UpdateVisual(bool hover)
    {
        _selectionBorder.BorderBrush = _selected
            ? new SolidColorBrush(Color.FromRgb(126, 34, 206))
            : hover ? new SolidColorBrush(Color.FromRgb(205, 192, 216)) : Brushes.Transparent;
        foreach (var h in _resizeThumbs) h.Visibility = _selected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Move_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var left = SafeLeft() + e.HorizontalChange;
        var top = SafeTop() + e.VerticalChange;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var (hostWidth, hostHeight) = HostSize();

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        if (hostWidth > 0) left = Math.Min(left, Math.Max(0, hostWidth - width));
        if (hostHeight > 0) top = Math.Min(top, Math.Max(0, hostHeight - height));

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        SyncModel();
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not string edge) return;
        var left = SafeLeft();
        var top = SafeTop();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var right = left + width;
        var bottom = top + height;
        var (hostWidth, hostHeight) = HostSize();

        if (edge.Contains('E')) right += e.HorizontalChange;
        if (edge.Contains('S')) bottom += e.VerticalChange;
        if (edge.Contains('W')) left += e.HorizontalChange;
        if (edge.Contains('N')) top += e.VerticalChange;

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        if (hostWidth > 0) right = Math.Min(hostWidth, right);
        if (hostHeight > 0) bottom = Math.Min(hostHeight, bottom);

        if (right - left < 28)
        {
            if (edge.Contains('W')) left = right - 28;
            else right = left + 28;
        }
        if (bottom - top < 24)
        {
            if (edge.Contains('N')) top = bottom - 24;
            else bottom = top + 24;
        }

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        if (hostWidth > 0 && right > hostWidth) right = hostWidth;
        if (hostHeight > 0 && bottom > hostHeight) bottom = hostHeight;

        Width = Math.Max(28, right - left);
        Height = Math.Max(24, bottom - top);
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        EnsureWithinHostBounds();
        SyncModel();
    }

    private double SafeLeft()
    {
        var value = Canvas.GetLeft(this);
        return double.IsNaN(value) ? 0 : value;
    }

    private double SafeTop()
    {
        var value = Canvas.GetTop(this);
        return double.IsNaN(value) ? 0 : value;
    }

    private (double Width, double Height) HostSize()
    {
        if (string.IsNullOrWhiteSpace(ParentObjectId)) return (0, 0);
        if (Parent is not Canvas host) return (0, 0);
        var width = host.ActualWidth > 0 ? host.ActualWidth : host.Width;
        var height = host.ActualHeight > 0 ? host.ActualHeight : host.Height;
        return (
            double.IsFinite(width) && width > 0 ? width : 0,
            double.IsFinite(height) && height > 0 ? height : 0);
    }

    private void SyncModel()
    {
        Model.X = SafeLeft();
        Model.Y = SafeTop();
        Model.Width = ActualWidth > 0 ? ActualWidth : Width;
        Model.Height = ActualHeight > 0 ? ActualHeight : Height;
    }
}
