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

/// <param name="ProfilePath">Voice profile file the segment was scanned against; confirmed segments can be added to it.</param>
public sealed record ReviewQueueItem(
    HitSegmentResult Segment,
    string FileName,
    string ProfileName,
    DateTimeOffset DetectedAtUtc,
    string? ProfilePath = null);
