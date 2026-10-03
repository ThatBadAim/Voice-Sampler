namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoiceScan.Core.Storage;

/// <summary>
/// Executes pipelined scanning of media files against an enrolled voice profile.
/// When an optional VoiceScanDatabase is provided, audio decode, VAD, and GPU embeddings are cached,
/// allowing subsequent scans with new/updated profiles to complete in a fraction of the time.
/// </summary>
public sealed class PipelineScanner
{
    public const int DefaultScoreSmoothingRadius = 0;

    private readonly ISpeakerEmbeddingModel _embeddingModel;
    private readonly SileroVad _vad;
    private readonly VoiceScanDatabase? _database;
    private readonly int _batchSize;

    public PipelineScanner(
        ISpeakerEmbeddingModel embeddingModel,
        SileroVad vad,
        VoiceScanDatabase? database = null,
        int batchSize = 16)
    {
        _embeddingModel = embeddingModel ?? throw new ArgumentNullException(nameof(embeddingModel));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
        _database = database;
        _batchSize = Math.Max(1, batchSize);
    }

    public ISpeakerEmbeddingModel EmbeddingModel => _embeddingModel;
    public SileroVad Vad => _vad;

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
        double clusterDistanceThreshold = 0.40,
        bool enableClustering = true,
        bool enableTemporalSmoothing = true,
        double peakDelta = 0.04,
        double neighborToleranceSec = 2.0,
        ScoreNormalizer? normalizer = null,
        int scoreSmoothingRadius = DefaultScoreSmoothingRadius,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
        {
            throw new FileNotFoundException($"Media file to scan not found: {mediaFilePath}");
        }

        if (targetProfile.Centroid.Length != _embeddingModel.EmbeddingDimension)
        {
            throw new InvalidOperationException(
                $"Voice profile '{targetProfile.ProfileName}' has embedding dimension {targetProfile.Centroid.Length}, " +
                $"which does not match active model '{_embeddingModel.ModelId}' dimension {_embeddingModel.EmbeddingDimension}. " +
                $"Please re-enroll the profile with the current model.");
        }

        string fileHash = FastFileHasher.ComputeFastHash(mediaFilePath);
        string vadSettings = SileroVad.SettingsFingerprint;
        string windowSettings = $"w:{windowDurationSec:F1}_h:{hopDurationSec:F1}_trk:{audioTrackIndex}_{SpeechWindowExtractor.Fingerprint}_{Filterbank.Fingerprint}";
        string cacheKey = string.Empty;

        // 1. Check SQLite Embedding Cache
        if (_database != null)
        {
            cacheKey = VoiceScanDatabase.ComputeCacheKey(fileHash, _embeddingModel.ModelId, vadSettings, windowSettings);

            var cachedWindows = await _database.GetCachedWindowsAsync(cacheKey, cancellationToken);
            if (cachedWindows != null)
            {
                // CACHE HIT: Instant re-scan bypassing decode, VAD, and neural inference
                var cachedWindowItems = new List<WindowItem>(cachedWindows.Count);
                for (int i = 0; i < cachedWindows.Count; i++)
                {
                    var cw = cachedWindows[i];
                    cachedWindowItems.Add(new WindowItem(i, cw.StartTimeSeconds, cw.EndTimeSeconds, cw.Embedding, cw.SnrDb, cw.SuspectedOverlap));
                }

                var (cachedSegments, cachedMaxConf, cachedVerdict) = ScoreAndAggregate(
                    cachedWindowItems,
                    targetProfile,
                    threshold,
                    clusterDistanceThreshold,
                    mergeToleranceSec,
                    enableClustering,
                    enableTemporalSmoothing,
                    peakDelta,
                    neighborToleranceSec,
                    normalizer,
                    scoreSmoothingRadius);

                double duration = await AudioDecoder.GetMediaDurationSecondsAsync(mediaFilePath, cancellationToken);
                if (duration <= 0.0 && cachedWindows.Count > 0)
                {
                    duration = cachedWindows[^1].EndTimeSeconds;
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
                    Segments = cachedSegments
                };

                // Persist scan result to database on cache hit
                if (!string.IsNullOrEmpty(fileHash))
                {
                    string segJson = JsonSerializer.Serialize(cachedSegments);
                    await _database.SaveScanResultAsync(
                        cachedResult.FilePath,
                        fileHash,
                        targetProfile.ProfileName,
                        _embeddingModel.ModelId,
                        threshold,
                        cachedVerdict,
                        cachedMaxConf,
                        segJson,
                        cancellationToken);
                }

                return cachedResult;
            }
        }

        // CACHE MISS: stream-decode into a temp spool while running VAD, then embed windows read back
        // from the spool. Memory stays bounded by the batch size however long the recording is.
        using var spool = new SpooledAudio();
        var vadStream = _vad.StartProbabilityStream();

        await foreach (var chunk in AudioDecoder.StreamDecodeAsync(
            mediaFilePath,
            audioTrackIndex: audioTrackIndex,
            sampleRate: 16000,
            chunkSize: 32000,
            cancellationToken: cancellationToken))
        {
            spool.Append(chunk.Samples);
            vadStream.Feed(chunk.Samples, chunk.Samples.Length);
        }

        long totalSamples = spool.SampleCount;
        if (totalSamples == 0)
        {
            Logging.VoiceScanLogger.Warn("PipelineScanner", $"Zero audio samples decoded from {mediaFilePath}. Returning No match.");
            return new FileScanResult
            {
                FilePath = Path.GetFullPath(mediaFilePath),
                ClipId = Path.GetFileName(mediaFilePath),
                DurationSeconds = 0.0,
                AudioTrackIndex = audioTrackIndex,
                Verdict = "No match",
                MaxConfidence = 0.0,
                Segments = []
            };
        }

