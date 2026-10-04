namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class VoiceProfile
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0.0";

    [JsonPropertyName("profile_name")]
    public string ProfileName { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="ISpeakerEmbeddingModel.ModelVersion"/> of the model that produced the embeddings. Empty for profiles
    /// enrolled before it was recorded; those must be re-enrolled.
    /// </summary>
    [JsonPropertyName("model_version")]
    public string ModelVersion { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("centroid")]
    public float[] Centroid { get; set; } = Array.Empty<float>();

    [JsonPropertyName("enrollment_embeddings")]
    public List<float[]> EnrollmentEmbeddings { get; set; } = new();

    [JsonPropertyName("clip_count")]
    public int ClipCount { get; set; }

    [JsonPropertyName("total_speech_duration_seconds")]
    public double TotalSpeechDurationSeconds { get; set; }

    public void SaveToFile(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Write to a sibling file first so a crash mid-write never leaves a truncated profile behind.
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        string tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }

    public static VoiceProfile LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Voice profile file not found: {filePath}");
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<VoiceProfile>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize voice profile from: {filePath}");
    }
}
