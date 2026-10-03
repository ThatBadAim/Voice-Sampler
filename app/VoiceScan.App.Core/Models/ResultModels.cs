using VoiceScan.Core;

namespace VoiceScan.App.Core.Models;

public enum VerdictFilter
{
    All,
    Match,
    Possible,
    NoMatch
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
    string? FileHash = null);

public sealed record FileVerdictResult(
    string FilePath,
    string FileName,
    string FileHash,
    double DurationSeconds,
    string OverallVerdict,
    double MaxConfidence,
    IReadOnlyList<HitSegmentResult> Segments,
    WaveformEnvelope? Waveform = null);
