namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Storage;

/// <summary>
/// Executes pipelined scanning of media files against an enrolled voice profile.
/// When an optional VoiceScanDatabase is provided, audio decode, VAD, and GPU embeddings are cached,
/// allowing subsequent scans with new/updated profiles to complete in a fraction of the time.
/// </summary>
public sealed class PipelineScanner
{
    public const int DefaultScoreSmoothingRadius = 0;
    public const string ErrorVerdict = "Error";

    /// <summary>Files decoded ahead while the current one is embedded.</summary>
    public const int FilePrefetchDepth = 2;

    private const int GpuBatchSize = 64;
    private const int CpuBatchSize = 16;

    private readonly ISpeakerEmbeddingModel _embeddingModel;
    private readonly WebRtcVad _vad;
    private readonly VoiceScanDatabase? _database;
    private readonly IInferenceClient? _sidecarClient;
    private readonly int _batchSize;

    public PipelineScanner(
        ISpeakerEmbeddingModel embeddingModel,
        WebRtcVad vad,
        VoiceScanDatabase? database = null,
        int? batchSize = null,
        IInferenceClient? sidecarClient = null)
    {
        _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
        _database = database;
        _batchSize = Math.Max(1, batchSize ?? (embeddingModel.IsCudaActive ? GpuBatchSize : CpuBatchSize));
        _sidecarClient = sidecarClient;
    }

