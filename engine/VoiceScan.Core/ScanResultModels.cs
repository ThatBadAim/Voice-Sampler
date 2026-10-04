namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class ScanOutputDocument
{
    public const string CurrentSchemaVersion = "1.1.0";

    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.1.0";

    [JsonPropertyName("scan_metadata")]
    public ScanMetadata ScanMetadata { get; set; } = new();

    [JsonPropertyName("files")]
    public List<FileScanResult> Files { get; set; } = new();
}

public sealed class ScanMetadata
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonPropertyName("profile_name")]
    public string ProfileName { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("model_version")]
    public string ModelVersion { get; set; } = string.Empty;

    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = string.Empty;

    [JsonPropertyName("elapsed_seconds")]
    public double ElapsedSeconds { get; set; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }

    // Null in results written before these settings were recorded; an evidence report needs them.
    [JsonPropertyName("clustering_enabled")]
    public bool? ClusteringEnabled { get; set; }

    [JsonPropertyName("cluster_threshold")]
    public double? ClusterThreshold { get; set; }

    [JsonPropertyName("temporal_smoothing")]
    public bool? TemporalSmoothing { get; set; }
}

public sealed class FileScanResult
{
    [JsonPropertyName("file_path")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("clip_id")]
    public string ClipId { get; set; } = string.Empty;

    [JsonPropertyName("file_hash")]
    public string FileHash { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("audio_track_index")]
    public int AudioTrackIndex { get; set; }

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "No match";

    [JsonPropertyName("max_confidence")]
    public double MaxConfidence { get; set; }

    [JsonPropertyName("segments")]
    public List<DetectedSegment> Segments { get; set; } = new();

    [JsonPropertyName("reason_flags")]
    public List<string> ReasonFlags { get; set; } = new();

    /// <summary>Set (with Verdict "Error") when the file could not be scanned.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("waveform_min_peaks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? WaveformMinPeaks { get; set; }

    [JsonPropertyName("waveform_max_peaks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? WaveformMaxPeaks { get; set; }
}

public sealed class DetectedSegment
{
    public DetectedSegment()
    {
    }

    public DetectedSegment(
        double startTimeSeconds,
        double endTimeSeconds,
        double confidence,
        string verdict = "Match",
        List<string>? reasonFlags = null,
        string? speakerLabel = null,
        string? transcript = null,
        bool isOffensive = false,
        IReadOnlyList<string>? moderationViolations = null,
        float[]? embedding = null)
    {
        StartTimeSeconds = startTimeSeconds;
        EndTimeSeconds = endTimeSeconds;
        Confidence = confidence;
        Verdict = verdict;
        ReasonFlags = reasonFlags ?? new List<string>();
        SpeakerLabel = speakerLabel;
        Transcript = transcript;
        IsOffensive = isOffensive;
        ModerationViolations = moderationViolations ?? Array.Empty<string>();
        Embedding = embedding;
    }

    [JsonPropertyName("start_time_seconds")]
    public double StartTimeSeconds { get; set; }

    [JsonPropertyName("end_time_seconds")]
    public double EndTimeSeconds { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "Match";

    [JsonPropertyName("reason_flags")]
    public List<string> ReasonFlags { get; set; } = new();

    [JsonPropertyName("speaker_label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SpeakerLabel { get; init; }

    [JsonPropertyName("transcript")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transcript { get; init; }

    [JsonPropertyName("is_offensive")]
    public bool IsOffensive { get; init; }

    [JsonPropertyName("moderation_violations")]
    public IReadOnlyList<string> ModerationViolations { get; init; } = Array.Empty<string>();

    [JsonPropertyName("embedding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? Embedding { get; set; }
}
