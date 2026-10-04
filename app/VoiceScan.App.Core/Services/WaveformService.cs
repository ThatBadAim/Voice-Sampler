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

        var accumulator = new EnvelopeAccumulator();
        await foreach (var chunk in AudioDecoder.StreamDecodeAsync(audioFilePath, cancellationToken: cancellationToken))
        {
            accumulator.Add(chunk.Samples);
        }

        if (accumulator.SampleCount == 0)
        {
            return new WaveformEnvelope([], [], 0.0, 0);
        }

        var (minPeaks, maxPeaks) = accumulator.Build(Math.Max(1, targetBuckets));
        return new WaveformEnvelope(minPeaks, maxPeaks, (double)accumulator.SampleCount / 16000.0, minPeaks.Length);
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

public static class WaveformPreview
{
    /// <summary>Shows or hides the item's waveform, decoding it on first use.</summary>
    public static async Task ToggleAsync(MediaFileItem item, IWaveformService service, CancellationToken cancellationToken = default)
    {
        if (item.IsWaveformVisible)
        {
            item.IsWaveformVisible = false;
            return;
        }

        item.IsWaveformVisible = true;
        if (item.Waveform is not null || item.IsLoadingWaveform) return;

        item.IsLoadingWaveform = true;
        item.WaveformError = null;
        try
        {
            var envelope = await service.GenerateEnvelopeAsync(item.Path, cancellationToken: cancellationToken);
            if (envelope.BucketCount == 0) item.WaveformError = "No audio could be decoded from this file.";
            else item.Waveform = envelope;
        }
        catch (OperationCanceledException)
        {
            item.IsWaveformVisible = false;
        }
        catch (Exception ex)
        {
            item.WaveformError = $"Could not draw waveform: {ex.Message}";
        }
        finally
        {
            item.IsLoadingWaveform = false;
        }
    }
}
