using System.Windows;
using System.Windows.Media;

namespace LocalNote.App.Controls;

public enum ShapeKind
{
    Line,
    Arrow,
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Triangle,
    Diamond,
    Flowchart
}

public sealed class ShapePresenter : FrameworkElement
{
    public ShapeKind Kind { get; set; } = ShapeKind.Rectangle;
    public Color StrokeColor { get; set; } = Color.FromRgb(108, 42, 165);
    public Color FillColor { get; set; } = Color.FromArgb(24, 108, 42, 165);
    public double StrokeThickness { get; set; } = 2.0;

    // Line/arrow need to remember the direction in which the user dragged.
    // Old documents did not persist this metadata; their legacy rendering was
    // bottom-left -> top-right, which is kept as the default.
    public bool StartAtRight { get; set; }
    public bool StartAtBottom { get; set; } = true;

    public void Update(ShapeKind kind, Color stroke, Color fill, double thickness)
    {
        Kind = kind;
        StrokeColor = stroke;
        FillColor = fill;
        StrokeThickness = Math.Clamp(thickness, 0.5, 16);
        InvalidateVisual();
    }

    public void UpdateDirection(bool startAtRight, bool startAtBottom)
    {
        StartAtRight = startAtRight;
        StartAtBottom = startAtBottom;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = Math.Max(1, ActualWidth);
        var h = Math.Max(1, ActualHeight);
        var t = Math.Max(0.5, StrokeThickness);
        var pen = new Pen(new SolidColorBrush(StrokeColor), t)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var fill = new SolidColorBrush(FillColor);
        var pad = Math.Max(4, t + 2);
        var rect = new Rect(pad, pad, Math.Max(1, w - pad * 2), Math.Max(1, h - pad * 2));

        var lineStart = new Point(StartAtRight ? w - pad : pad, StartAtBottom ? h - pad : pad);
        var lineEnd = new Point(StartAtRight ? pad : w - pad, StartAtBottom ? pad : h - pad);

        switch (Kind)
        {
            case ShapeKind.Line:
                dc.DrawLine(pen, lineStart, lineEnd);
                break;
            case ShapeKind.Arrow:
                DrawArrow(dc, pen, lineStart, lineEnd);
                break;
            case ShapeKind.Rectangle:
                dc.DrawRectangle(fill, pen, rect);
                break;
            case ShapeKind.RoundedRectangle:
                dc.DrawRoundedRectangle(fill, pen, rect, 14, 14);
                break;
            case ShapeKind.Ellipse:
                dc.DrawEllipse(fill, pen, new Point(w / 2, h / 2), rect.Width / 2, rect.Height / 2);
                break;
            case ShapeKind.Triangle:
                dc.DrawGeometry(fill, pen, PolygonGeometry(new[]
                {
                    new Point(w / 2, pad), new Point(w - pad, h - pad), new Point(pad, h - pad)
                }));
                break;
            case ShapeKind.Diamond:
                dc.DrawGeometry(fill, pen, PolygonGeometry(new[]
                {
                    new Point(w / 2, pad), new Point(w - pad, h / 2), new Point(w / 2, h - pad), new Point(pad, h / 2)
                }));
                break;
            case ShapeKind.Flowchart:
                dc.DrawRoundedRectangle(fill, pen, rect, 10, 10);
                dc.DrawLine(
                    new Pen(new SolidColorBrush(Color.FromArgb(120, StrokeColor.R, StrokeColor.G, StrokeColor.B)), Math.Max(1, t * 0.7)),
                    new Point(rect.Left + rect.Width * 0.08, rect.Top + rect.Height * 0.28),
                    new Point(rect.Right - rect.Width * 0.08, rect.Top + rect.Height * 0.28));
                break;
        }
    }

    private static StreamGeometry PolygonGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        ctx.BeginFigure(points[0], true, true);
        ctx.PolyLineTo(points.Skip(1).ToArray(), true, true);
        geometry.Freeze();
        return geometry;
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Point start, Point end)
    {
        dc.DrawLine(pen, start, end);
        var vector = start - end;
        if (vector.Length < 1) return;
        vector.Normalize();
        var normal = new Vector(-vector.Y, vector.X);
        var arrowLength = Math.Max(10, Math.Min(24, pen.Thickness * 4 + 10));
        var p1 = end + vector * arrowLength + normal * arrowLength * 0.45;
        var p2 = end + vector * arrowLength - normal * arrowLength * 0.45;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(pen.Brush, null, geo);
    }
}
