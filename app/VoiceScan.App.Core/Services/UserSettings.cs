using System.Text.Json;
using VoiceScan.Core.Logging;

namespace VoiceScan.App.Core.Services;

public sealed class UserSettings
{
    public string? LastBrowseFolder { get; set; }
    public string? LastProfilePath { get; set; }
    public bool UseClustering { get; set; } = true;
    public bool UseTemporalSmoothing { get; set; } = true;
    /// <summary>AHC stopping distance chosen by the user; null uses the embedding model's measured default.</summary>
    public double? ClusterThreshold { get; set; }
    /// <summary>Classifier score at or above which a transcribed line is listed as an incident.</summary>
    public double ModerationSensitivity { get; set; } = 0.5;
    /// <summary>Voice-embedding model chosen on the Models page; null uses the built-in ECAPA-TDNN.</summary>
    public string? EmbeddingModelId { get; set; }
    /// <summary>Id of the selected <see cref="Models.AppTheme"/>; null or unknown ids fall back to the default theme.</summary>
    public string? ThemeId { get; set; }
}

/// <summary>Persists <see cref="UserSettings"/> as JSON. Settings are a convenience, so I/O problems never surface to the user.</summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public UserSettings Current { get; }

    public UserSettingsStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                VoiceScanLogger.Warn("UserSettings", $"Could not save settings to {_path}: {ex.Message}");
            }
        }
    }

    private static UserSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new UserSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            VoiceScanLogger.Warn("UserSettings", $"Ignoring unreadable settings file {path}: {ex.Message}");
        }

        return new UserSettings();
    }
}
