namespace VoiceScan.Core.Storage;

using System;
using System.Collections.Generic;

public record CachedWindow(
    double StartTimeSeconds,
    double EndTimeSeconds,
    float[] Embedding);

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