    public static string EngineVersion { get; } = typeof(PipelineScanner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public ISpeakerEmbeddingModel EmbeddingModel => _embeddingModel;
    public WebRtcVad Vad => _vad;
    public IInferenceClient? SidecarClient => _sidecarClient;

    /// <summary>
    /// Throws unless <paramref name="profile"/> was enrolled with exactly this model, weights and front-end:
    /// embeddings from different models (even ones with the same dimension) are not comparable.
    /// </summary>
    public void EnsureCompatible(VoiceProfile profile)
    {
        if (profile.ModelVersion != _embeddingModel.ModelVersion)
        {
            string enrolledWith = string.IsNullOrEmpty(profile.ModelVersion)
                ? $"an earlier VoiceScan version ({(string.IsNullOrEmpty(profile.ModelId) ? "unknown model" : profile.ModelId)})"
                : profile.ModelVersion;
            throw new InvalidOperationException(
                $"Voice profile '{profile.ProfileName}' was enrolled with {enrolledWith}, but this scan uses {_embeddingModel.ModelVersion}. " +
                "Enroll the voice again with the current model.");
        }

        if (profile.Centroid.Length != _embeddingModel.EmbeddingDimension)
        {
            throw new InvalidOperationException(
                $"Voice profile '{profile.ProfileName}' has embedding dimension {profile.Centroid.Length}, but model " +
                $"'{_embeddingModel.ModelId}' produces {_embeddingModel.EmbeddingDimension}. Enroll the voice again.");
        }
    }

    /// <summary>
    /// Scans a single media file against the target profile using pipelined execution and embedding caching.
    /// </summary>
    public async Task<FileScanResult> ScanFileAsync(
        string mediaFilePath,
        VoiceProfile targetProfile,
        double threshold,
        int audioTrackIndex = 0,
        double windowDurationSec = 2.0,
        double hopDurationSec = 1.0,
        double mergeToleranceSec = 1.0,
        double? clusterDistanceThreshold = null,
        bool enableClustering = true,
        bool enableTemporalSmoothing = true,
        double peakDelta = 0.04,
        double neighborToleranceSec = 2.0,
        ScoreNormalizer? normalizer = null,
        int scoreSmoothingRadius = DefaultScoreSmoothingRadius,
        Action<double>? reportProgress = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? pauseGate = null,
        IInferenceClient? sidecarClient = null)
    {
        double clusterDistance = clusterDistanceThreshold ?? _embeddingModel.OperatingPoint.ClusterDistanceThreshold;
        if (!File.Exists(mediaFilePath))
        {
            throw new FileNotFoundException($"Media file to scan not found: {mediaFilePath}");
        }

        EnsureCompatible(targetProfile);

        string fileHash = FastFileHasher.ComputeFastHash(mediaFilePath);
        string vadSettings = _vad.SettingsFingerprint;
        string windowSettings = string.Create(CultureInfo.InvariantCulture,
            $"w:{windowDurationSec:R}_h:{hopDurationSec:R}_trk:{audioTrackIndex}_{SpeechWindowExtractor.Fingerprint}");
        string cacheKey = string.Empty;

        var activeSidecar = sidecarClient ?? _sidecarClient;

        // 1. Check SQLite Embedding Cache
        if (_database != null)
        {
            cacheKey = VoiceScanDatabase.ComputeCacheKey(fileHash, _embeddingModel.ModelVersion, vadSettings, windowSettings);

            var cachedScan = await _database.GetCachedScanAsync(cacheKey, cancellationToken);
            if (cachedScan != null)
            {
                var cachedWindows = cachedScan.Windows;
                var cachedInfo = cachedScan.Info;
                // CACHE HIT: Instant re-scan bypassing decode, VAD, and neural inference
                var cachedWindowItems = new List<WindowItem>(cachedWindows.Count);
                for (int i = 0; i < cachedWindows.Count; i++)
                {
                    var cw = cachedWindows[i];
                    cachedWindowItems.Add(new WindowItem(i, cw.StartTimeSeconds, cw.EndTimeSeconds, cw.Embedding, cw.SnrDb));
                }

                double duration = cachedInfo.DurationSeconds > 0.0
                    ? cachedInfo.DurationSeconds
                    : await AudioDecoder.GetMediaDurationSecondsAsync(mediaFilePath, cancellationToken);
                if (duration <= 0.0 && cachedWindows.Count > 0)
                {
                    duration = cachedWindows[^1].EndTimeSeconds;
                }

                SidecarScanResponse? cachedSidecarResponse = null;
                if (activeSidecar != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        cachedSidecarResponse = await activeSidecar.ScanAudioAsync(mediaFilePath, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        Logging.VoiceScanLogger.Warn("PipelineScanner", $"Sidecar scan on cache hit failed: {ex.Message}");
                        cachedSidecarResponse = null;
                    }
                    if (cachedSidecarResponse != null && !cachedSidecarResponse.HasSpeech)
                    {
                        var noSpeechResult = new FileScanResult
                        {
                            FilePath = Path.GetFullPath(mediaFilePath),
                            ClipId = Path.GetFileName(mediaFilePath),
                            FileHash = fileHash,
                            DurationSeconds = Math.Round(duration, 3),
                            AudioTrackIndex = audioTrackIndex,
                            Verdict = "No match",
                            MaxConfidence = 0.0,
                            Segments = new List<DetectedSegment>(),
                            ReasonFlags = new List<string> { "NO_SPEECH_DETECTED" }
                        };

                        if (!string.IsNullOrEmpty(fileHash))
                        {
                            await _database.SaveScanResultAsync(
                                noSpeechResult.FilePath,
                                fileHash,
                                targetProfile.ProfileName,
                                _embeddingModel.ModelVersion,
                                threshold,
                                noSpeechResult.Verdict,
                                noSpeechResult.MaxConfidence,
                                "[]",
                                cancellationToken: cancellationToken);
                        }

                        reportProgress?.Invoke(1.0);
                        return noSpeechResult;
                    }
                }

                List<DetectedSegment> cachedSegments;
                double cachedMaxConf;
                string cachedVerdict;

                if (cachedSidecarResponse != null && cachedSidecarResponse.HasSpeech)
                {
                    cachedSegments = MapAndScoreSidecarSegments(
                        cachedSidecarResponse.Segments,
                        cachedWindowItems,
                        targetProfile,
                        threshold,
                        normalizer);
                    cachedMaxConf = cachedSegments.Count > 0 ? cachedSegments.Max(s => s.Confidence) : 0.0;
                    cachedVerdict = ComputeOverallVerdict(cachedSegments);
                }
                else
                {
                    (cachedSegments, cachedMaxConf, cachedVerdict) = ScoreAndAggregate(
                        cachedWindowItems,
                        targetProfile,
                        threshold,
                        clusterDistance,
                        mergeToleranceSec,
                        enableClustering,
                        enableTemporalSmoothing,
                        peakDelta,
                        neighborToleranceSec,
                        normalizer,
                        scoreSmoothingRadius);
                }

                var cachedResult = new FileScanResult
                {
                    FilePath = Path.GetFullPath(mediaFilePath),
                    ClipId = Path.GetFileName(mediaFilePath),
                    FileHash = fileHash,
                    DurationSeconds = Math.Round(duration, 3),
                    AudioTrackIndex = audioTrackIndex,
                    Verdict = cachedVerdict,
                    MaxConfidence = Math.Round(cachedMaxConf, 4),
                    Segments = cachedSegments,
                    WaveformMinPeaks = cachedInfo.WaveformMinPeaks,
                    WaveformMaxPeaks = cachedInfo.WaveformMaxPeaks
                };

                // Persist scan result to database on cache hit
                if (!string.IsNullOrEmpty(fileHash))
                {
                    string segJson = JsonSerializer.Serialize(cachedSegments);
                    var primaryHit = cachedSegments.FirstOrDefault(s => s.Verdict == "Match") ?? cachedSegments.FirstOrDefault();
                    await _database.SaveScanResultAsync(
                        cachedResult.FilePath,
                        fileHash,
                        targetProfile.ProfileName,
                        _embeddingModel.ModelVersion,
                        threshold,
                        cachedVerdict,
                        cachedMaxConf,
                        segJson,
                        speakerLabel: primaryHit?.SpeakerLabel,
                        transcript: primaryHit?.Transcript,
                        isOffensive: cachedSegments.Any(s => s.IsOffensive),
                        moderationViolations: cachedSegments.SelectMany(s => s.ModerationViolations).Distinct().ToList(),
                        cancellationToken: cancellationToken);
                }

                reportProgress?.Invoke(1.0);
                return cachedResult;
            }
        }

        // Sidecar check when processing audio
        SidecarScanResponse? sidecarResponse = null;
        if (activeSidecar != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                sidecarResponse = await activeSidecar.ScanAudioAsync(mediaFilePath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Logging.VoiceScanLogger.Warn("PipelineScanner", $"Sidecar scan failed for {mediaFilePath}: {ex.Message}");
                sidecarResponse = null;
            }

            if (sidecarResponse != null && !sidecarResponse.HasSpeech)
            {
                double duration = await AudioDecoder.GetMediaDurationSecondsAsync(mediaFilePath, cancellationToken);
                var noSpeechResult = new FileScanResult
                {
                    FilePath = Path.GetFullPath(mediaFilePath),
                    ClipId = Path.GetFileName(mediaFilePath),
                    FileHash = fileHash,
                    DurationSeconds = Math.Round(duration, 3),
                    AudioTrackIndex = audioTrackIndex,
                    Verdict = "No match",
                    MaxConfidence = 0.0,
                    Segments = new List<DetectedSegment>(),
                    ReasonFlags = new List<string> { "NO_SPEECH_DETECTED" }
                };

                if (_database != null && !string.IsNullOrEmpty(fileHash))
                {
                    await _database.SaveScanResultAsync(
                        noSpeechResult.FilePath,
                        fileHash,
                        targetProfile.ProfileName,
                        _embeddingModel.ModelVersion,
                        threshold,
                        noSpeechResult.Verdict,
                        noSpeechResult.MaxConfidence,
                        "[]",
                        cancellationToken: cancellationToken);
                }

                reportProgress?.Invoke(1.0);
                return noSpeechResult;
            }
        }

        // CACHE MISS: stream-decode into a temp spool while running VAD, then embed windows read back
        // from the spool. Memory stays bounded by the batch size however long the recording is.
        using var spool = new SpooledAudio();
        var vadStream = _vad.StartProbabilityStream();

        // Decode covers the first half of the file's progress and embedding the second; the decode half
        // is only reported when ffprobe can tell us the expected length.
        double expectedSamples = 0.0;
        if (reportProgress != null)
        {
            expectedSamples = await AudioDecoder.GetMediaDurationSecondsAsync(mediaFilePath, cancellationToken) * 16000.0;
        }

        double lastReported = 0.0;
        void Report(double fraction)
        {
            if (reportProgress == null || fraction - lastReported < 0.005 && fraction < 1.0) return;
            lastReported = fraction;
            reportProgress(fraction);
        }

        await foreach (var chunk in AudioDecoder.StreamDecodeAsync(
            mediaFilePath,
            audioTrackIndex: audioTrackIndex,
            sampleRate: 16000,
            chunkSize: 32000,
            cancellationToken: cancellationToken))
        {
            spool.Append(chunk.Samples);
            vadStream.Feed(chunk.Samples, chunk.Samples.Length);
            if (expectedSamples > 0.0) Report(Math.Min(0.5, 0.5 * spool.SampleCount / expectedSamples));
            if (pauseGate != null) await pauseGate(cancellationToken);
        }
        Report(0.5);

        long totalSamples = spool.SampleCount;
        if (totalSamples == 0)
        {
            Logging.VoiceScanLogger.Warn("PipelineScanner", $"Zero audio samples decoded from {mediaFilePath}.");
            return CreateErrorResult(
                mediaFilePath,
                audioTrackIndex,
                new InvalidDataException("No audio could be decoded: the file has no audio track (or the selected track is empty) or is corrupt."));
        }

        double durationSeconds = (double)totalSamples / 16000.0;
        var speechIntervals = WebRtcVad.ProbabilitiesToIntervals(vadStream.Finish(), durationSeconds);
        var plans = SpeechWindowExtractor.PlanWindows(
            totalSamples,
            speechIntervals,
            sampleRate: 16000,
            windowSec: windowDurationSec,
            hopSec: hopDurationSec);

        var windowsToCache = new List<CachedWindow>();
        var windowItems = new List<WindowItem>();

        if (plans.Count > 0 && targetProfile.Centroid.Length > 0)
        {
            // Process windows in batches through GPU embedding model
            for (int i = 0; i < plans.Count; i += _batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pauseGate != null) await pauseGate(cancellationToken);
                int count = Math.Min(_batchSize, plans.Count - i);
                var batchWindows = Enumerable.Range(i, count)
                    .Select(k => SpeechWindowExtractor.Materialize(
                        plans[k],
                        spool.Read(plans[k].SourceStart, plans[k].SourceLength),
                        16000,
                        windowDurationSec))
                    .ToList();
                var batchAudios = batchWindows.Select(w => w.AudioSamples).ToList();

                var batchEmbeddings = _embeddingModel.ExtractEmbeddingsBatch(batchAudios);

                for (int b = 0; b < count; b++)
                {
                    var win = batchWindows[b];
                    var emb = batchEmbeddings[b];
                    double snrDb = AcousticDiagnostics.EstimateSnrDb(win.AudioSamples);
                    windowsToCache.Add(new CachedWindow(win.StartTimeSeconds, win.EndTimeSeconds, emb, snrDb));
                    windowItems.Add(new WindowItem(windowItems.Count, win.StartTimeSeconds, win.EndTimeSeconds, emb, snrDb));
                }

                Report(0.5 + 0.5 * (i + count) / plans.Count);
            }
        }

        var (minPeaks, maxPeaks) = spool.Envelope(300);

        // 4. Save extracted embeddings to SQLite cache
        if (_database != null && !string.IsNullOrEmpty(cacheKey) && windowsToCache.Count > 0)
        {
            await _database.SaveCachedWindowsAsync(
                cacheKey,
                fileHash,
                _embeddingModel.ModelVersion,
                vadSettings,
                windowSettings,
                windowsToCache,
                cancellationToken,
                new CachedFileInfo(Math.Round(durationSeconds, 3), minPeaks, maxPeaks));
        }

        List<DetectedSegment> segments;
        double maxConfidence;
        string verdict;

        if (sidecarResponse != null && sidecarResponse.HasSpeech)
        {
            segments = MapAndScoreSidecarSegments(
                sidecarResponse.Segments,
                windowItems,
                targetProfile,
                threshold,
                normalizer,
                spool,
                _embeddingModel);
            maxConfidence = segments.Count > 0 ? segments.Max(s => s.Confidence) : 0.0;
            verdict = ComputeOverallVerdict(segments);
        }
        else
        {
            (segments, maxConfidence, verdict) = ScoreAndAggregate(
                windowItems,
                targetProfile,
                threshold,
                clusterDistance,
                mergeToleranceSec,
                enableClustering,
                enableTemporalSmoothing,
                peakDelta,
                neighborToleranceSec,
                normalizer,
                scoreSmoothingRadius);
        }

        var result = new FileScanResult
        {
            FilePath = Path.GetFullPath(mediaFilePath),
            ClipId = Path.GetFileName(mediaFilePath),
            FileHash = fileHash,
            DurationSeconds = Math.Round(durationSeconds, 3),
            AudioTrackIndex = audioTrackIndex,
            Verdict = verdict,
            MaxConfidence = Math.Round(maxConfidence, 4),
            Segments = segments,
            WaveformMinPeaks = minPeaks,
            WaveformMaxPeaks = maxPeaks
        };

        // 5. Persist scan result to database if active
        if (_database != null && !string.IsNullOrEmpty(fileHash))
        {
            string segJson = JsonSerializer.Serialize(segments);
            var primaryHit = segments.FirstOrDefault(s => s.Verdict == "Match") ?? segments.FirstOrDefault();
            await _database.SaveScanResultAsync(
                result.FilePath,
                fileHash,
                targetProfile.ProfileName,
                _embeddingModel.ModelVersion,
                threshold,
                verdict,
                maxConfidence,
                segJson,
                speakerLabel: primaryHit?.SpeakerLabel,
                transcript: primaryHit?.Transcript,
                isOffensive: segments.Any(s => s.IsOffensive),
                moderationViolations: segments.SelectMany(s => s.ModerationViolations).Distinct().ToList(),
                cancellationToken: cancellationToken);
        }

        Report(1.0);
        return result;
    }

