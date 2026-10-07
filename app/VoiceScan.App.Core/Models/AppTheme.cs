namespace VoiceScan.App.Core.Models;

/// <summary>A selectable colour theme. <see cref="Id"/> matches a theme dictionary key in Theme.axaml.</summary>
public sealed record AppTheme(string Id, string Name, bool IsDark, string BackgroundHex, string AccentHex)
{
    public static IReadOnlyList<AppTheme> All { get; } =
    [
        new("Dark", "Ember", true, "#151412", "#D2704F"),
        new("Light", "Ember Light", false, "#F6F3EE", "#B5502E"),
        new("Ocean", "Ocean", true, "#0F151C", "#4FA3D2"),
        new("Forest", "Forest", true, "#121611", "#6BB07A"),
        new("Violet", "Violet", true, "#16131C", "#A483DE"),
        new("Graphite", "Graphite", true, "#121212", "#6F7C8C"),
        new("Mist", "Mist", false, "#F1F4F7", "#2F6DB0"),
    ];

    public const string DefaultId = "Dark";

    public static AppTheme Find(string? id) =>
        All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
