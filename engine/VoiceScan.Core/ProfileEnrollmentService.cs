namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class ProfileEnrollmentService
{
    /// <summary>
    /// Only this much audio from the start of each enrollment clip is used (and quality-checked). Enrollment needs
    /// seconds of clean speech, and the cap keeps a long file from exhausting memory.
    /// </summary>
    public const double MaxSecondsPerClip = 600.0;

    private const int EmbeddingBatchSize = 32;

    private readonly ISpeakerEmbeddingModel _embeddingModel;
    private readonly WebRtcVad _vad;

    public ProfileEnrollmentService(ISpeakerEmbeddingModel embeddingModel, WebRtcVad vad)
    {
        _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
    }

    public ISpeakerEmbeddingModel EmbeddingModel => _embeddingModel;

    /// <exception cref="InvalidDataException">A clip contains no detectable speech.</exception>
    public async Task<VoiceProfile> EnrollProfileAsync(
        IReadOnlyList<string> audioFilePaths,
        string profileName,
        bool multiCondition = false,
        CancellationToken cancellationToken = default)
    {
        if (audioFilePaths.Count == 0)
        {
            throw new ArgumentException("At least one audio file is required for enrollment.", nameof(audioFilePaths));
        }

        var allEmbeddings = new List<float[]>();
        double totalSpeechDuration = 0.0;

        foreach (var path in audioFilePaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Enrollment audio file not found: {path}");
            }

            float[] audio = await AudioDecoder.DecodeEntireFileAsync(
                path, cancellationToken: cancellationToken, maxDurationSeconds: MaxSecondsPerClip);
            var intervals = _vad.DetectSpeechIntervals(audio);
            var plans = SpeechWindowExtractor.PlanWindows(audio.Length, intervals);
            if (plans.Count == 0)
            {
                throw new InvalidDataException(
                    $"No speech was detected in '{Path.GetFileName(path)}'. Use a recording where the person talks clearly for a few seconds.");
            }

            foreach (var interval in intervals)
            {
                totalSpeechDuration += interval.EndTimeSeconds - interval.StartTimeSeconds;
            }

            var sources = new List<float[]> { audio };
            if (multiCondition)
            {
                // Multi-condition enrollment: augment with AGC, game noise, and codec degradation
                sources.Add(AudioAugmenter.ApplyAgc(audio));
                sources.Add(AudioAugmenter.ApplyNoiseMix(audio, targetSnrDb: 12.0));
                sources.Add(AudioAugmenter.ApplyCodecDegradation(audio));
            }

            // Windows are materialized one batch at a time so a long clip never holds every window in memory.
            foreach (var source in sources)
            {
                for (int i = 0; i < plans.Count; i += EmbeddingBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = new List<float[]>(EmbeddingBatchSize);
                    for (int k = i; k < Math.Min(i + EmbeddingBatchSize, plans.Count); k++)
                    {
                        var plan = plans[k];
                        var windowSource = source.AsSpan((int)plan.SourceStart, plan.SourceLength).ToArray();
                        batch.Add(SpeechWindowExtractor.Materialize(plan, windowSource).AudioSamples);
                    }
                    allEmbeddings.AddRange(_embeddingModel.ExtractEmbeddingsBatch(batch));
                }
            }
        }

        return new VoiceProfile
        {
            ProfileName = profileName,
            ModelId = _embeddingModel.ModelId,
            ModelVersion = _embeddingModel.ModelVersion,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Centroid = ComputeCentroid(allEmbeddings),
            EnrollmentEmbeddings = allEmbeddings,
            ClipCount = audioFilePaths.Count,
            TotalSpeechDurationSeconds = Math.Round(totalSpeechDuration, 3)
        };
    }

    /// <summary>L2-normalized mean of unit embeddings: the profile centroid.</summary>
    public static float[] ComputeCentroid(IReadOnlyList<float[]> embeddings)
    {
        if (embeddings.Count == 0)
        {
            throw new ArgumentException("At least one embedding is required.", nameof(embeddings));
        }

        int dim = embeddings[0].Length;
        double[] sum = new double[dim];
        foreach (var emb in embeddings)
        {
            if (emb.Length != dim)
            {
                throw new ArgumentException($"Embeddings differ in dimension ({emb.Length} vs {dim}).", nameof(embeddings));
            }
            for (int d = 0; d < dim; d++) sum[d] += emb[d];
        }

        double norm = Math.Sqrt(sum.Sum(x => x * x));
        float[] centroid = new float[dim];
        for (int d = 0; d < dim; d++)
        {
            centroid[d] = norm > 1e-12 ? (float)(sum[d] / norm) : 0f;
        }
        return centroid;
    }
}
