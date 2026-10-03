namespace VoiceScan.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VoiceScan.Core;
using Xunit;

public class CoreEngineTests
{
    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "VoiceScan.sln")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    [Fact]
    public void Filterbank_Computes80DimFbankAndNormalizesMean()
    {
        int sr = 16000;
        int samples = 2 * sr; // 2 seconds
        float[] audio = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            audio[i] = 0.5f * MathF.Sin(2f * MathF.PI * 220f * i / sr);
        }

        float[,] fbank = Filterbank.ComputeFbank(audio);
        Assert.True(fbank.GetLength(0) > 100, $"Expected >100 frames, got {fbank.GetLength(0)}");
        Assert.Equal(80, fbank.GetLength(1));

        // Verify Cepstral Mean Normalization: column mean should be ~0.0
        for (int m = 0; m < 80; m++)
        {
            float sum = 0f;
            for (int f = 0; f < fbank.GetLength(0); f++)
            {
                sum += fbank[f, m];
            }
            float mean = sum / fbank.GetLength(0);
            Assert.True(MathF.Abs(mean) < 1e-4f, $"CMN failed for mel bin {m}: mean was {mean}");
        }
    }

    [Theory]
    [InlineData("fbank", FbankProfile.WeSpeaker)]
    [InlineData("fbank_campplus", FbankProfile.CamPlusPlus)]
    public void Filterbank_MatchesKaldiNativeFbankGolden(string expectedKey, FbankProfile profile)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fbank_golden.json")));
        float[] audio = doc.RootElement.GetProperty("audio").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        float[][] expected = doc.RootElement.GetProperty(expectedKey).EnumerateArray()
            .Select(r => r.EnumerateArray().Select(e => e.GetSingle()).ToArray()).ToArray();

        float[,] actual = Filterbank.ComputeFbank(audio, profile);

        Assert.Equal(expected.Length, actual.GetLength(0));
        float maxDiff = 0f;
        for (int f = 0; f < expected.Length; f++)
        {
            for (int m = 0; m < 80; m++)
            {
                maxDiff = MathF.Max(maxDiff, MathF.Abs(expected[f][m] - actual[f, m]));
            }
        }
        Assert.True(maxDiff < 2e-3f, $"Max deviation from kaldi-native-fbank was {maxDiff}");
    }

    [Fact]
    public async Task AudioDecoder_StreamsChunksFromWavFile()
    {
        var root = FindRepoRoot();
        var testWav = Path.Combine(root, "eval", "dev_dataset", "audio", "dev_clip_0000.wav");
        Assert.True(File.Exists(testWav), $"Test audio missing at {testWav}");

        int chunkCount = 0;
        long totalSamples = 0;

        await foreach (var chunk in AudioDecoder.StreamDecodeAsync(testWav, chunkSize: 16000))
        {
            chunkCount++;
            totalSamples += chunk.Samples.Length;
            Assert.True(chunk.Samples.Length > 0);
        }

        Assert.True(chunkCount >= 5, $"Expected at least 5 chunks for 10s audio, got {chunkCount}");
        Assert.Equal(160000, totalSamples); // 10s @ 16kHz
    }

    [Fact]
    public async Task AudioDecoder_ProbeAudioTracks_FindsTrack()
    {
        var root = FindRepoRoot();
        var testWav = Path.Combine(root, "eval", "dev_dataset", "audio", "dev_clip_0000.wav");
        Assert.True(File.Exists(testWav));

        var tracks = await AudioDecoder.ProbeAudioTracksAsync(testWav);
        Assert.NotEmpty(tracks);
        Assert.Equal(0, tracks[0].Index);
        Assert.Equal(16000, tracks[0].SampleRate);
    }

    [Fact]
    public void SpeechWindowExtractor_ExtractsWindowsCorrectly()
    {
        int sr = 16000;
        float[] dummyAudio = new float[5 * sr]; // 5 seconds

        var intervals = new[]
        {
            new SpeechInterval(0.5, 3.5) // 3 seconds duration
        };

        var windows = SpeechWindowExtractor.ExtractWindows(dummyAudio, intervals, sampleRate: sr, windowSec: 2.0, hopSec: 1.0);

        // A 3-second interval with 2.0s window and 1.0s hop yields 2 windows: [0.5, 2.5] and [1.5, 3.5]
        Assert.Equal(2, windows.Count);
        Assert.Equal(0.5, windows[0].StartTimeSeconds, precision: 2);
        Assert.Equal(2.5, windows[0].EndTimeSeconds, precision: 2);
        Assert.Equal(32000, windows[0].AudioSamples.Length);

        Assert.Equal(1.5, windows[1].StartTimeSeconds, precision: 2);
        Assert.Equal(3.5, windows[1].EndTimeSeconds, precision: 2);
    }

    [Fact]
    public void SpeechWindowExtractor_TilesShortIntervalsAndReportsRealExtent()
    {
        int sr = 16000;
        float[] audio = new float[2 * sr];
        for (int i = 0; i < audio.Length; i++) audio[i] = i + 1;

        var windows = SpeechWindowExtractor.ExtractWindows(audio, new[] { new SpeechInterval(0.2, 1.0) }, sampleRate: sr);

        Assert.Single(windows);
        Assert.Equal(32000, windows[0].AudioSamples.Length);
        Assert.Equal(0.2, windows[0].StartTimeSeconds, precision: 3);
        Assert.Equal(1.0, windows[0].EndTimeSeconds, precision: 3);
        Assert.All(windows[0].AudioSamples, v => Assert.NotEqual(0f, v));
    }

    [Fact]
    public void SpeechWindowExtractor_SkipsSegmentsBelowMinimum()
    {
        float[] audio = new float[16000];
        var windows = SpeechWindowExtractor.ExtractWindows(audio, new[] { new SpeechInterval(0.0, 0.4) });
        Assert.Empty(windows);
    }

    [Fact]
    public void SpeechWindowExtractor_AddsEndAlignedTailWindow()
    {
        int sr = 16000;
        float[] audio = new float[5 * sr];

        var windows = SpeechWindowExtractor.ExtractWindows(audio, new[] { new SpeechInterval(0.0, 3.6) }, sampleRate: sr);

        // Hop grid covers [0,2] and [1,3]; the last 0.6 s is covered by [1.6, 3.6].
        Assert.Equal(3, windows.Count);
        Assert.Equal(1.6, windows[2].StartTimeSeconds, precision: 3);
        Assert.Equal(3.6, windows[2].EndTimeSeconds, precision: 3);
    }

    [Fact]
    public void SileroVad_Hysteresis_BridgesShortDipsAndDropsBlips()
    {
        // 32 ms frames. Speech with a one-frame dip (kept), a long gap, then a 3-frame blip (< 0.25 s, dropped).
        var probs = new float[100];
        for (int i = 5; i < 30; i++) probs[i] = 0.9f;
        probs[15] = 0.4f; // between negative threshold (0.35) and threshold: stays in speech
        for (int i = 70; i < 73; i++) probs[i] = 0.9f;

        var intervals = SileroVad.ProbabilitiesToIntervals(probs, totalSeconds: 100 * 0.032, threshold: 0.5f);

        Assert.Single(intervals);
        Assert.Equal(5 * 0.032 - 0.03, intervals[0].StartTimeSeconds, precision: 3);
        Assert.Equal(30 * 0.032 + 0.03, intervals[0].EndTimeSeconds, precision: 3);
    }

    [Fact]
    public void SileroVad_RealSpeech_IsDetectedAndPureToneIsNot()
    {
        using var vad = new SileroVad();
        float[] speech = AudioDecoder.DecodeEntireFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk_speech.wav")).GetAwaiter().GetResult();
        float[] tone = new float[16000 * 5];
        for (int i = 0; i < tone.Length; i++) tone[i] = 0.4f * MathF.Sin(2f * MathF.PI * 440f * i / 16000);

        Assert.NotEmpty(vad.DetectSpeechIntervals(speech));
        Assert.Empty(vad.DetectSpeechIntervals(tone));
    }

    [Fact]
    public void OnnxEmbeddingModel_EcapaAndTitaNet_ProduceUnitNormVectors()
    {
        var root = FindRepoRoot();
        var ecapaPath = Path.Combine(root, "models", "ecapa_tdnn.onnx");
        var titanetPath = Path.Combine(root, "models", "titanet_small.onnx");

        float[] testAudio = new float[32000];
        for (int i = 0; i < testAudio.Length; i++)
        {
            testAudio[i] = 0.5f * MathF.Sin(2f * MathF.PI * 300f * i / 16000);
        }

        // 1. Test SpeechBrain ECAPA-TDNN
        using (var ecapaModel = new OnnxEmbeddingModel(ecapaPath))
        {
            Assert.Equal("speechbrain-ecapa-tdnn", ecapaModel.ModelId);
            Assert.Equal(192, ecapaModel.EmbeddingDimension);

            var emb = ecapaModel.ExtractEmbedding(testAudio);
            Assert.Equal(192, emb.Length);

            float norm = MathF.Sqrt(emb.Sum(x => x * x));
            Assert.True(MathF.Abs(norm - 1.0f) < 1e-4f, $"Expected unit norm, got {norm}");
        }

        // 2. Test NVIDIA NeMo TitaNet
        using (var titanetModel = new OnnxEmbeddingModel(titanetPath))
        {
            Assert.Equal("nvidia-titanet-small", titanetModel.ModelId);
            Assert.Equal(192, titanetModel.EmbeddingDimension);

            var emb = titanetModel.ExtractEmbedding(testAudio);
            Assert.Equal(192, emb.Length);

            float norm = MathF.Sqrt(emb.Sum(x => x * x));
            Assert.True(MathF.Abs(norm - 1.0f) < 1e-4f, $"Expected unit norm, got {norm}");
        }
    }

    [Fact]
    public async Task ProfileEnrollmentService_EnrollsAndSerializesProfile()
    {
        var root = FindRepoRoot();
        var enrollDir = Path.Combine(root, "eval", "dev_dataset", "enrollment", "speaker_charlie");
        var clips = Directory.GetFiles(enrollDir, "*.wav");
        Assert.NotEmpty(clips);

        using var embeddingModel = new OnnxEmbeddingModel("ecapa");
        using var vad = new SileroVad();

        var service = new ProfileEnrollmentService(embeddingModel, vad);
        var profile = await service.EnrollProfileAsync(clips, "speaker_charlie");

        Assert.Equal("speaker_charlie", profile.ProfileName);
        Assert.Equal("speechbrain-ecapa-tdnn", profile.ModelId);
        Assert.Equal(192, profile.Centroid.Length);
        Assert.True(profile.EnrollmentEmbeddings.Count >= 2);

        // Verify centroid is unit normalized
        float norm = MathF.Sqrt(profile.Centroid.Sum(x => x * x));
        Assert.True(MathF.Abs(norm - 1.0f) < 1e-4f);

        // Verify JSON serialization roundtrip
        string tempJson = Path.GetTempFileName();
        try
        {
            profile.SaveToFile(tempJson);
            var loaded = VoiceProfile.LoadFromFile(tempJson);

            Assert.Equal(profile.ProfileName, loaded.ProfileName);
            Assert.Equal(profile.ModelId, loaded.ModelId);
            Assert.Equal(profile.Centroid.Length, loaded.Centroid.Length);
        }
        finally
        {
            if (File.Exists(tempJson)) File.Delete(tempJson);
        }
    }

    [Fact]
    public void SimilarityScorer_MergeAdjacentHits_FusesSegments()
    {
        var hits = new[]
        {
            (Start: 1.0, End: 3.0, Confidence: 0.65),
            (Start: 2.0, End: 4.0, Confidence: 0.85),
            (Start: 3.0, End: 5.0, Confidence: 0.70),
            (Start: 8.0, End: 10.0, Confidence: 0.60)
        };

        var segments = SimilarityScorer.MergeAdjacentHits(hits, mergeToleranceSec: 1.0);

        Assert.Equal(2, segments.Count);
        // First merged segment: [1.0, 5.0] with max confidence 0.85
        Assert.Equal(1.0, segments[0].StartTimeSeconds);
        Assert.Equal(5.0, segments[0].EndTimeSeconds);
        Assert.Equal(0.85, segments[0].Confidence);

        // Second isolated segment: [8.0, 10.0] with confidence 0.60
        Assert.Equal(8.0, segments[1].StartTimeSeconds);
        Assert.Equal(10.0, segments[1].EndTimeSeconds);
        Assert.Equal(0.60, segments[1].Confidence);
    }

    [Fact]
    public async Task PipelineScanner_ScansFileConcurrently()
    {
        var root = FindRepoRoot();
        var clipPath = Path.Combine(root, "eval", "dev_dataset", "audio", "dev_clip_0006.wav");
        var enrollDir = Path.Combine(root, "eval", "dev_dataset", "enrollment", "speaker_charlie");
        var clips = Directory.GetFiles(enrollDir, "*.wav");

        using var embeddingModel = new OnnxEmbeddingModel("ecapa");
        using var vad = new SileroVad();

        var service = new ProfileEnrollmentService(embeddingModel, vad);
        var profile = await service.EnrollProfileAsync(clips, "speaker_charlie");

        var scanner = new PipelineScanner(embeddingModel, vad);
        var result = await scanner.ScanFileAsync(clipPath, profile, threshold: 0.48);

        Assert.Equal("dev_clip_0006.wav", result.ClipId);
        Assert.True(result.DurationSeconds > 9.0);
        Assert.NotNull(result.Verdict);
        Assert.True(result.MaxConfidence > 0.0);
    }

    [Fact]
    public void SpeakerClusterer_SeparatesDistinctSpeakersIntoClusters()
    {
        // 4 windows: windows 0, 1 from speaker A (pointing along axis 0), windows 2, 3 from speaker B (pointing along axis 1)
        int dim = 16;
        float[] spkA1 = new float[dim]; spkA1[0] = 1.0f;
        float[] spkA2 = new float[dim]; spkA2[0] = 0.96f; spkA2[1] = 0.28f; // ~0.96 cosine sim with A1
        float[] spkB1 = new float[dim]; spkB1[2] = 1.0f;
        float[] spkB2 = new float[dim]; spkB2[2] = 0.96f; spkB2[3] = 0.28f; // ~0.96 cosine sim with B1

        var windows = new List<WindowItem>
        {
            new(0, 0.0, 2.0, spkA1),
            new(1, 1.0, 3.0, spkA2),
            new(2, 5.0, 7.0, spkB1),
            new(3, 6.0, 8.0, spkB2)
        };

        // Cosine distance threshold 0.40 (requires sim >= 0.60 to merge)
        var clusters = SpeakerClusterer.ClusterWindows(windows, distanceThreshold: 0.40);

        Assert.Equal(2, clusters.Count);
        // Each cluster should contain exactly 2 windows
        var c0 = clusters.First(c => c.Windows.Any(w => w.Index == 0));
        var c1 = clusters.First(c => c.Windows.Any(w => w.Index == 2));

        Assert.Contains(c0.Windows, w => w.Index == 1);
        Assert.Contains(c1.Windows, w => w.Index == 3);
        Assert.Equal(2, c0.Windows.Count);
        Assert.Equal(2, c1.Windows.Count);

        // Verify centroids are unit normalized
        float norm0 = MathF.Sqrt(c0.Centroid.Sum(x => x * x));
        float norm1 = MathF.Sqrt(c1.Centroid.Sum(x => x * x));
        Assert.True(MathF.Abs(norm0 - 1.0f) < 1e-4f);
        Assert.True(MathF.Abs(norm1 - 1.0f) < 1e-4f);
    }

    [Fact]
    public void PipelineScanner_ScoreAndAggregate_SuppressesTransientSpikeViaClusterCentroid()
    {
        // Target profile centroid along axis 0
        int dim = 16;
        float[] targetCentroid = new float[dim]; targetCentroid[0] = 1.0f;
        var profile = new VoiceProfile
        {
            ProfileName = "test_target",
            ModelId = "test-model",
            Centroid = targetCentroid
        };

        // Impostor speaker with 3 windows: 2 are completely orthogonal to target, 1 has transient similarity 0.49
        float[] imp1 = new float[dim]; imp1[1] = 1.0f; // sim = 0.0
        float[] imp2 = new float[dim]; imp2[1] = 0.98f; imp2[2] = 0.20f; // sim = 0.0
        float[] impSpike = new float[dim]; impSpike[0] = 0.50f; impSpike[1] = 0.866f; // sim = 0.50 with target!

        var windows = new List<WindowItem>
        {
            new(0, 0.0, 2.0, imp1),
            new(1, 1.0, 3.0, imp2),
            new(2, 2.0, 4.0, impSpike)
        };

        // Window-level scoring (without temporal smoothing): threshold 0.48 -> impSpike triggers a false alarm!
        var (unclusteredSegs, unclusteredMax, unclusteredVerdict) = PipelineScanner.ScoreAndAggregate(
            windows, profile, threshold: 0.48, enableClustering: false, enableTemporalSmoothing: false);
        Assert.Single(unclusteredSegs); // False alarm emitted!
        Assert.True(unclusteredMax >= 0.48);

        // Cluster-level scoring: windows merge into impostor cluster, centroid score pulled down -> NO false alarm!
        var (clusteredSegs, clusteredMax, clusteredVerdict) = PipelineScanner.ScoreAndAggregate(
            windows, profile, threshold: 0.48, clusterDistanceThreshold: 0.50, enableClustering: true);
        Assert.Empty(clusteredSegs); // False alarm suppressed!
        Assert.True(clusteredMax < 0.48);
        Assert.Equal("No match", clusteredVerdict);
    }

    [Fact]
    public void SimilarityScorer_TemporalSmoothingAndAggregation_DropsIsolatedWeakWindows()
    {
        // 3 hits:
        // Hit 1 & 2: adjacent at 1.0s and 2.0s with score 0.50 (has neighboring support)
        // Hit 3: isolated at 20.0s with score 0.49 (weak, threshold 0.48, peakDelta 0.04 -> requires >= 0.52 to survive alone)
        var hits = new[]
        {
            (Start: 1.0, End: 3.0, Confidence: 0.50),
            (Start: 2.0, End: 4.0, Confidence: 0.51),
            (Start: 20.0, End: 22.0, Confidence: 0.49) // Isolated weak window
        };

        var smoothed = SimilarityScorer.TemporalSmoothingAndAggregation(
            hits,
            baseThreshold: 0.48,
            peakDelta: 0.04,
            neighborToleranceSec: 2.0,
            mergeToleranceSec: 1.0);

        // Only the first adjacent pair [1.0, 4.0] should survive; isolated 20.0s hit is dropped!
        Assert.Single(smoothed);
        Assert.Equal(1.0, smoothed[0].StartTimeSeconds);
        Assert.Equal(4.0, smoothed[0].EndTimeSeconds);

        // However, if the isolated hit has a strong peak score (e.g. 0.60 >= 0.52), it is preserved!
        var strongHits = new[]
        {
            (Start: 20.0, End: 22.0, Confidence: 0.60)
        };
        var strongSmoothed = SimilarityScorer.TemporalSmoothingAndAggregation(
            strongHits,
            baseThreshold: 0.48,
            peakDelta: 0.04,
            minDurationSec: 1.0);
        Assert.Single(strongSmoothed);
        Assert.Equal(20.0, strongSmoothed[0].StartTimeSeconds);
    }

    [Fact]
    public void ScoreNormalizer_ComputesAsNormAndCalibrates()
    {
        int dim = 16;
        var targetCentroid = new float[dim]; targetCentroid[0] = 1.0f;
        var matchingTest = new float[dim]; matchingTest[0] = 0.95f; matchingTest[1] = 0.31f; // High sim ~0.95
        var nonMatchingTest = new float[dim]; nonMatchingTest[2] = 1.0f; // Low sim = 0.0

        // Create an impostor cohort pointing mostly in orthogonal axes (e.g. axis 2, 3, 4)
        var cohort = new List<float[]>();
        for (int i = 0; i < 10; i++)
        {
            var imp = new float[dim];
            imp[2 + (i % 6)] = 0.9f;
            imp[0] = 0.1f * (i % 3); // minor random overlap
            // normalize
            float norm = MathF.Sqrt(imp.Sum(x => x * x));
            for (int d = 0; d < dim; d++) imp[d] /= norm;
            cohort.Add(imp);
        }

        var normalizer = new ScoreNormalizer(cohort, topK: 10);
        Assert.Equal(10, normalizer.CohortSize);

        // Matching test vector: z-score should be significantly positive (> 3.0)
        float rawMatch = SimilarityScorer.CosineSimilarity(matchingTest, targetCentroid);
        double zMatch = normalizer.NormalizeScore(rawMatch, targetCentroid, matchingTest);
        Assert.True(zMatch > 3.0, $"Expected zMatch > 3.0, got {zMatch}");

        double confMatch = ScoreNormalizer.CalibrateZScoreToConfidence(zMatch);
        Assert.True(confMatch > 0.70, $"Expected high calibrated confidence, got {confMatch}");

        // Non-matching test vector: z-score should be near zero or negative
        float rawNonMatch = SimilarityScorer.CosineSimilarity(nonMatchingTest, targetCentroid);
        double zNonMatch = normalizer.NormalizeScore(rawNonMatch, targetCentroid, nonMatchingTest);
        Assert.True(zNonMatch < 1.0, $"Expected zNonMatch < 1.0, got {zNonMatch}");

        double confNonMatch = ScoreNormalizer.CalibrateZScoreToConfidence(zNonMatch);
        Assert.True(confNonMatch < 0.35, $"Expected low calibrated confidence, got {confNonMatch}");
    }

    [Fact]
    public void AcousticDiagnostics_EstimatesSnrAndAttachesReasonFlags()
    {
        // 1s tone burst + 1s near-silent background
        int sr = 16000;
        float[] clean = new float[sr * 2];
        for (int i = 0; i < sr; i++)
        {
            clean[i] = 0.5f * MathF.Sin(2f * MathF.PI * 440f * i / sr);
        }

        double snrClean = AcousticDiagnostics.EstimateSnrDb(clean, sr);
        Assert.True(snrClean > 15.0, $"Expected clean SNR > 15 dB, got {snrClean}");

        // Short segment (<1.5s) and low SNR (<10 dB)
        var flags = AcousticDiagnostics.EvaluateReasonFlags(durationSeconds: 1.0, snrDb: 6.0, isOverlap: true, isCodecDegraded: false);
        Assert.Contains("SHORT_SEGMENT", flags);
        Assert.Contains("LOW_SNR", flags);
        Assert.Contains("SUSPECTED_OVERLAP", flags);
    }

    [Fact]
    public void AudioAugmenter_AppliesAgcNoiseAndCodecAugmentations()
    {
        int sr = 16000;
        float[] audio = new float[sr];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = 0.4f * MathF.Sin(2f * MathF.PI * 300f * i / sr);
        }

        float[] agc = AudioAugmenter.ApplyAgc(audio);
        Assert.Equal(audio.Length, agc.Length);

        float[] mixed = AudioAugmenter.ApplyNoiseMix(audio, targetSnrDb: 10.0);
        Assert.Equal(audio.Length, mixed.Length);

        float[] codec = AudioAugmenter.ApplyCodecDegradation(audio);
        Assert.Equal(audio.Length, codec.Length);
    }

    [Fact]
    public void ScoreNormalizer_NormalizeScoreWithTargetStats_MatchesNormalizeScore()
    {
        var rng = new Random(42);
        int dim = 192;
        var cohort = new List<float[]>();
        for (int i = 0; i < 10; i++)
        {
            float[] vec = new float[dim];
            for (int d = 0; d < dim; d++) vec[d] = (float)rng.NextDouble();
            cohort.Add(vec);
        }

        var normalizer = new ScoreNormalizer(cohort, topK: 5);
        float[] target = new float[dim];
        float[] test = new float[dim];
        for (int d = 0; d < dim; d++)
        {
            target[d] = (float)rng.NextDouble();
            test[d] = (float)rng.NextDouble();
        }

        float rawScore = 0.72f;
        double zDefault = normalizer.NormalizeScore(rawScore, target, test);

        var targetStats = normalizer.ComputeCohortStats(target);
        double zCached = normalizer.NormalizeScoreWithTargetStats(rawScore, targetStats, test);

        Assert.Equal(zDefault, zCached, precision: 6);
    }

    [Fact]
    public void SpeakerClusterer_NearestNeighborClustering_ProducesValidClusters()
    {
        var rng = new Random(123);
        int dim = 64;

        // Create 2 distinct orthogonal speakers (each with 3 windows)
        float[] speakerA = new float[dim];
        float[] speakerB = new float[dim];
        for (int d = 0; d < dim / 2; d++) speakerA[d] = 1.0f;
        for (int d = dim / 2; d < dim; d++) speakerB[d] = 1.0f;

        // Unit normalize
        float normA = MathF.Sqrt(speakerA.Sum(x => x * x));
        float normB = MathF.Sqrt(speakerB.Sum(x => x * x));
        for (int d = 0; d < dim; d++)
        {
            speakerA[d] /= normA;
            speakerB[d] /= normB;
        }

        var windows = new List<WindowItem>();
        for (int i = 0; i < 3; i++)
        {
            float[] w = (float[])speakerA.Clone();
            w[0] += (float)(rng.NextDouble() * 0.01);
            windows.Add(new WindowItem(windows.Count, i * 2.0, (i + 1) * 2.0, w));
        }
        for (int i = 0; i < 3; i++)
        {
            float[] w = (float[])speakerB.Clone();
            w[0] += (float)(rng.NextDouble() * 0.01);
            windows.Add(new WindowItem(windows.Count, (i + 3) * 2.0, (i + 4) * 2.0, w));
        }

        var clusters = SpeakerClusterer.ClusterWindows(windows, distanceThreshold: 0.15);
        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.Equal(3, c.Windows.Count));
    }

    [Fact]
    public async Task AudioDecoder_GetMediaDurationSecondsAsync_ReturnsAccurateDuration()
    {
        var root = FindRepoRoot();
        var testWav = Path.Combine(root, "eval", "dev_dataset", "audio", "dev_clip_0000.wav");
        Assert.True(File.Exists(testWav));

        double duration = await AudioDecoder.GetMediaDurationSecondsAsync(testWav);
        Assert.True(duration > 9.0 && duration < 11.0, $"Expected ~10s duration, got {duration}");
    }

    [Fact]
    public async Task AudioDecoder_ExtractAudioSegmentAsync_ExtractsValidClip()
    {
        var root = FindRepoRoot();
        var testWav = Path.Combine(root, "eval", "dev_dataset", "audio", "dev_clip_0000.wav");
        Assert.True(File.Exists(testWav));

        string tempClip = Path.Combine(Path.GetTempPath(), $"voicescan_test_clip_{Guid.NewGuid():N}.wav");
        try
        {
            await AudioDecoder.ExtractAudioSegmentAsync(testWav, tempClip, startTimeSeconds: 1.0, durationSeconds: 2.0);
            Assert.True(File.Exists(tempClip));

            double dur = await AudioDecoder.GetMediaDurationSecondsAsync(tempClip);
            Assert.True(dur > 1.8 && dur < 2.2, $"Expected ~2s extracted clip, got {dur}");
        }
        finally
        {
            if (File.Exists(tempClip)) File.Delete(tempClip);
        }
    }
}
