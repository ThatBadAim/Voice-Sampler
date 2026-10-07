using System.Globalization;

namespace VoiceScan.App.Core.Models;

public static class TimeFormat
{
    /// <summary>"m:ss" under an hour, "h:mm:ss" from one hour up, so multi-hour recordings never wrap.</summary>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
        return t.TotalHours >= 1.0
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes:00}:{t.Seconds:00}");
    }

    /// <summary>Parses "s", "m:ss" or "h:mm:ss" (seconds may have decimals) into seconds.</summary>
    public static bool TryParseClock(string? text, out double seconds)
    {
        seconds = 0.0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        double total = 0.0;
        string[] parts = text.Trim().Split(':');
        if (parts.Length > 3) return false;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0) return false;
            if (i < parts.Length - 1 && value != Math.Floor(value)) return false;
            total = total * 60.0 + value;
        }
        seconds = total;
        return true;
    }
}