    private static List<DetectedSegment> MapAndScoreSidecarSegments(
        IReadOnlyList<DetectedSegment> sidecarSegments,
        IReadOnlyList<WindowItem> windowItems,
        VoiceProfile targetProfile,
        double threshold,
        ScoreNormalizer? normalizer,
        SpooledAudio? spool = null,
        ISpeakerEmbeddingModel? embeddingModel = null)
    {
        var mapped = new List<DetectedSegment>(sidecarSegments.Count);
        double possibleThreshold = Math.Max(0.10, threshold - 0.08);
        (double Mean, double StdDev)? targetStats = normalizer != null && targetProfile.Centroid.Length > 0
            ? normalizer.ComputeCohortStats(targetProfile.Centroid)
            : null;

        foreach (var src in sidecarSegments)
        {
            double segStart = src.StartTimeSeconds;
            double segEnd = src.EndTimeSeconds;
            double duration = Math.Max(0.01, segEnd - segStart);

            var covered = windowItems
                .Where(w => w.EndTimeSeconds > segStart && w.StartTimeSeconds < segEnd)
                .ToList();

            float[]? segEmb = src.Embedding;
            double confidence = src.Confidence;
            string verdict = src.Verdict;
            var reasonFlags = new List<string>(src.ReasonFlags);

            if (targetProfile.Centroid.Length > 0)
            {
                if (covered.Count > 0)
                {
                    segEmb = new float[covered[0].Embedding.Length];
                    for (int w = 0; w < covered.Count; w++)
                    {
                        for (int d = 0; d < segEmb.Length; d++)
                        {
                            segEmb[d] += covered[w].Embedding[d];
                        }
                    }
                    float norm = MathF.Sqrt(segEmb.Sum(x => x * x));
                    if (norm > 1e-12f)
                    {
                        for (int d = 0; d < segEmb.Length; d++) segEmb[d] /= norm;
                    }

                    double snrDb = covered.Average(w => w.SnrDb);
                    var acousticFlags = AcousticDiagnostics.EvaluateReasonFlags(duration, snrDb, isCodecDegraded: false);
                    foreach (var f in acousticFlags)
                    {
                        if (!reasonFlags.Contains(f)) reasonFlags.Add(f);
                    }

                    float rawSim = SimilarityScorer.CosineSimilarity(segEmb, targetProfile.Centroid);
                    double score = rawSim;
                    if (normalizer != null && targetStats.HasValue)
                    {
                        double z = normalizer.NormalizeScoreWithTargetStats(rawSim, targetStats.Value, segEmb);
                        score = ScoreNormalizer.CalibrateZScoreToConfidence(z);
                    }

                    confidence = Math.Round(score, 4);
                    if (confidence >= threshold)
                    {
                        verdict = (reasonFlags.Contains("LOW_SNR") && confidence < threshold + 0.03) ? "Possible" : "Match";
                    }
                    else if (confidence >= possibleThreshold)
                    {
                        verdict = "Possible";
                    }
                    else
                    {
                        verdict = "No match";
                    }
                }
                else if (spool != null && embeddingModel != null && spool.SampleCount > 0)
                {
                    long startSample = Math.Clamp((long)(segStart * 16000), 0, spool.SampleCount);
                    long endSample = Math.Clamp((long)(segEnd * 16000), 0, spool.SampleCount);
                    long sampleCount = endSample - startSample;
                    if (sampleCount >= 1600) // at least 100ms
                    {
                        var samples = spool.Read(startSample, (int)sampleCount);
                        segEmb = embeddingModel.ExtractEmbedding(samples);
                        double snrDb = AcousticDiagnostics.EstimateSnrDb(samples);
                        var acousticFlags = AcousticDiagnostics.EvaluateReasonFlags(duration, snrDb, isCodecDegraded: false);
                        foreach (var f in acousticFlags)
                        {
                            if (!reasonFlags.Contains(f)) reasonFlags.Add(f);
                        }

                        float rawSim = SimilarityScorer.CosineSimilarity(segEmb, targetProfile.Centroid);
                        double score = rawSim;
                        if (normalizer != null && targetStats.HasValue)
                        {
                            double z = normalizer.NormalizeScoreWithTargetStats(rawSim, targetStats.Value, segEmb);
                            score = ScoreNormalizer.CalibrateZScoreToConfidence(z);
                        }

                        confidence = Math.Round(score, 4);
                        if (confidence >= threshold)
                        {
                            verdict = (reasonFlags.Contains("LOW_SNR") && confidence < threshold + 0.03) ? "Possible" : "Match";
                        }
                        else if (confidence >= possibleThreshold)
                        {
                            verdict = "Possible";
                        }
                        else
                        {
                            verdict = "No match";
                        }
                    }
                }
                else if (segEmb != null)
                {
                    float rawSim = SimilarityScorer.CosineSimilarity(segEmb, targetProfile.Centroid);
                    double score = rawSim;
                    if (normalizer != null && targetStats.HasValue)
                    {
                        double z = normalizer.NormalizeScoreWithTargetStats(rawSim, targetStats.Value, segEmb);
                        score = ScoreNormalizer.CalibrateZScoreToConfidence(z);
                    }

                    confidence = Math.Round(score, 4);
                    if (confidence >= threshold)
                    {
                        verdict = (reasonFlags.Contains("LOW_SNR") && confidence < threshold + 0.03) ? "Possible" : "Match";
                    }
                    else if (confidence >= possibleThreshold)
                    {
                        verdict = "Possible";
                    }
                    else
                    {
                        verdict = "No match";
                    }
                }
            }

            if (src.IsOffensive && !reasonFlags.Contains("OFFENSIVE_CONTENT"))
            {
                reasonFlags.Add("OFFENSIVE_CONTENT");
            }

            mapped.Add(new DetectedSegment
            {
                StartTimeSeconds = Math.Round(segStart, 3),
                EndTimeSeconds = Math.Round(segEnd, 3),
                Confidence = confidence,
                Verdict = verdict,
                ReasonFlags = reasonFlags,
                SpeakerLabel = src.SpeakerLabel,
                Transcript = src.Transcript,
                IsOffensive = src.IsOffensive,
                ModerationViolations = src.ModerationViolations,
                ModerationScores = src.ModerationScores,
                Embedding = segEmb
            });
        }

        return mapped;
    }

