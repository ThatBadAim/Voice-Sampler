namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Linq;

public static class SimilarityScorer
{
    /// <summary>
    /// Computes cosine similarity between two unit-normalized embedding vectors.
    /// </summary>
    public static float CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length)
        {
            throw new ArgumentException($"Vector dimension mismatch: {vectorA.Length} vs {vectorB.Length}");
        }

        float dot = 0f;
        for (int i = 0; i < vectorA.Length; i++)
        {
            dot += vectorA[i] * vectorB[i];
        }

        return dot;
    }

    /// <summary>
    /// Centered moving average of per-window scores over up to <paramref name="radius"/> temporal neighbours on each side.
    /// Neighbours are only pooled while consecutive window starts are at most <paramref name="maxGapSec"/> apart,
    /// so evidence never bridges long silences or removed non-speech. Windows must be sorted by start time.
    /// </summary>
    public static double[] SmoothScores(IReadOnlyList<double> starts, IReadOnlyList<double> scores, int radius, double maxGapSec)
    {
        var smoothed = new double[scores.Count];
        for (int i = 0; i < scores.Count; i++)
        {
            double sum = scores[i];
            int count = 1;
            for (int j = i - 1; j >= Math.Max(0, i - radius) && starts[j + 1] - starts[j] <= maxGapSec; j--)
            {
                sum += scores[j];
                count++;
            }
            for (int j = i + 1; j <= Math.Min(scores.Count - 1, i + radius) && starts[j] - starts[j - 1] <= maxGapSec; j++)
            {
                sum += scores[j];
                count++;
            }
            smoothed[i] = sum / count;
        }
        return smoothed;
    }

    /// <summary>
    /// Merges consecutive or overlapping hit windows into continuous detected speech segments.
    /// </summary>
    public static List<DetectedSegment> MergeAdjacentHits(
        IReadOnlyList<(double Start, double End, double Confidence)> hits,
        double mergeToleranceSec = 1.0)
    {
        if (hits.Count == 0)
        {
            return new List<DetectedSegment>();
        }

        var sorted = hits.OrderBy(h => h.Start).ToList();
        var segments = new List<DetectedSegment>();

        double curStart = sorted[0].Start;
        double curEnd = sorted[0].End;
        double curConf = sorted[0].Confidence;

        for (int i = 1; i < sorted.Count; i++)
        {
            var next = sorted[i];
            if (next.Start <= curEnd + mergeToleranceSec)
            {
                curEnd = Math.Max(curEnd, next.End);
                curConf = Math.Max(curConf, next.Confidence);
            }
            else
            {
                segments.Add(new DetectedSegment
                {
                    StartTimeSeconds = Math.Round(curStart, 3),
                    EndTimeSeconds = Math.Round(curEnd, 3),
                    Confidence = Math.Round(curConf, 4),
                    Verdict = "Match",
                    ReasonFlags = new List<string>()
                });

                curStart = next.Start;
                curEnd = next.End;
                curConf = next.Confidence;
            }
        }

        segments.Add(new DetectedSegment
        {
            StartTimeSeconds = Math.Round(curStart, 3),
            EndTimeSeconds = Math.Round(curEnd, 3),
            Confidence = Math.Round(curConf, 4),
            Verdict = "Match",
            ReasonFlags = new List<string>()
        });

        return segments;
    }

    /// <summary>
    /// Performs temporal smoothing and segment aggregation:
    /// - Merges adjacent hits within mergeToleranceSec.
    /// - Requires neighboring window support (within neighborToleranceSec) or a high peak score (>= baseThreshold + peakDelta).
    /// - Drops isolated weak windows/segments that lack sufficient temporal support and peak confidence.
    /// </summary>
    public static List<DetectedSegment> TemporalSmoothingAndAggregation(
        IReadOnlyList<(double Start, double End, double Confidence)> hits,
        double baseThreshold,
        double peakDelta = 0.04,
        double neighborToleranceSec = 2.0,
        double mergeToleranceSec = 1.0,
        double minDurationSec = 1.5)
    {
        if (hits == null || hits.Count == 0)
        {
            return new List<DetectedSegment>();
        }

        var sorted = hits.OrderBy(h => h.Start).ToList();
        double peakThreshold = baseThreshold + peakDelta;

        // 1. Identify isolated weak windows and filter them
        var supportedHits = new List<(double Start, double End, double Confidence)>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var cur = sorted[i];
            bool hasNeighbor = false;

            // Check previous neighbor
            if (i > 0 && Math.Abs(cur.Start - sorted[i - 1].Start) <= neighborToleranceSec)
            {
                hasNeighbor = true;
            }
            // Check next neighbor
            if (!hasNeighbor && i + 1 < sorted.Count && Math.Abs(sorted[i + 1].Start - cur.Start) <= neighborToleranceSec)
            {
                hasNeighbor = true;
            }

            // Keep if supported by adjacent window OR if peak score is strong
            if (hasNeighbor || cur.Confidence >= peakThreshold)
            {
                supportedHits.Add(cur);
            }
        }

        if (supportedHits.Count == 0)
        {
            return new List<DetectedSegment>();
        }

        // 2. Merge adjacent supported hits
        var rawMerged = MergeAdjacentHits(supportedHits, mergeToleranceSec);

        // 3. Post-aggregation pruning: drop short isolated segments if they don't meet peak threshold
        var finalSegments = new List<DetectedSegment>();
        foreach (var seg in rawMerged)
        {
            double duration = seg.EndTimeSeconds - seg.StartTimeSeconds;
            if (duration < minDurationSec && seg.Confidence < peakThreshold)
            {
                continue; // Drop isolated weak short segment
            }
            finalSegments.Add(seg);
        }

        return finalSegments;
    }
}
