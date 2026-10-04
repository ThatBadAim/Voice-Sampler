using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VoiceScan.App.Services;

public static class VerdictConverters
{
    private static readonly Color Match = Color.Parse("#4E9F7D");
    private static readonly Color Possible = Color.Parse("#C9923A");
    private static readonly Color NoMatch = Color.Parse("#8A857C");
    private static readonly Color Error = Color.Parse("#C4584E");

    public static readonly IValueConverter Foreground =
        new FuncValueConverter<object?, IBrush>(v => new SolidColorBrush(For(v)));

    public static readonly IValueConverter Background =
        new FuncValueConverter<object?, IBrush>(v => new SolidColorBrush(For(v), 0.14));

    private static Color For(object? verdict)
    {
        var text = verdict?.ToString() ?? "";
        if (text.Contains("error", StringComparison.OrdinalIgnoreCase)) return Error;
        if (text.Contains("possible", StringComparison.OrdinalIgnoreCase)) return Possible;
        if (text.Contains("no", StringComparison.OrdinalIgnoreCase)) return NoMatch;
        return text.Contains("match", StringComparison.OrdinalIgnoreCase) ? Match : NoMatch;
    }
}
