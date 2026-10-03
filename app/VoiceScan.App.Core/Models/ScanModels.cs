namespace VoiceScan.App.Core.Models;

public enum ScanExecutionState
{
    Idle,
    Scanning,
    Paused,
    Completed,
    Cancelled,
    Failed
}

public sealed record OverallScanProgress(
    ScanExecutionState State,
    int TotalFiles,
    int ProcessedFiles,
    int CurrentFileIndex,
    string? CurrentFileName,
    double CurrentFileProgress,
    double OverallProgressPercent,
    TimeSpan ElapsedTime,
    TimeSpan? EstimatedTimeRemaining,
    double RealtimeMultiple,
    double GpuUtilizationPercent,
    double MemoryMegabytes,
    int TotalMatchesFound,
    int TotalPossibleFound,
    int TotalNoMatchFound);
