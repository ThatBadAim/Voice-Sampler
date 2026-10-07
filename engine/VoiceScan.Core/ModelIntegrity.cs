namespace VoiceScan.Core;

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Checks model weights against the SHA-256 recorded in models/manifest.json before they are loaded.
/// The manifest is compiled into this assembly, so a model file placed in any search directory, together with a
/// manifest of its own, cannot pass verification unless it is byte-identical to the released weights.
/// </summary>
public static class ModelIntegrity
{
    private const string ManifestResource = "VoiceScan.Core.models.manifest.json";

    /// <summary>Verifies the file and returns its SHA-256 as upper-case hex.</summary>
    /// <exception cref="InvalidDataException">The file is not listed in the bundled manifest, or its hash differs.</exception>
    public static string Verify(string modelPath)
    {
        string fileName = Path.GetFileName(modelPath);
        string expected = ExpectedHash(fileName)
            ?? throw new InvalidDataException($"'{fileName}' is not a model listed in the bundled manifest; refusing to load it.");

        string actual = HashFile(modelPath);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model '{modelPath}' does not match the SHA-256 in models/manifest.json (expected {expected}, got {actual}). Refusing to load it.");
        }
        return actual;
    }

    /// <summary>Verifies a user-approved model against the SHA-256 recorded when it was approved.</summary>
    /// <exception cref="InvalidDataException">The file has changed since it was approved.</exception>
    public static string VerifyHash(string modelPath, string expectedSha256)
    {
        string actual = HashFile(modelPath);
        if (string.IsNullOrEmpty(expectedSha256) || !actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model '{modelPath}' has changed since it was approved (expected SHA-256 {expectedSha256}, got {actual}). Refusing to load it; import it again.");
        }
        return actual;
    }

    /// <summary>SHA-256 of the file as upper-case hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>SHA-256 recorded for <paramref name="fileName"/> in the bundled manifest, or null when it is not listed.</summary>
    public static string? ExpectedHash(string fileName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ManifestResource)
            ?? throw new InvalidOperationException($"Embedded resource {ManifestResource} is missing from the build.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var model in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            if (model.TryGetProperty("filename", out var name)
                && string.Equals(name.GetString(), fileName, StringComparison.OrdinalIgnoreCase)
                && model.TryGetProperty("sha256", out var hash))
            {
                return hash.GetString();
            }
        }
        return null;
    }
}
