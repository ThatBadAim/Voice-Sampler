namespace VoiceScan.App.Core.Models;

public sealed record SnrTierBenchmarkItem(
    string TierName,
    int TotalFiles,
    double Recall,
    double RecallLowCi,
    double RecallHighCi,
    double Precision,
    double PrecisionLowCi,
    double PrecisionHighCi,
    double FaPerHour,
    double FaLowCi,
    double FaHighCi,
    double MeanTimingErrorSec);

public sealed record BenchmarkReportSummary(
    string ReportId,
    string ReportTitle,
    DateTimeOffset TimestampUtc,
    string GitCommit,
    string TargetProfile,
    string Split,
    double TotalAudioDurationSeconds,
    double ElapsedSeconds,
    double SpeedMultiple,
    double OverallRecall,
    double OverallRecallLowCi,
    double OverallRecallHighCi,
    double OverallPrecision,
    double OverallPrecisionLowCi,
    double OverallPrecisionHighCi,
    double OverallFaPerHour,
    double OverallFaLowCi,
    double OverallFaHighCi,
    double MeanTimingErrorSec,
    IReadOnlyList<SnrTierBenchmarkItem> SnrTiers);
