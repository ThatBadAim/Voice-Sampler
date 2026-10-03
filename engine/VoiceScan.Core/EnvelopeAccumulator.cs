namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Streaming min/max waveform envelope: feed audio in any chunk sizes, then ask for N buckets.
/// Keeps one min/max pair per 256 samples, so memory grows by about 2 MB per five hours of audio.
/// </summary>
public sealed class EnvelopeAccumulator
{
    private const int BlockSamples = 256;

    private readonly List<float> _blockMin = new();
    private readonly List<float> _blockMax = new();
    private float _curMin;
    private float _curMax;
    private int _curCount;

    public long SampleCount { get; private set; }

    public void Add(ReadOnlySpan<float> samples)
    {
        SampleCount += samples.Length;
        foreach (float value in samples)
        {
            if (value < _curMin) _curMin = value;
            if (value > _curMax) _curMax = value;
            if (++_curCount == BlockSamples)
            {
                FlushBlock();
            }
        }
    }

    /// <summary>Peaks over <paramref name="buckets"/> equal slices of the audio (fewer when the audio is very short).</summary>
    public (float[] Min, float[] Max) Build(int buckets)
    {
        if (_curCount > 0) FlushBlock();

        int blocks = _blockMin.Count;
        int count = Math.Min(buckets, blocks);
        var min = new float[count];
        var max = new float[count];
        for (int b = 0; b < count; b++)
        {
            int from = (int)((long)b * blocks / count);
            int to = Math.Max(from + 1, (int)((long)(b + 1) * blocks / count));
            for (int k = from; k < to; k++)
            {
                if (_blockMin[k] < min[b]) min[b] = _blockMin[k];
                if (_blockMax[k] > max[b]) max[b] = _blockMax[k];
            }
        }
        return (min, max);
    }

    private void FlushBlock()
    {
        _blockMin.Add(_curMin);
        _blockMax.Add(_curMax);
        _curMin = 0f;
        _curMax = 0f;
        _curCount = 0;
    }
}
