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
}
