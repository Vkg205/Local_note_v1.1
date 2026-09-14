using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LocalNote.App.Dialogs;

public partial class ColorPickerDialog : Window
{
    private double _hue;
    private double _saturation = 1;
    private double _value = 1;
    private bool _updating;

    private static readonly string[] Presets =
    [
        "#202124", "#5F6368", "#9AA0A6", "#FFFFFF",
        "#6B21A8", "#7E22CE", "#A855F7", "#C084FC",
        "#2563EB", "#0EA5E9", "#14B8A6", "#16A34A",
        "#84CC16", "#EAB308", "#F59E0B", "#EA580C",
        "#DC2626", "#E11D48", "#DB2777", "#9333EA",
        "#FFF1A8", "#FFE4E6", "#DCFCE7", "#DBEAFE"
    ];

    public ColorPickerDialog(Color initialColor)
    {
        InitializeComponent();
        BuildPresets();
        SetFromColor(initialColor);
        Loaded += (_, _) => UpdateVisuals();
    }

    public Color SelectedColor { get; private set; }

    private void BuildPresets()
    {
        foreach (var hex in Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex)!;
            var button = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromRgb(210, 205, 214)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
                Tag = color
            };
            button.Click += Preset_Click;
            PresetPanel.Children.Add(button);
        }
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color color }) SetFromColor(color);
    }

    private void ColorBoard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ColorBoard.CaptureMouse();
        PickFromBoard(e.GetPosition(ColorBoard));
        e.Handled = true;
    }

    private void ColorBoard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !ColorBoard.IsMouseCaptured) return;
        PickFromBoard(e.GetPosition(ColorBoard));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (ColorBoard.IsMouseCaptured) ColorBoard.ReleaseMouseCapture();
        base.OnMouseLeftButtonUp(e);
    }

    private void PickFromBoard(Point p)
    {
        var w = Math.Max(1, ColorBoard.ActualWidth);
        var h = Math.Max(1, ColorBoard.ActualHeight);
        _saturation = Math.Clamp(p.X / w, 0, 1);
        _value = Math.Clamp(1 - p.Y / h, 0, 1);
        SelectedColor = FromHsv(_hue, _saturation, _value);
        UpdateVisuals(updateHue: false);
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _hue = e.NewValue;
        SelectedColor = FromHsv(_hue, _saturation, _value);
        UpdateVisuals(updateHue: false);
    }

    private void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyHex();
    private void HexBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHex();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ApplyHex()
    {
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            if (ColorConverter.ConvertFromString(text) is Color color) SetFromColor(Color.FromRgb(color.R, color.G, color.B));
        }
        catch
        {
            HexBox.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
        }
    }

    private void SetFromColor(Color color)
    {
        SelectedColor = Color.FromRgb(color.R, color.G, color.B);
        ToHsv(SelectedColor, out _hue, out _saturation, out _value);
        UpdateVisuals();
    }

    private void UpdateVisuals(bool updateHue = true)
    {
        _updating = true;
        try
        {
            if (updateHue) HueSlider.Value = _hue;
            HueBase.Fill = new SolidColorBrush(FromHsv(_hue, 1, 1));
            PreviewBorder.Background = new SolidColorBrush(SelectedColor);
            HexBox.Text = $"#{SelectedColor.R:X2}{SelectedColor.G:X2}{SelectedColor.B:X2}";
            RgbText.Text = $"RGB  {SelectedColor.R}, {SelectedColor.G}, {SelectedColor.B}";

            var w = ColorBoard.ActualWidth > 0 ? ColorBoard.ActualWidth : 290;
            var h = ColorBoard.ActualHeight > 0 ? ColorBoard.ActualHeight : 194;
            Canvas.SetLeft(PickerMarker, _saturation * w - PickerMarker.Width / 2);
            Canvas.SetTop(PickerMarker, (1 - _value) * h - PickerMarker.Height / 2);
        }
        finally { _updating = false; }
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        var m = value - c;
        var (r1, g1, b1) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }

    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        saturation = max == 0 ? 0 : delta / max;
        value = max;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
