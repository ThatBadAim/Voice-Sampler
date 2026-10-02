namespace VoiceScan.App.Core.Models;

public enum ReviewDecision
{
    Unreviewed,
    Confirmed,
    Rejected
}

public sealed record ReviewDecisionRecord(
    string SegmentId,
    string FilePath,
    string FileHash,
    string ProfileName,
    double StartTimeSeconds,
    double EndTimeSeconds,
    double Confidence,
    string OriginalVerdict,
    IReadOnlyList<string> ReasonFlags,
    ReviewDecision Decision,
    DateTimeOffset DecidedAtUtc,
    string? Notes = null,
    float[]? SegmentEmbedding = null);

public sealed record ReviewQueueItem(
    HitSegmentResult Segment,
    string FileName,
    string ProfileName,
    DateTimeOffset DetectedAtUtc,
    bool AddedToProfile = false);
