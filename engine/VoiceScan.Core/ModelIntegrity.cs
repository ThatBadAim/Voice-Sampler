namespace VoiceScan.Core;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceScan.Core.Logging;

/// <summary>Checks model weights against the SHA-256 recorded in models/manifest.json before they are loaded.</summary>
public static class ModelIntegrity
{
    /// <exception cref="InvalidDataException">The file is listed in the manifest and its hash differs.</exception>
    public static void Verify(string modelPath)
    {
        string fileName = Path.GetFileName(modelPath);
        string? expected = FindExpectedHash(modelPath, fileName);
        if (expected is null)
        {
            VoiceScanLogger.Warn("ModelIntegrity", $"No manifest checksum for '{fileName}'; loading it unverified.");
            return;
        }

        using var stream = File.OpenRead(modelPath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model '{modelPath}' does not match the SHA-256 in models/manifest.json (expected {expected}, got {actual}). Refusing to load it.");
        }
    }

    private static string? FindExpectedHash(string modelPath, string fileName)
    {
        var candidates = new[] { Path.GetDirectoryName(Path.GetFullPath(modelPath))! }
            .Concat(AppPaths.ModelSearchDirectories())
            .Select(d => Path.Combine(d, "manifest.json"));

        foreach (var manifestPath in candidates.Where(File.Exists))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("models", out var models)) continue;
            foreach (var model in models.EnumerateArray())
            {
                if (model.TryGetProperty("filename", out var name)
                    && string.Equals(name.GetString(), fileName, StringComparison.OrdinalIgnoreCase)
                    && model.TryGetProperty("sha256", out var hash))
                {
                    return hash.GetString();
                }
            }
        }
        return null;
    }
}