    private static string ComputeOverallVerdict(IReadOnlyList<DetectedSegment> segments)
    {
        if (segments.Any(s => s.Verdict == "Match")) return "Match";
        if (segments.Any(s => s.Verdict == "Possible")) return "Possible";
        return "No match";
    }


    /// <summary>Result recorded for a file that could not be scanned, so it is never mistaken for a clean "No match".</summary>
    public static FileScanResult CreateErrorResult(string filePath, int audioTrackIndex, Exception ex) => new()
    {
        FilePath = Path.GetFullPath(filePath),
        ClipId = Path.GetFileName(filePath),
        AudioTrackIndex = audioTrackIndex,
        Verdict = ErrorVerdict,
        Error = ex.Message,
        Segments = []
    };

    /// <summary>
    /// Scores window embeddings using cluster-level scoring (or window-level fallback) and merges hits.
    /// </summary>
    public static (List<DetectedSegment> Segments, double MaxConfidence, string Verdict) ScoreAndAggregate(
        IReadOnlyList<WindowItem> windowItems,
        VoiceProfile targetProfile,
        double threshold,
        double clusterDistanceThreshold = 0.40,
        double mergeToleranceSec = 1.0,
        bool enableClustering = true,
        bool enableTemporalSmoothing = true,
        double peakDelta = 0.04,
        double neighborToleranceSec = 2.0,
        ScoreNormalizer? normalizer = null,
        int scoreSmoothingRadius = DefaultScoreSmoothingRadius)
    {
        if (windowItems.Count == 0 || targetProfile.Centroid.Length == 0)
        {
            return (new List<DetectedSegment>(), 0.0, "No match");
        }

        double possibleThreshold = Math.Max(0.10, threshold - 0.08);
        var hits = new List<(double Start, double End, double Confidence)>();
        // Windows that produced a hit; segment diagnostics come only from these, never from other speakers' windows.
        var hitWindows = new List<WindowItem>();
        double rawMaxConfidence = 0.0;

        (double Mean, double StdDev)? targetStats = normalizer != null
            ? normalizer.ComputeCohortStats(targetProfile.Centroid)
            : null;

        if (!enableClustering)
        {
            var ordered = windowItems.OrderBy(w => w.StartTimeSeconds).ToList();
            var scores = new double[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                var win = ordered[i];
                float rawSim = SimilarityScorer.CosineSimilarity(win.Embedding, targetProfile.Centroid);
                scores[i] = rawSim;
                if (normalizer != null && targetStats.HasValue)
                {
                    double z = normalizer.NormalizeScoreWithTargetStats(rawSim, targetStats.Value, win.Embedding);
                    scores[i] = ScoreNormalizer.CalibrateZScoreToConfidence(z);
                }
            }

            if (scoreSmoothingRadius > 0)
            {
                scores = SimilarityScorer.SmoothScores(
                    ordered.Select(w => w.StartTimeSeconds).ToList(),
                    scores,
                    scoreSmoothingRadius,
                    neighborToleranceSec);
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                double sim = scores[i];
                if (sim > rawMaxConfidence) rawMaxConfidence = sim;
                if (sim >= possibleThreshold)
                {
                    hits.Add((ordered[i].StartTimeSeconds, ordered[i].EndTimeSeconds, sim));
                    hitWindows.Add(ordered[i]);
                }
            }
        }
        else
        {
            // Within-file Agglomerative Hierarchical Clustering (AHC)
            var clusters = SpeakerClusterer.ClusterWindows(windowItems, clusterDistanceThreshold);
            foreach (var cluster in clusters)
            {
                float rawSim = SimilarityScorer.CosineSimilarity(cluster.Centroid, targetProfile.Centroid);
                float clusterScore = rawSim;
                if (normalizer != null && targetStats.HasValue)
                {
                    double z = normalizer.NormalizeScoreWithTargetStats(rawSim, targetStats.Value, cluster.Centroid);
                    clusterScore = (float)ScoreNormalizer.CalibrateZScoreToConfidence(z);
                }

                cluster.Score = clusterScore;

                if (clusterScore > rawMaxConfidence)
                {
                    rawMaxConfidence = clusterScore;
                }

                if (clusterScore >= possibleThreshold)
                {
                    foreach (var win in cluster.Windows)
                    {
                        hits.Add((win.StartTimeSeconds, win.EndTimeSeconds, clusterScore));
                        hitWindows.Add(win);
                    }
                }
            }
        }

        var segments = enableTemporalSmoothing
            ? SimilarityScorer.TemporalSmoothingAndAggregation(
                hits,
                possibleThreshold,
                peakDelta: peakDelta,
                neighborToleranceSec: neighborToleranceSec,
                mergeToleranceSec: mergeToleranceSec)
            : SimilarityScorer.MergeAdjacentHits(hits, mergeToleranceSec);

        // Assign diagnostic reason flags and 3-state verdicts
        foreach (var seg in segments)
        {
            double duration = seg.EndTimeSeconds - seg.StartTimeSeconds;

            // Diagnostics and embedding come from the hit windows inside the segment (cached and fresh scans agree).
            var covered = hitWindows.Where(w => w.EndTimeSeconds > seg.StartTimeSeconds && w.StartTimeSeconds < seg.EndTimeSeconds).ToList();
            double snrDb = covered.Count > 0 ? covered.Average(w => w.SnrDb) : 20.0;

            if (covered.Count > 0)
            {
                float[] segEmb = new float[covered[0].Embedding.Length];
                for (int w = 0; w < covered.Count; w++)
                {
                    for (int d = 0; d < segEmb.Length; d++)
                    {
                        segEmb[d] += covered[w].Embedding[d];
                    }
                }
                float norm = MathF.Sqrt(segEmb.Sum(x => x * x));
                if (norm > 1e-12f)
                {
                    for (int d = 0; d < segEmb.Length; d++) segEmb[d] /= norm;
                }
                seg.Embedding = segEmb;
            }

            var reasonFlags = AcousticDiagnostics.EvaluateReasonFlags(duration, snrDb, isCodecDegraded: false);
            seg.ReasonFlags = reasonFlags;

            if (seg.Confidence >= threshold)
            {
                if (reasonFlags.Contains("LOW_SNR") && seg.Confidence < threshold + 0.03)
                {
                    seg.Verdict = "Possible";
                }
                else
                {
                    seg.Verdict = "Match";
                }
            }
            else if (seg.Confidence >= possibleThreshold)
            {
                seg.Verdict = "Possible";
            }
            else
            {
                seg.Verdict = "No match";
            }
        }

        string fileVerdict;
        if (segments.Any(s => s.Verdict == "Match"))
        {
            fileVerdict = "Match";
        }
        else if (segments.Any(s => s.Verdict == "Possible"))
        {
            fileVerdict = "Possible";
        }
        else
        {
            fileVerdict = "No match";
        }

        // The highest score behind the verdict: the strongest reported segment, or, when temporal support removed
        // every hit, the strongest raw score so the number is never lower than what was actually measured.
        double finalConfidence = segments.Count > 0 ? segments.Max(s => s.Confidence) : rawMaxConfidence;

        return (segments, finalConfidence, fileVerdict);
    }

