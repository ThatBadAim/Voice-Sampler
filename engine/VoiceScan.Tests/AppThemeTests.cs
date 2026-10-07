using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;

namespace VoiceScan.Tests;

public class AppThemeTests
{
    [Fact]
    public void Find_UnknownOrNullId_FallsBackToDefault()
    {
        Assert.Equal(AppTheme.DefaultId, AppTheme.Find(null).Id);
        Assert.Equal(AppTheme.DefaultId, AppTheme.Find("NoSuchTheme").Id);
    }

    [Fact]
    public void All_HasUniqueIds_AndBothBrightnessFamilies()
    {
        Assert.Equal(AppTheme.All.Count, AppTheme.All.Select(t => t.Id).Distinct().Count());
        Assert.Contains(AppTheme.All, t => t.IsDark);
        Assert.Contains(AppTheme.All, t => !t.IsDark);
    }

    [Fact]
    public void ThemeId_RoundTripsThroughSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voicescan_settings_{Guid.NewGuid():N}.json");
        try
        {
            var store = new UserSettingsStore(path);
            store.Current.ThemeId = "Ocean";
            store.Save();
            Assert.Equal("Ocean", new UserSettingsStore(path).Current.ThemeId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Theme_DictionaryKeys_ExistInThemeAxaml()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "VoiceScan.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!, "app", "VoiceScan.Avalonia", "Styles", "Theme.axaml"));
        foreach (var theme in AppTheme.All)
            Assert.True(xaml.Contains($"x:Key=\"{theme.Id}\"") || xaml.Contains($"AppThemeVariants.{theme.Id}}}"), theme.Id);
    }
}