        double durationSeconds = (double)totalSamples / 16000.0;
        var speechIntervals = SileroVad.ProbabilitiesToIntervals(vadStream.Finish(), durationSeconds, 0.5f);
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
                    bool overlap = AcousticDiagnostics.DetectSuspectedOverlap(win.AudioSamples, windowCount: 2);
                    windowsToCache.Add(new CachedWindow(win.StartTimeSeconds, win.EndTimeSeconds, emb, snrDb, overlap));
                    windowItems.Add(new WindowItem(windowItems.Count, win.StartTimeSeconds, win.EndTimeSeconds, emb, snrDb, overlap));
                }
            }
        }

        // 4. Save extracted embeddings to SQLite cache
        if (_database != null && !string.IsNullOrEmpty(cacheKey) && windowsToCache.Count > 0)
        {
            await _database.SaveCachedWindowsAsync(
                cacheKey,
                fileHash,
                _embeddingModel.ModelId,
                vadSettings,
                windowSettings,
                windowsToCache,
                cancellationToken);
        }

        var (segments, maxConfidence, verdict) = ScoreAndAggregate(
            windowItems,
            targetProfile,
            threshold,
            clusterDistanceThreshold,
            mergeToleranceSec,
            enableClustering,
            enableTemporalSmoothing,
            peakDelta,
            neighborToleranceSec,
            normalizer,
            scoreSmoothingRadius);

        var (minPeaks, maxPeaks) = spool.Envelope(300);

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
            await _database.SaveScanResultAsync(
                result.FilePath,
                fileHash,
                targetProfile.ProfileName,
                _embeddingModel.ModelId,
                threshold,
                verdict,
                maxConfidence,
                segJson,
                cancellationToken);
        }

        return result;
    }

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

            // Diagnostics and embedding come from the windows covering the segment so cached and fresh scans agree.
            var covered = windowItems.Where(w => w.EndTimeSeconds > seg.StartTimeSeconds && w.StartTimeSeconds < seg.EndTimeSeconds).ToList();
            double snrDb = covered.Count > 0 ? covered.Average(w => w.SnrDb) : 20.0;
            bool isOverlap = covered.Any(w => w.SuspectedOverlap);

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

            var reasonFlags = AcousticDiagnostics.EvaluateReasonFlags(duration, snrDb, isOverlap, false);
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

        double finalConfidence;
        if (fileVerdict == "Match")
        {
            finalConfidence = segments.Where(s => s.Verdict == "Match").Max(s => s.Confidence);
        }
        else if (fileVerdict == "Possible")
        {
            finalConfidence = Math.Min(threshold - 0.001, segments.Max(s => s.Confidence));
        }
        else
        {
            finalConfidence = Math.Min(rawMaxConfidence, possibleThreshold - 0.001);
        }

        return (segments, finalConfidence, fileVerdict);
    }

    /// <summary>
    /// Scans a directory of media files against a voice profile.
    /// </summary>
    public async Task<ScanOutputDocument> ScanDirectoryAsync(
        string inputPath,
        VoiceProfile targetProfile,
        double threshold,
        int audioTrackIndex = 0,
        double clusterDistanceThreshold = 0.40,
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

        var mediaFiles = new List<string>();
        if (Directory.Exists(inputPath))
        {
            var searchOpt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            string[] extensions = { "*.wav", "*.flac", "*.mp3", "*.ogg", "*.mp4", "*.mkv", "*.m4a" };
            foreach (var ext in extensions)
            {
                mediaFiles.AddRange(Directory.GetFiles(inputPath, ext, searchOpt));
            }
            mediaFiles.Sort();
        }
        else if (File.Exists(inputPath))
        {
            mediaFiles.Add(inputPath);
        }
        else
        {
            throw new FileNotFoundException($"Input media directory or file not found: {inputPath}");
        }

        var results = new List<FileScanResult>();
        foreach (var file in mediaFiles)
        {
            var prof = (clipProfileMap != null && clipProfileMap.TryGetValue(Path.GetFileName(file), out var p)) ? p : targetProfile;
            FileScanResult res;
            try
            {
                res = await ScanFileAsync(
                    file,
                    prof,
                    threshold,
                    audioTrackIndex: audioTrackIndex,
                    clusterDistanceThreshold: clusterDistanceThreshold,
                    enableClustering: enableClustering,
                    enableTemporalSmoothing: enableTemporalSmoothing,
                    peakDelta: peakDelta,
                    neighborToleranceSec: neighborToleranceSec,
                    normalizer: normalizer,
                    scoreSmoothingRadius: scoreSmoothingRadius,
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Logging.VoiceScanLogger.Info("PipelineScanner", $"Scan cancelled by user during processing of {file}.");
                throw;
            }
            catch (Exception ex)
            {
                Logging.VoiceScanLogger.Error("PipelineScanner", $"Error scanning file {file}", ex);
                res = new FileScanResult
                {
                    FilePath = Path.GetFullPath(file),
                    ClipId = Path.GetFileName(file),
                    DurationSeconds = 0.0,
                    AudioTrackIndex = audioTrackIndex,
                    Verdict = "No match",
                    MaxConfidence = 0.0,
                    Segments = []
                };
            }
            results.Add(res);
        }

        stopwatch.Stop();

        return new ScanOutputDocument
        {
            SchemaVersion = "1.0.0",
            ScanMetadata = new ScanMetadata
            {
                Timestamp = DateTime.UtcNow.ToString("o"),
                ProfileName = targetProfile.ProfileName,
                ModelId = _embeddingModel.ModelId,
                EngineVersion = "0.1.0",
                ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                Threshold = Math.Round(threshold, 4)
            },
            Files = results
        };
    }
}
