using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace VoiceScan.App.Services;

public static class FilePickers
{
    public static async Task<string?> PickFileAsync(Control anchor, string title, params string[] patterns)
    {
        var files = await PickFilesAsync(anchor, title, allowMultiple: false, patterns);
        return files.Count > 0 ? files[0] : null;
    }

    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        Control anchor, string title, bool allowMultiple, params string[] patterns) =>
        await PickFilesAsync(anchor, title, allowMultiple, startFolder: null, patterns);

    public static async Task<IReadOnlyList<string>> PickFilesAsync(
        Control anchor, string title, bool allowMultiple, string? startFolder, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(anchor);
        if (top is null) return [];

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = await StartLocationAsync(top, startFolder),
        };
        if (patterns.Length > 0)
        {
            options.FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }];
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public static async Task<string?> PickFolderAsync(Control anchor, string title, string? startFolder = null)
    {
        var top = TopLevel.GetTopLevel(anchor);
        if (top is null) return null;

        var folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                SuggestedStartLocation = await StartLocationAsync(top, startFolder),
            });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static async Task<IStorageFolder?> StartLocationAsync(TopLevel top, string? folder) =>
        folder is not null && Directory.Exists(folder) ? await top.StorageProvider.TryGetFolderFromPathAsync(folder) : null;

    /// <summary>Local paths of files and folders dropped onto a control.</summary>
    public static IReadOnlyList<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    public static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);
}
