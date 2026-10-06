namespace VoiceScan.Core;

using System;
using System.IO;

/// <summary>Per-user locations for everything VoiceScan stores outside the install directory.</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceScan");

    public static string ProfilesDirectory => Path.Combine(DataRoot, "profiles");

    public static string ModelsDirectory => Path.Combine(DataRoot, "models");

    public static string DatabasePath => Path.Combine(DataRoot, "voicescan.db");

    /// <summary>Decoded-audio spool files. Kept on disk under the data root: the system temp directory is often RAM-backed.</summary>
    public static string SpoolDirectory => Path.Combine(DataRoot, "spool");

    public static string LogFilePath => Path.Combine(DataRoot, "logs", "voicescan.log");

    /// <summary>
    /// Attempts to locate the VoiceScan repository root directory by walking up from the base or current directory.
    /// </summary>
    public static string? FindRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "VoiceScan.sln")) ||
                File.Exists(Path.Combine(current, "models", "manifest.json")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null || parent.FullName == current) break;
            current = parent.FullName;
        }

        current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "VoiceScan.sln")) ||
                File.Exists(Path.Combine(current, "models", "manifest.json")))
            {
                return current;
            }
            var parent = Directory.GetParent(current);
            if (parent == null || parent.FullName == current) break;
            current = parent.FullName;
        }

        return null;
    }

    /// <summary>
    /// Directories searched, in order, for ONNX model files. Any file found is still checked against the checksums
    /// compiled into the engine (<see cref="ModelIntegrity"/>), so a model planted in one of these directories is refused.
    /// </summary>
    public static string[] ModelSearchDirectories()
    {
        var dirs = new System.Collections.Generic.List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "models"),
            ModelsDirectory,
            Path.Combine(Directory.GetCurrentDirectory(), "models"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models")
        };
        string? repoRoot = FindRepoRoot();
        if (repoRoot != null)
        {
            dirs.Add(Path.Combine(repoRoot, "models"));
        }
        return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Distinct(dirs));
    }

    /// <summary>Puts the FFmpeg copy shipped next to the executable (installer layout: ./ffmpeg) first on PATH for this process and its children.</summary>
    public static void UseBundledTools()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (!Directory.Exists(dir)) return;

        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    public static string? FindModel(string fileName)
    {
        foreach (var dir in ModelSearchDirectories())
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
