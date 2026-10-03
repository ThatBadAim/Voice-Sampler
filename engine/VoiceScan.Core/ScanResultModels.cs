namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

public sealed class ScanOutputDocument
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0.0";

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

    [JsonPropertyName("engine_version")]
    public string EngineVersion { get; set; } = "0.1.0";

    [JsonPropertyName("elapsed_seconds")]
    public double ElapsedSeconds { get; set; }

    [JsonPropertyName("threshold")]
    public double Threshold { get; set; }
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

    [JsonPropertyName("waveform_min_peaks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? WaveformMinPeaks { get; set; }

    [JsonPropertyName("waveform_max_peaks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? WaveformMaxPeaks { get; set; }
}

public sealed class DetectedSegment
{
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

    [JsonPropertyName("embedding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? Embedding { get; set; }
}
