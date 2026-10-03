using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VoiceScan.App.Services;

public static class VerdictConverters
{
    private static readonly Color Match = Color.Parse("#22A06B");
    private static readonly Color Possible = Color.Parse("#E08A1E");
    private static readonly Color NoMatch = Color.Parse("#7A8794");

    public static readonly IValueConverter Foreground =
        new FuncValueConverter<object?, IBrush>(v => new SolidColorBrush(For(v)));

    public static readonly IValueConverter Background =
        new FuncValueConverter<object?, IBrush>(v => new SolidColorBrush(For(v), 0.16));

    private static Color For(object? verdict)
    {
        var text = verdict?.ToString() ?? "";
        if (text.Contains("possible", StringComparison.OrdinalIgnoreCase)) return Possible;
        if (text.Contains("no", StringComparison.OrdinalIgnoreCase)) return NoMatch;
        return text.Contains("match", StringComparison.OrdinalIgnoreCase) ? Match : NoMatch;
    }
}
