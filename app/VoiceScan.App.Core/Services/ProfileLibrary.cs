using System.Text.Json;
using VoiceScan.App.Core.Models;
using VoiceScan.Core;
using VoiceScan.Core.Logging;

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

    public static void Delete(string path)
    {
        File.Delete(path);
        VoiceScanLogger.Info("ProfileLibrary", $"Deleted voice profile {path}");
    }
}
