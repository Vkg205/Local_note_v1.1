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

    public DrawingShapeControl(ContentObject model, ShapeKind kind, Color stroke, Color fill, double thickness)
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
        _moveThumb.DragStarted += (_, _) => ObjectManipulationStarted?.Invoke(this, EventArgs.Empty);
        _moveThumb.DragDelta += Move_DragDelta;
        _moveThumb.DragCompleted += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        root.Children.Add(_moveThumb);

        AddResize(root, "NW", HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE);
        AddResize(root, "NE", HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW);
        AddResize(root, "SE", HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        AddResize(root, "SW", HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW);

        base.Content = root;
        PreviewMouseLeftButtonDown += (_, e) => { Activated?.Invoke(this, EventArgs.Empty); e.Handled = true; };
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

    private void AddResize(Grid root, string edge, HorizontalAlignment h, VerticalAlignment v, Cursor cursor)
    {
        var thumb = new Thumb
        {
            Tag = edge,
            Width = 14,
            Height = 14,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Cursor = cursor,
            Background = new SolidColorBrush(Color.FromRgb(126, 34, 206)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed
        };
        thumb.DragStarted += (_, _) => ObjectManipulationStarted?.Invoke(this, EventArgs.Empty);
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
        var left = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        var top = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        left = Math.Max(0, left + e.HorizontalChange);
        top = Math.Max(0, top + e.VerticalChange);
        Canvas.SetLeft(this, left); Canvas.SetTop(this, top);
        Model.X = left; Model.Y = top;
    }

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb thumb || thumb.Tag is not string edge) return;
        var left = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
        var top = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        if (edge.Contains('E')) width = Math.Max(28, width + e.HorizontalChange);
        if (edge.Contains('S')) height = Math.Max(24, height + e.VerticalChange);
        if (edge.Contains('W')) { var next = Math.Max(28, width - e.HorizontalChange); left += width - next; width = next; }
        if (edge.Contains('N')) { var next = Math.Max(24, height - e.VerticalChange); top += height - next; height = next; }

        Canvas.SetLeft(this, Math.Max(0, left)); Canvas.SetTop(this, Math.Max(0, top));
        Width = width; Height = height;
        Model.X = Canvas.GetLeft(this); Model.Y = Canvas.GetTop(this); Model.Width = width; Model.Height = height;
    }
}
