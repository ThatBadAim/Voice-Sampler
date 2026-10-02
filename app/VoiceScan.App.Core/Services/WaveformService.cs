using VoiceScan.App.Core.Models;
using VoiceScan.Core;

namespace VoiceScan.App.Core.Services;

public interface IWaveformService
{
    Task<WaveformEnvelope> GenerateEnvelopeAsync(string audioFilePath, int targetBuckets = 300, CancellationToken cancellationToken = default);
    WaveformEnvelope GenerateEnvelopeFromPcm(ReadOnlySpan<float> pcm, int sampleRate = 16000, int targetBuckets = 300);
}

public sealed class WaveformService : IWaveformService
{
    public async Task<WaveformEnvelope> GenerateEnvelopeAsync(string audioFilePath, int targetBuckets = 300, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(audioFilePath))
        {
            return new WaveformEnvelope([], [], 0.0, 0);
        }

        List<float> samples = [];
        await foreach (var chunk in AudioDecoder.StreamDecodeAsync(audioFilePath, cancellationToken: cancellationToken))
        {
            samples.AddRange(chunk.Samples);
        }

        if (samples.Count == 0)
        {
            return new WaveformEnvelope([], [], 0.0, 0);
        }

        return GenerateEnvelopeFromPcm(samples.ToArray(), 16000, targetBuckets);
    }

    public WaveformEnvelope GenerateEnvelopeFromPcm(ReadOnlySpan<float> pcm, int sampleRate = 16000, int targetBuckets = 300)
    {
        double duration = (double)pcm.Length / sampleRate;
        if (pcm.Length == 0 || targetBuckets <= 0)
        {
            return new WaveformEnvelope([], [], duration, 0);
        }

        int bucketCount = Math.Min(targetBuckets, pcm.Length);
        float[] minPeaks = new float[bucketCount];
        float[] maxPeaks = new float[bucketCount];

        double samplesPerBucket = (double)pcm.Length / bucketCount;

        for (int b = 0; b < bucketCount; b++)
        {
            int start = (int)(b * samplesPerBucket);
            int end = Math.Min(pcm.Length, (int)((b + 1) * samplesPerBucket));
            if (end <= start) end = Math.Min(pcm.Length, start + 1);

            float min = 0f;
            float max = 0f;

            for (int i = start; i < end; i++)
            {
                float val = pcm[i];
                if (val < min) min = val;
                if (val > max) max = val;
            }

            minPeaks[b] = min;
            maxPeaks[b] = max;
        }

        return new WaveformEnvelope(minPeaks, maxPeaks, duration, bucketCount);
    }
}
