using System.Text.Json;
using VoiceScan.App.Core.Models;
using VoiceScan.Core;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.Services;

/// <summary>Lists the voice profiles saved in the per-user profiles directory.</summary>
public static class ProfileLibrary
{
    public static IReadOnlyList<VoiceProfileSummary> List(string? directory = null)
    {
        directory ??= AppPaths.ProfilesDirectory;
        if (!Directory.Exists(directory)) return [];

        var profiles = new List<VoiceProfileSummary>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var profile = VoiceProfile.LoadFromFile(path);
                profiles.Add(new VoiceProfileSummary(
                    profile.ProfileName, path, profile.Centroid.Length,
                    profile.EnrollmentEmbeddings.Count, DateTimeOffset.Parse(profile.CreatedAt)));
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException)
            {
                VoiceScanLogger.Warn("ProfileLibrary", $"Skipping unreadable profile {path}: {ex.Message}");
            }
        }
        return profiles.OrderByDescending(p => p.CreatedAtUtc).ToList();
    }

    /// <summary>Enrolled voices made with <paramref name="modelVersion"/>, used to name speakers whose voice matches one.</summary>
    public static IReadOnlyList<KnownVoice> LoadKnownVoices(string modelVersion, string? directory = null)
    {
        var voices = new List<KnownVoice>();
        foreach (var summary in List(directory))
        {
            try
            {
                var profile = VoiceProfile.LoadFromFile(summary.Path);
                if (profile.ModelVersion == modelVersion && profile.Centroid.Length > 0)
                {
                    voices.Add(new KnownVoice(profile.ProfileName, profile.Centroid));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                VoiceScanLogger.Warn("ProfileLibrary", $"Skipping voice profile {summary.Path}: {ex.Message}");
            }
        }
        return voices;
    }

    /// <summary>Names of enrolled voices made with a different embedding model than <paramref name="modelVersion"/>.</summary>
    public static IReadOnlyList<string> IncompatibleProfiles(string modelVersion, string? directory = null)
    {
        var names = new List<string>();
        foreach (var summary in List(directory))
        {
            try
            {
                if (VoiceProfile.LoadFromFile(summary.Path).ModelVersion != modelVersion) names.Add(summary.Name);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                VoiceScanLogger.Warn("ProfileLibrary", $"Skipping voice profile {summary.Path}: {ex.Message}");
            }
        }
        return names;
    }

    public static void Delete(string path)
    {
        File.Delete(path);
        VoiceScanLogger.Info("ProfileLibrary", $"Deleted voice profile {path}");
    }
}
