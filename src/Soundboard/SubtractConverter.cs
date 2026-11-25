using System.Globalization;
using System.Windows.Data;

namespace Soundboard;

/// <summary>A number minus the converter parameter, never below zero. Used to fit a panel inside its own margins.</summary>
public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var amount = double.Parse(parameter?.ToString() ?? "0", CultureInfo.InvariantCulture);
        return value is double d ? Math.Max(0, d - amount) : value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