    private static readonly string[] MediaExtensions = [".wav", ".flac", ".mp3", ".ogg", ".mp4", ".mkv", ".m4a"];

    private static bool IsMediaFile(string path) =>
        MediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scans a directory of media files against a voice profile.
    /// </summary>
    public async Task<ScanOutputDocument> ScanDirectoryAsync(
        string inputPath,
        VoiceProfile targetProfile,
        double threshold,
        int audioTrackIndex = 0,
        double? clusterDistanceThreshold = null,
        bool enableClustering = true,
        bool enableTemporalSmoothing = true,
        double peakDelta = 0.04,
        double neighborToleranceSec = 2.0,
        ScoreNormalizer? normalizer = null,
        IReadOnlyDictionary<string, VoiceProfile>? clipProfileMap = null,
        int scoreSmoothingRadius = DefaultScoreSmoothingRadius,
        bool recursive = true,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        double clusterDistance = clusterDistanceThreshold ?? _embeddingModel.OperatingPoint.ClusterDistanceThreshold;

        EnsureCompatible(targetProfile);
        foreach (var mapped in clipProfileMap?.Values ?? [])
        {
            EnsureCompatible(mapped);
        }

        var mediaFiles = new List<string>();
        if (Directory.Exists(inputPath))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
            mediaFiles.AddRange(Directory.EnumerateFiles(inputPath, "*", options).Where(IsMediaFile));
            mediaFiles.Sort(StringComparer.Ordinal);
        }
        else if (File.Exists(inputPath))
        {
            mediaFiles.Add(inputPath);
        }
        else
        {
            throw new FileNotFoundException($"Input media directory or file not found: {inputPath}");
        }

