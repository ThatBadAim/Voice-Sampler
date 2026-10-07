using Avalonia.Styling;
using VoiceScan.App.Core.Models;

namespace VoiceScan.App.Styles;

/// <summary>Custom theme variants; each inherits Fluent's control colours from the base variant it derives from.</summary>
public static class AppThemeVariants
{
    public static readonly ThemeVariant Ocean = new("Ocean", ThemeVariant.Dark);
    public static readonly ThemeVariant Forest = new("Forest", ThemeVariant.Dark);
    public static readonly ThemeVariant Violet = new("Violet", ThemeVariant.Dark);
    public static readonly ThemeVariant Graphite = new("Graphite", ThemeVariant.Dark);
    public static readonly ThemeVariant Mist = new("Mist", ThemeVariant.Light);

    public static ThemeVariant For(AppTheme theme) => theme.Id switch
    {
        "Dark" => ThemeVariant.Dark,
        "Light" => ThemeVariant.Light,
        "Ocean" => Ocean,
        "Forest" => Forest,
        "Violet" => Violet,
        "Graphite" => Graphite,
        "Mist" => Mist,
        _ => ThemeVariant.Dark
    };
}
