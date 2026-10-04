using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using VoiceScan.App.Services;
using VoiceScan.Core;

namespace VoiceScan.App;

/// <summary>Shown instead of the main window when models or FFmpeg are missing.</summary>
public partial class SetupWindow : Window
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;

    public SetupWindow() : this(null!) { }

    public SetupWindow(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        InitializeComponent();
        Refresh();
    }

    public static bool IsSetupNeeded() =>
        SetupCheck.MissingModels().Count > 0 || SetupCheck.MissingTools().Count > 0;

    private void Refresh()
    {
        var models = SetupCheck.MissingModels();
        ModelsCard.IsVisible = models.Count > 0;
        ModelsText.Text =
            $"Missing: {string.Join(", ", models)}.\n" +
            $"Choose the files and VoiceScan will copy them to {AppPaths.ModelsDirectory}. " +
            "They are listed with download links and checksums in models/manifest.json.";

        var tools = SetupCheck.MissingTools();
        ToolsCard.IsVisible = tools.Count > 0;
        ToolsText.Text = $"Not found on PATH: {string.Join(", ", tools)}.\n{SetupCheck.FfmpegInstallHint}";
    }

    private async void LocateModels_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await FilePickers.PickFolderAsync(this, "Folder containing the model files");
        if (folder is null) return;

        var found = SetupCheck.MissingModels()
            .Select(name => (Name: name, Path: FindFile(folder, name)))
            .ToList();
        var located = found.Where(f => f.Path is not null).ToList();

        Directory.CreateDirectory(AppPaths.ModelsDirectory);
        foreach (var (name, path) in located)
        {
            File.Copy(path!, Path.Combine(AppPaths.ModelsDirectory, name), overwrite: true);
        }

        var notFound = found.Where(f => f.Path is null).Select(f => f.Name).ToList();
        StatusText.Text = located.Count == 0
            ? $"None of the missing files ({string.Join(", ", notFound)}) were found in that folder or its subfolders."
            : $"Copied {located.Count} file(s)." + (notFound.Count > 0 ? $" Still missing: {string.Join(", ", notFound)}." : "");
        Refresh();
    }

    private static string? FindFile(string folder, string fileName) =>
        Directory.EnumerateFiles(folder, fileName, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .FirstOrDefault();

    private async void ChooseModels_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FilePickers.PickFilesAsync(this, "Model files (.onnx)", allowMultiple: true, "*.onnx");
        if (files.Count == 0) return;

        Directory.CreateDirectory(AppPaths.ModelsDirectory);
        foreach (var file in files)
        {
            File.Copy(file, Path.Combine(AppPaths.ModelsDirectory, Path.GetFileName(file)), overwrite: true);
        }

        StatusText.Text = $"Copied {files.Count} file(s).";
        Refresh();
    }

    private void OpenModels_Click(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.ModelsDirectory);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.ModelsDirectory, UseShellExecute = true });
    }

    private void CheckAgain_Click(object? sender, RoutedEventArgs e)
    {
        if (IsSetupNeeded())
        {
            Refresh();
            StatusText.Text = "Still missing items. Fix the ones listed above, then check again.";
            return;
        }

        IsEnabled = false;
        StatusText.Text = "Loading and verifying voice models…";
        _ = App.OpenMainWindowAsync(_lifetime, this);
    }
}
