using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace VoiceScan.Installer;

internal static class InstallActions
{
    public const string AppName = "VoiceScan";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VoiceScan";
    // A fixed release with its SHA-256 pinned here (checked against both GitHub and gyan.dev on 2026-10-04), so a
    // compromised or replaced download is rejected instead of being verified against a checksum from the same server.
    private const string FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
    private const string FfmpegSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";

    /// <summary>Written into every install folder; uninstall only deletes a folder that carries it.</summary>
    private const string InstallMarker = ".voicescan-install";
    private static readonly string[] FfmpegTools = ["ffmpeg", "ffprobe", "ffplay"];

    public static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    private static string StartMenuLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{AppName}.lnk");

    private static string DesktopLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

    /// <summary>True when ffmpeg, ffprobe and ffplay can already be found on PATH.</summary>
    public static bool FfmpegOnPath()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return FfmpegTools.All(tool => dirs.Any(d => File.Exists(Path.Combine(d, tool + ".exe"))));
    }

    /// <summary>
    /// Returns the full install path, or throws if installing there could later let uninstall delete unrelated files:
    /// drive roots and non-empty folders that are not an earlier VoiceScan install are refused.
    /// </summary>
    public static string ValidateInstallDir(string installDir)
    {
        string full = Path.GetFullPath(installDir.Trim());
        if (Path.GetPathRoot(full) is { } root && string.Equals(full.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a folder for VoiceScan, not the root of a drive.");
        }
        if (full.Contains('%') || full.Contains('"'))
        {
            throw new InvalidOperationException("Choose a folder whose path does not contain % or \" characters.");
        }
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && !IsVoiceScanFolder(full))
        {
            throw new InvalidOperationException(
                $"{full} already contains other files. Choose an empty folder (or the folder of an earlier VoiceScan install) so uninstalling never removes anything else.");
        }
        return full;
    }

    /// <summary>
    /// A folder this installer owns: it carries the marker, or it is the app-specific default location used by
    /// installs made before the marker existed.
    /// </summary>
    private static bool IsVoiceScanFolder(string fullPath) =>
        File.Exists(Path.Combine(fullPath, InstallMarker))
        || string.Equals(fullPath.TrimEnd('\\', '/'), Path.GetFullPath(DefaultInstallDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static void ExtractApp(string installDir, IProgress<(int Percent, string Text)> progress)
    {
        installDir = ValidateInstallDir(installDir);
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("This installer was built without the application payload.");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);

        Directory.CreateDirectory(installDir);
        string root = Path.GetFullPath(installDir) + Path.DirectorySeparatorChar;
        int done = 0;
        foreach (var entry in zip.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsafe path in installer payload: {entry.FullName}");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            progress.Report((++done * 60 / zip.Entries.Count, "Copying VoiceScan files..."));
        }

        File.WriteAllText(Path.Combine(installDir, InstallMarker), "Folder created by the VoiceScan installer; uninstall removes it.");
    }

    /// <summary>Downloads the FFmpeg release build, checks its published SHA-256 and unpacks the three tools into installDir\ffmpeg.</summary>
    public static async Task InstallFfmpegAsync(string installDir, IProgress<(int Percent, string Text)> progress)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        const string expectedHash = FfmpegSha256;

        string zipPath = Path.Combine(Path.GetTempPath(), $"ffmpeg_{Guid.NewGuid():N}.zip");
        try
        {
            using (var response = await http.GetAsync(FfmpegUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(zipPath);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                    {
                        progress.Report((60 + (int)(read * 30 / total), $"Downloading FFmpeg ({read / 1_000_000} of {total / 1_000_000} MB)..."));
                    }
                }
            }

            progress.Report((90, "Verifying FFmpeg download..."));
            string actualHash;
            await using (var check = File.OpenRead(zipPath))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(check));
            }
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The FFmpeg download failed its checksum check.");
            }

            progress.Report((94, "Installing FFmpeg..."));
            string ffmpegDir = Path.Combine(installDir, "ffmpeg");
            Directory.CreateDirectory(ffmpegDir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
            {
                string name = Path.GetFileNameWithoutExtension(entry.Name);
                bool isTool = entry.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && FfmpegTools.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (isTool)
                {
                    entry.ExtractToFile(Path.Combine(ffmpegDir, entry.Name), overwrite: true);
                }
            }

            if (FfmpegTools.Any(t => !File.Exists(Path.Combine(ffmpegDir, t + ".exe"))))
            {
                throw new InvalidOperationException("The FFmpeg download did not contain ffmpeg, ffprobe and ffplay.");
            }
        }
        finally
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
    }

    public static void Register(string installDir, bool desktopShortcut)
    {
        string exe = Path.Combine(installDir, "VoiceScan.exe");
        string uninstaller = Path.Combine(installDir, "Uninstall.exe");
        File.Copy(Environment.ProcessPath!, uninstaller, overwrite: true);

        CreateShortcut(StartMenuLink, exe, installDir);
        if (desktopShortcut) CreateShortcut(DesktopLink, exe, installDir);

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        key.SetValue("Publisher", AppName);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", exe);
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public static void Launch(string installDir) =>
        Process.Start(new ProcessStartInfo(Path.Combine(installDir, "VoiceScan.exe")) { UseShellExecute = true, WorkingDirectory = installDir });

    public static string? InstalledLocation()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>
    /// Removes shortcuts, registration and (optionally) user data. The install folder is deleted only if it carries
    /// the installer's marker; returns false when it was left in place for that reason.
    /// </summary>
    public static bool Uninstall(string installDir, bool deleteUserData)
    {
        foreach (var link in new[] { StartMenuLink, DesktopLink })
        {
            if (File.Exists(link)) File.Delete(link);
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (deleteUserData && Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);

        string full = Path.GetFullPath(installDir);
        if (!IsVoiceScanFolder(full) || full.Contains('%') || full.Contains('"'))
        {
            return false;
        }

        // This process runs from the install folder, so a detached shell removes it after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{full}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
        return true;
    }

    private static void CreateShortcut(string linkPath, string target, string workingDir)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable, so shortcuts cannot be created.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = target;
        link.WorkingDirectory = workingDir;
        link.Description = "Find where a person speaks in your recordings";
        link.Save();
    }
}
