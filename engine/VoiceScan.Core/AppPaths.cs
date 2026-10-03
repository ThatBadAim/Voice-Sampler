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

    /// <summary>Directories searched, in order, for ONNX model files.</summary>
    public static string[] ModelSearchDirectories() =>
    [
        Path.Combine(AppContext.BaseDirectory, "models"),
        ModelsDirectory,
        Path.Combine(Directory.GetCurrentDirectory(), "models"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models")
    ];

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
