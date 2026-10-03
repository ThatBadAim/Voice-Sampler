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
    private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
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

    public static void ExtractApp(string installDir, IProgress<(int Percent, string Text)> progress)
    {
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
    }

    /// <summary>Downloads the FFmpeg release build, checks its published SHA-256 and unpacks the three tools into installDir\ffmpeg.</summary>
    public static async Task InstallFfmpegAsync(string installDir, IProgress<(int Percent, string Text)> progress)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        string expectedHash = (await http.GetStringAsync(FfmpegUrl + ".sha256")).Trim().Split(' ')[0];

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

    public static void Uninstall(string installDir, bool deleteUserData)
    {
        foreach (var link in new[] { StartMenuLink, DesktopLink })
        {
            if (File.Exists(link)) File.Delete(link);
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (deleteUserData && Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);

        // This process runs from the install folder, so a detached shell removes it after we exit.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{installDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });
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
