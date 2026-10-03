namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

public record SpeechAudioWindow(
    double StartTimeSeconds,
    double EndTimeSeconds,
    float[] AudioSamples);

/// <summary>
/// Partitions active speech intervals into fixed-duration sliding windows with configurable hop.
/// Window times describe the real speech extent; short segments are tiled, tails get an end-aligned window.
/// </summary>
public static class SpeechWindowExtractor
{
    public const int DefaultSampleRate = 16000;
    public const double DefaultWindowDurationSeconds = 2.0;
    public const double DefaultHopDurationSeconds = 1.0;

    /// <summary>Speech shorter than this yields unstable embeddings and is skipped.</summary>
    public const double MinSegmentSeconds = 0.5;

    /// <summary>Changes whenever window placement changes; part of the embedding cache key.</summary>
    public const string Fingerprint = "win-tile-tail-min0.5";

    /// <summary>Where a window comes from: <c>SourceLength</c> samples starting at <c>SourceStart</c>, tiled up to the window length when shorter.</summary>
    public readonly record struct WindowPlan(
        double StartTimeSeconds,
        double EndTimeSeconds,
        long SourceStart,
        int SourceLength);

    public static IReadOnlyList<SpeechAudioWindow> ExtractWindows(
        float[] audio,
        IReadOnlyList<SpeechInterval> intervals,
        int sampleRate = DefaultSampleRate,
        double windowSec = DefaultWindowDurationSeconds,
        double hopSec = DefaultHopDurationSeconds)
    {
        var plans = PlanWindows(audio.Length, intervals, sampleRate, windowSec, hopSec);
        var windows = new List<SpeechAudioWindow>(plans.Count);
        foreach (var plan in plans)
        {
            float[] source = new float[plan.SourceLength];
            Array.Copy(audio, plan.SourceStart, source, 0, plan.SourceLength);
            windows.Add(Materialize(plan, source, sampleRate, windowSec));
        }
        return windows;
    }

    /// <summary>Builds the audio for a planned window from its source samples, tiling short segments.</summary>
    public static SpeechAudioWindow Materialize(
        WindowPlan plan,
        float[] source,
        int sampleRate = DefaultSampleRate,
        double windowSec = DefaultWindowDurationSeconds)
    {
        int winSamples = (int)(windowSec * sampleRate);
        if (source.Length == winSamples)
        {
            return new SpeechAudioWindow(plan.StartTimeSeconds, plan.EndTimeSeconds, source);
        }

        // Tile the short segment to the window length: zero padding would add silence frames
        // that skew the per-window mean normalization and the embedding.
        float[] tiled = new float[winSamples];
        for (int i = 0; i < winSamples; i++)
        {
            tiled[i] = source[i % source.Length];
        }
        return new SpeechAudioWindow(plan.StartTimeSeconds, plan.EndTimeSeconds, tiled);
    }

    /// <summary>Decides window placement from speech intervals without touching audio, so long files can be read window by window.</summary>
    public static IReadOnlyList<WindowPlan> PlanWindows(
        long totalSamples,
        IReadOnlyList<SpeechInterval> intervals,
        int sampleRate = DefaultSampleRate,
        double windowSec = DefaultWindowDurationSeconds,
        double hopSec = DefaultHopDurationSeconds)
    {
        int winSamples = (int)(windowSec * sampleRate);
        int hopSamples = (int)(hopSec * sampleRate);
        int minSpeechSamples = (int)(MinSegmentSeconds * sampleRate);
        var plans = new List<WindowPlan>();

        foreach (var interval in intervals)
        {
            long startSamp = Math.Max(0, (long)(interval.StartTimeSeconds * sampleRate));
            long endSamp = Math.Min(totalSamples, (long)(interval.EndTimeSeconds * sampleRate));
            int segLen = (int)(endSamp - startSamp);

            if (segLen < minSpeechSamples)
            {
                continue;
            }

            double segStartSec = (double)startSamp / sampleRate;
            double segEndSec = (double)endSamp / sampleRate;

            if (segLen < winSamples)
            {
                plans.Add(new WindowPlan(segStartSec, segEndSec, startSamp, segLen));
                continue;
            }

            int lastOffset = 0;
            for (int w = 0; w <= segLen - winSamples; w += hopSamples)
            {
                double tStart = segStartSec + ((double)w / sampleRate);
                plans.Add(new WindowPlan(tStart, tStart + windowSec, startSamp + w, winSamples));
                lastOffset = w;
            }

            // Cover the tail that the hop grid skipped with one window aligned to the segment end.
            int tailOffset = segLen - winSamples;
            if (tailOffset - lastOffset >= minSpeechSamples)
            {
                plans.Add(new WindowPlan(segEndSec - windowSec, segEndSec, startSamp + tailOffset, winSamples));
            }
        }

        return plans;
    }
}
