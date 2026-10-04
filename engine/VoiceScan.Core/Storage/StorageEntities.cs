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
