namespace VoiceScan.App.Core.Services;

public static class MediaFileCollector
{
    public static readonly string[] Extensions = [".wav", ".mp3", ".flac", ".ogg", ".mp4", ".mkv", ".m4a"];

    public static readonly string[] PickerPatterns = [.. Extensions.Select(e => "*" + e)];

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Expands files and folders (recursively) into a de-duplicated, sorted list of supported media files.
    /// Files named explicitly are kept only if supported; unreadable folders are skipped.
    /// </summary>
    public static IReadOnlyList<string> Collect(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(PathComparer);
        var result = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            if (Directory.Exists(path))
            {
                // Full paths, so a folder given relatively de-duplicates against the same files named explicitly.
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var file in Directory.EnumerateFiles(Path.GetFullPath(path), "*", options).Where(IsSupported).Order())
                {
                    if (seen.Add(file)) result.Add(file);
                }
            }
            else if (File.Exists(path) && IsSupported(path))
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full)) result.Add(full);
            }
        }

        return result;
    }
}
