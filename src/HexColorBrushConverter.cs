using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LocalNote.App.Converters;

public sealed class HexColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string text && ColorConverter.ConvertFromString(text) is Color color) return new SolidColorBrush(color);
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(108, 42, 165));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
