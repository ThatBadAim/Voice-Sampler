namespace VoiceScan.Core.Storage;

using System;
using System.Collections.Generic;

public record CachedWindow(
    double StartTimeSeconds,
    double EndTimeSeconds,
    float[] Embedding,
    double SnrDb = 20.0);

/// <summary>Per-file facts stored beside the cached windows so a cache hit needs no re-decode or ffprobe.</summary>
public record CachedFileInfo(
    double DurationSeconds,
    float[]? WaveformMinPeaks = null,
    float[]? WaveformMaxPeaks = null);

public record CachedScan(IReadOnlyList<CachedWindow> Windows, CachedFileInfo Info);

public record StoredProfileInfo(
    string Name,
    string ModelVersion,
    string CreatedAt,
    int ClipCount,
    double TotalSpeechDurationSeconds);

public record CacheStats(
    int TotalCachedFiles,
    int TotalCachedWindows,
    int TotalProfiles,
    int TotalScanResults,
    long DatabaseSizeBytes);

public record StoredScanResult(
    long Id,
    string FilePath,
    string FileHash,
    string ProfileName,
    string ModelId,
    double Threshold,
    string Verdict,
    double MaxConfidence,
    string SegmentsJson,
    string ScannedAt,
    string? SpeakerLabel = null,
    string? Transcript = null,
    bool IsOffensive = false,
    IReadOnlyList<string>? ModerationViolations = null)
{
    public virtual bool Equals(StoredScanResult? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id
            && FilePath == other.FilePath
            && FileHash == other.FileHash
            && ProfileName == other.ProfileName
            && ModelId == other.ModelId
            && Math.Abs(Threshold - other.Threshold) < 1e-6
            && Verdict == other.Verdict
            && Math.Abs(MaxConfidence - other.MaxConfidence) < 1e-6
            && SegmentsJson == other.SegmentsJson
            && ScannedAt == other.ScannedAt
            && SpeakerLabel == other.SpeakerLabel
            && Transcript == other.Transcript
            && IsOffensive == other.IsOffensive
            && ((ModerationViolations == null && other.ModerationViolations == null) ||
                (ModerationViolations != null && other.ModerationViolations != null && System.Linq.Enumerable.SequenceEqual(ModerationViolations, other.ModerationViolations)));
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(FilePath);
        hash.Add(FileHash);
        hash.Add(ProfileName);
        hash.Add(ModelId);
        hash.Add(Threshold);
        hash.Add(Verdict);
        hash.Add(MaxConfidence);
        hash.Add(SegmentsJson);
        hash.Add(ScannedAt);
        hash.Add(SpeakerLabel);
        hash.Add(Transcript);
        hash.Add(IsOffensive);
        return hash.ToHashCode();
    }
}

