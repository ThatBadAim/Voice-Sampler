using System.Diagnostics;

namespace VoiceScan.App.Core.Services;

public interface IAudioOutputSession : IDisposable
{
    bool HasExited { get; }
}

public interface IAudioOutput
{
    bool IsAvailable { get; }
    IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds);
}

/// <summary>Plays media through the ffplay binary that ships with ffmpeg (already required by the engine).</summary>
public sealed class FfplayAudioOutput : IAudioOutput
{
    private readonly Lazy<bool> _isAvailable = new(() => FindOnPath(OperatingSystem.IsWindows() ? "ffplay.exe" : "ffplay"));

    public bool IsAvailable => _isAvailable.Value;

    public static IReadOnlyList<string> BuildArguments(string filePath, double startSeconds, double? durationSeconds)
    {
        // Same protocol whitelist as the decoder: a media file can never make playback open a network or device input.
        var args = new List<string> { "-nodisp", "-autoexit", "-loglevel", "quiet", "-vn", "-protocol_whitelist", "file,pipe" };
        args.Add("-ss");
        args.Add(startSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        if (durationSeconds is > 0)
        {
            args.Add("-t");
            args.Add(durationSeconds.Value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        args.Add(filePath);
        return args;
    }

    public IAudioOutputSession Start(string filePath, double startSeconds, double? durationSeconds)
    {
        var info = new ProcessStartInfo("ffplay")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        foreach (var arg in BuildArguments(filePath, startSeconds, durationSeconds)) info.ArgumentList.Add(arg);

        var process = Process.Start(info) ?? throw new InvalidOperationException("ffplay failed to start.");
        return new FfplaySession(process);
    }

    private static bool FindOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, executable)));

    private sealed class FfplaySession(Process process) : IAudioOutputSession
    {
        public bool HasExited => process.HasExited;

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the check and Kill.
            }
            process.Dispose();
        }
    }
}
