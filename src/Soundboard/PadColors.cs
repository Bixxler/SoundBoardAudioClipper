using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Soundboard;

/// <summary>
/// Gives each pad its own colour, picked from its file path so it stays the same across restarts,
/// renames and reordering. The palette is muted enough for white text on every colour.
/// </summary>
public sealed class PadColorConverter : IValueConverter
{
    private static readonly Brush[] Palette =
    [
        Frozen(0x4F, 0x5B, 0xD5), // indigo
        Frozen(0x1F, 0x85, 0x78), // teal
        Frozen(0x7A, 0x4F, 0xD0), // violet
        Frozen(0xB8, 0x3B, 0x67), // rose
        Frozen(0x2F, 0x73, 0xC9), // blue
        Frozen(0x9A, 0x5B, 0x13), // amber
        Frozen(0x3B, 0x82, 0x3B), // green
        Frozen(0xA8, 0x44, 0x3A), // brick
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // FNV-1a: string.GetHashCode is randomised per process, so it would reshuffle colours on every launch.
        var hash = 2166136261u;
        foreach (var c in (value as string ?? "").ToUpperInvariant()) hash = (hash ^ c) * 16777619u;
        return Palette[hash % (uint)Palette.Length];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