        async Task<FileScanResult> ScanOne(int _, string file, CancellationToken ct)
        {
            var prof = (clipProfileMap != null && clipProfileMap.TryGetValue(Path.GetFileName(file), out var p)) ? p : targetProfile;
            try
            {
                return await ScanFileAsync(
                    file,
                    prof,
                    threshold,
                    audioTrackIndex: audioTrackIndex,
                    clusterDistanceThreshold: clusterDistance,
                    enableClustering: enableClustering,
                    enableTemporalSmoothing: enableTemporalSmoothing,
                    peakDelta: peakDelta,
                    neighborToleranceSec: neighborToleranceSec,
                    normalizer: normalizer,
                    scoreSmoothingRadius: scoreSmoothingRadius,
                    cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                Logging.VoiceScanLogger.Info("PipelineScanner", $"Scan cancelled by user during processing of {file}.");
                throw;
            }
            catch (Exception ex)
            {
                Logging.VoiceScanLogger.Error("PipelineScanner", $"Error scanning file {file}", ex);
                return CreateErrorResult(file, audioTrackIndex, ex);
            }
        }

        var results = new List<FileScanResult>();
        await foreach (var res in OrderedPrefetch.RunAsync(mediaFiles, FilePrefetchDepth, ScanOne, cancellationToken))
        {
            results.Add(res);
        }

        stopwatch.Stop();

        return new ScanOutputDocument
        {
            SchemaVersion = ScanOutputDocument.CurrentSchemaVersion,
            ScanMetadata = new ScanMetadata
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                ProfileName = targetProfile.ProfileName,
                ModelId = _embeddingModel.ModelId,
                ModelVersion = _embeddingModel.ModelVersion,
                EngineVersion = EngineVersion,
                ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                Threshold = Math.Round(threshold, 4),
                ClusteringEnabled = enableClustering,
                ClusterThreshold = clusterDistance,
                TemporalSmoothing = enableTemporalSmoothing
            },
            Files = results
        };
    }
}
