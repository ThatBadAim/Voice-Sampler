namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class ProfileEnrollmentService
{
    private readonly ISpeakerEmbeddingModel _embeddingModel;
    private readonly SileroVad _vad;

    public ProfileEnrollmentService(ISpeakerEmbeddingModel embeddingModel, SileroVad vad)
    {
        _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
    }

    public async Task<VoiceProfile> EnrollProfileAsync(
        IReadOnlyList<string> audioFilePaths,
        string profileName,
        float vadThreshold = 0.5f,
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

            float[] audio = await AudioDecoder.DecodeEntireFileAsync(path, cancellationToken: cancellationToken);
            var intervals = _vad.DetectSpeechIntervals(audio, vadThreshold);

            if (intervals.Count == 0)
            {
                intervals = new[] { new SpeechInterval(0.0, (double)audio.Length / 16000) };
            }

            foreach (var interval in intervals)
            {
                totalSpeechDuration += (interval.EndTimeSeconds - interval.StartTimeSeconds);
            }

            var cleanWindows = SpeechWindowExtractor.ExtractWindows(audio, intervals);
            var windowAudios = cleanWindows.Select(w => w.AudioSamples).ToList();

            if (multiCondition && cleanWindows.Count > 0)
            {
                // Multi-condition enrollment: augment with AGC, game noise, and codec degradation
                float[] agcAudio = AudioAugmenter.ApplyAgc(audio);
                float[] noiseAudio = AudioAugmenter.ApplyNoiseMix(audio, targetSnrDb: 12.0);
                float[] codecAudio = AudioAugmenter.ApplyCodecDegradation(audio);

                var agcWindows = SpeechWindowExtractor.ExtractWindows(agcAudio, intervals);
                var noiseWindows = SpeechWindowExtractor.ExtractWindows(noiseAudio, intervals);
                var codecWindows = SpeechWindowExtractor.ExtractWindows(codecAudio, intervals);

                windowAudios.AddRange(agcWindows.Select(w => w.AudioSamples));
                windowAudios.AddRange(noiseWindows.Select(w => w.AudioSamples));
                windowAudios.AddRange(codecWindows.Select(w => w.AudioSamples));
            }

            if (windowAudios.Count > 0)
            {
                var embeddings = _embeddingModel.ExtractEmbeddingsBatch(windowAudios);
                allEmbeddings.AddRange(embeddings);
            }
        }

        if (allEmbeddings.Count == 0)
        {
            throw new InvalidOperationException($"No speech windows could be extracted from enrollment audio files for {profileName}.");
        }

        // Calculate centroid: average of all window embeddings, then L2-normalize
        int dim = _embeddingModel.EmbeddingDimension;
        float[] centroid = new float[dim];

        foreach (var emb in allEmbeddings)
        {
            for (int d = 0; d < dim; d++)
            {
                centroid[d] += emb[d];
            }
        }

        float sumSq = 0f;
        for (int d = 0; d < dim; d++)
        {
            centroid[d] /= allEmbeddings.Count;
            sumSq += centroid[d] * centroid[d];
        }

        float norm = MathF.Sqrt(sumSq + 1e-12f);
        for (int d = 0; d < dim; d++)
        {
            centroid[d] /= norm;
        }

        return new VoiceProfile
        {
            ProfileName = profileName,
            ModelId = _embeddingModel.ModelId,
            CreatedAt = DateTime.UtcNow.ToString("o"),
            Centroid = centroid,
            EnrollmentEmbeddings = allEmbeddings,
            ClipCount = audioFilePaths.Count,
            TotalSpeechDurationSeconds = Math.Round(totalSpeechDuration, 3)
        };
    }
}
