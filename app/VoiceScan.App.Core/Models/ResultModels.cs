using VoiceScan.Core;

namespace VoiceScan.App.Core.Models;

public enum VerdictFilter
{
    All,
    Match,
    Possible,
    NoMatch,
    Error
}

public enum ResultSortColumn
{
    FileName,
    Verdict,
    MaxConfidence,
    Duration,
    HitCount
}

public sealed record WaveformEnvelope(
    IReadOnlyList<float> MinPeaks,
    IReadOnlyList<float> MaxPeaks,
    double DurationSeconds,
    int BucketCount);

public sealed record HitSegmentResult(
    string SegmentId,
    string FilePath,
    double StartTimeSeconds,
    double EndTimeSeconds,
    double DurationSeconds,
    string Verdict,
    double Confidence,
    IReadOnlyList<string> ReasonFlags,
    ReviewDecision Decision = ReviewDecision.Unreviewed,
    float[]? SegmentEmbedding = null,
    string? FileHash = null,
    string? SpeakerLabel = null,
    string? Transcript = null,
    bool IsOffensive = false,
    IReadOnlyList<string>? ModerationViolations = null)
{
    public double Start => StartTimeSeconds;
    public double End => EndTimeSeconds;
    public bool IsFlagged => IsOffensive;
    public IReadOnlyList<string> ModerationViolations { get; init; } = ModerationViolations ?? Array.Empty<string>();

    public string ViolationsSummary => ModerationViolations.Count > 0
        ? string.Join(", ", ModerationViolations)
        : (IsOffensive ? "Violation" : string.Empty);

    public string? DisplaySpeakerLabel => string.IsNullOrWhiteSpace(SpeakerLabel)
        ? null
        : (SpeakerLabel.StartsWith('[') && SpeakerLabel.EndsWith(']') ? SpeakerLabel : $"[{SpeakerLabel}]");

    /// <summary>
    /// Stable id that stays unique across files that share a name: the content hash (or, failing that, the full
    /// path) is part of it.
    /// </summary>
    public static string CreateId(string filePath, string? fileHash, int index, double startTimeSeconds)
    {
        string source = string.IsNullOrEmpty(fileHash) ? Path.GetFullPath(filePath) : fileHash;
        string tag = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)))[..8];
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Path.GetFileNameWithoutExtension(filePath)}_{tag}_{index}_{startTimeSeconds:F1}");
    }
}

public sealed record FileVerdictResult(
    string FilePath,
    string FileName,
    string FileHash,
    double DurationSeconds,
    string OverallVerdict,
    double MaxConfidence,
    IReadOnlyList<HitSegmentResult> Segments,
    WaveformEnvelope? Waveform = null,
    string? ErrorMessage = null,
    int AudioTrackIndex = 0,
    string? ProfileName = null,
    string? ProfilePath = null)
{
    public bool IsError => OverallVerdict.Equals(PipelineScanner.ErrorVerdict, StringComparison.OrdinalIgnoreCase);
}
