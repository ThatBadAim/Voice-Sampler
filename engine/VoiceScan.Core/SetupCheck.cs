namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>Reports what a fresh install is still missing before scanning can work.</summary>
public static class SetupCheck
{
    public static readonly string[] RequiredModelFiles =
    [
        "ecapa_tdnn.onnx"
    ];

    public static readonly string[] RequiredTools = ["ffmpeg", "ffprobe", "ffplay"];

    public static IReadOnlyList<string> MissingModels() =>
        RequiredModelFiles.Where(f => AppPaths.FindModel(f) is null).ToList();

    public static IReadOnlyList<string> MissingTools() =>
        RequiredTools.Where(t => !IsOnPath(t)).ToList();

    public static string FfmpegInstallHint =>
        OperatingSystem.IsWindows() ? "Install FFmpeg with: winget install Gyan.FFmpeg, then restart VoiceScan."
        : OperatingSystem.IsMacOS() ? "Install FFmpeg with: brew install ffmpeg."
        : "Install FFmpeg with your package manager, for example: sudo apt install ffmpeg (Debian/Ubuntu) or sudo pacman -S ffmpeg (Arch).";

    private static bool IsOnPath(string tool)
    {
        string[] names = OperatingSystem.IsWindows() ? [tool + ".exe", tool] : [tool];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return dirs.Any(d => names.Any(n => File.Exists(Path.Combine(d, n))));
    }
}
