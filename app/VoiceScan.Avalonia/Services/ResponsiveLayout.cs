using Avalonia.Controls;

namespace VoiceScan.App.Services;

public static class ResponsiveLayout
{
    public const double CompactWindowWidth = 1000;
    public const double StackedPageWidth = 820;

    /// <summary>Lays two panes side by side, or one above the other when <paramref name="stacked"/>.</summary>
    public static void SplitPanes(Grid grid, bool stacked, double firstWidth, Control first, Control second, params Control[] withSecond)
    {
        grid.ColumnDefinitions = ColumnDefinitions.Parse(stacked ? "*" : $"{firstWidth},40,*");
        grid.RowDefinitions = RowDefinitions.Parse(stacked ? "Auto,32,*" : "*");
        first.MaxHeight = stacked ? 300 : double.PositiveInfinity;
        Grid.SetRow(first, 0);
        Grid.SetColumn(first, 0);
        foreach (var pane in new[] { second }.Concat(withSecond))
        {
            Grid.SetRow(pane, stacked ? 2 : 0);
            Grid.SetColumn(pane, stacked ? 0 : 2);
        }
    }
}
