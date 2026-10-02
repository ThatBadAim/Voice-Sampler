namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VoiceScan.Core.Storage;

/// <summary>
/// Executes pipelined scanning of media files against an enrolled voice profile.
/// When an optional VoiceScanDatabase is provided, audio decode, VAD, and GPU embeddings are cached,
/// allowing subsequent scans with new/updated profiles to complete in a fraction of the time.
/// </summary>
public sealed class PipelineScanner
{
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
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaFilePath))
        {
            throw new FileNotFoundException($"Media file to scan not found: {mediaFilePath}");
        }

        string fileHash = string.Empty;
        string vadSettings = "silero-v5:0.5";
        string windowSettings = $"w:{windowDurationSec:F1}_h:{hopDurationSec:F1}";
        string cacheKey = string.Empty;

        // 1. Check SQLite Embedding Cache
        if (_database != null)
        {
            fileHash = FastFileHasher.ComputeFastHash(mediaFilePath);
            cacheKey = VoiceScanDatabase.ComputeCacheKey(fileHash, _embeddingModel.ModelId, vadSettings, windowSettings);

            var cachedWindows = await _database.GetCachedWindowsAsync(cacheKey, cancellationToken);
            if (cachedWindows != null)
            {
                // CACHE HIT: Instant re-scan bypassing decode, VAD, and neural inference
                var cachedWindowItems = new List<WindowItem>(cachedWindows.Count);
                for (int i = 0; i < cachedWindows.Count; i++)
                {
                    var cw = cachedWindows[i];
                    cachedWindowItems.Add(new WindowItem(i, cw.StartTimeSeconds, cw.EndTimeSeconds, cw.Embedding));
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
                    audioPcm: null);

                double approxDuration = cachedWindows.Count > 0 ? cachedWindows[^1].EndTimeSeconds : 0.0;

                var cachedResult = new FileScanResult
                {
                    FilePath = Path.GetFullPath(mediaFilePath),
                    ClipId = Path.GetFileName(mediaFilePath),
                    DurationSeconds = Math.Round(approxDuration, 3),
                    AudioTrackIndex = audioTrackIndex,
                    Verdict = cachedVerdict,
                    MaxConfidence = Math.Round(cachedMaxConf, 4),
                    Segments = cachedSegments
                };

                return cachedResult;
            }
        }

        // CACHE MISS: Execute pipelined decode + VAD + windowing + GPU embedding extraction
        var channel = Channel.CreateBounded<DecodedAudioChunk>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

        // 2. Launch FFmpeg decode producer task
        var decodeTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in AudioDecoder.StreamDecodeAsync(
                    mediaFilePath,
                    audioTrackIndex: audioTrackIndex,
                    sampleRate: 16000,
                    chunkSize: 32000,
                    cancellationToken: cancellationToken))
                {
                    await channel.Writer.WriteAsync(chunk, cancellationToken);
                }
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // 3. Concurrently consume chunks
        var allAudioChunks = new List<float[]>();
        long totalSamples = 0;

        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var chunk))
            {
                allAudioChunks.Add(chunk.Samples);
                totalSamples += chunk.Samples.Length;
            }
        }

        await decodeTask;

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

        // Flatten collected audio for VAD and windowing
        float[] fullAudio = new float[totalSamples];
        int offset = 0;
        foreach (var c in allAudioChunks)
        {
            Array.Copy(c, 0, fullAudio, offset, c.Length);
            offset += c.Length;
        }

        double durationSeconds = (double)totalSamples / 16000.0;

        // Run Silero VAD
        var speechIntervals = _vad.DetectSpeechIntervals(fullAudio);

        // Extract 2s windows with 1s hop
        var windows = SpeechWindowExtractor.ExtractWindows(
            fullAudio,
            speechIntervals,
            sampleRate: 16000,
            windowSec: windowDurationSec,
            hopSec: hopDurationSec);

        var windowsToCache = new List<CachedWindow>();
        var windowItems = new List<WindowItem>();

        if (windows.Count > 0 && targetProfile.Centroid.Length > 0)
        {
            // Process windows in batches through GPU embedding model
            for (int i = 0; i < windows.Count; i += _batchSize)
            {
                int count = Math.Min(_batchSize, windows.Count - i);
                var batchWindows = windows.Skip(i).Take(count).ToList();
                var batchAudios = batchWindows.Select(w => w.AudioSamples).ToList();

                var batchEmbeddings = _embeddingModel.ExtractEmbeddingsBatch(batchAudios);

                for (int b = 0; b < count; b++)
                {
                    var win = batchWindows[b];
                    var emb = batchEmbeddings[b];
                    windowsToCache.Add(new CachedWindow(win.StartTimeSeconds, win.EndTimeSeconds, emb));
                    windowItems.Add(new WindowItem(windowItems.Count, win.StartTimeSeconds, win.EndTimeSeconds, emb));
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
            audioPcm: fullAudio);

        var result = new FileScanResult
        {
            FilePath = Path.GetFullPath(mediaFilePath),
            ClipId = Path.GetFileName(mediaFilePath),
            DurationSeconds = Math.Round(durationSeconds, 3),
            AudioTrackIndex = audioTrackIndex,
            Verdict = verdict,
            MaxConfidence = Math.Round(maxConfidence, 4),
            Segments = segments
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
        float[]? audioPcm = null)
    {
        if (windowItems.Count == 0 || targetProfile.Centroid.Length == 0)
        {
            return (new List<DetectedSegment>(), 0.0, "No match");
        }

        double possibleThreshold = Math.Max(0.10, threshold - 0.08);
        var hits = new List<(double Start, double End, double Confidence)>();
        double rawMaxConfidence = 0.0;

        if (!enableClustering)
        {
            foreach (var win in windowItems)
            {
                float rawSim = SimilarityScorer.CosineSimilarity(win.Embedding, targetProfile.Centroid);
                float sim = rawSim;
                if (normalizer != null)
                {
                    double z = normalizer.NormalizeScore(rawSim, targetProfile.Centroid, win.Embedding);
                    sim = (float)ScoreNormalizer.CalibrateZScoreToConfidence(z);
                }

                if (sim > rawMaxConfidence) rawMaxConfidence = sim;
                if (sim >= possibleThreshold)
                {
                    hits.Add((win.StartTimeSeconds, win.EndTimeSeconds, sim));
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
                if (normalizer != null)
                {
                    double z = normalizer.NormalizeScore(rawSim, targetProfile.Centroid, cluster.Centroid);
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
            double snrDb = 20.0;
            bool isOverlap = false;

            if (audioPcm != null && audioPcm.Length > 0)
            {
                int startIdx = Math.Clamp((int)(seg.StartTimeSeconds * 16000), 0, audioPcm.Length);
                int endIdx = Math.Clamp((int)(seg.EndTimeSeconds * 16000), startIdx, audioPcm.Length);
                int len = endIdx - startIdx;
                if (len > 0)
                {
                    float[] slice = new float[len];
                    Array.Copy(audioPcm, startIdx, slice, 0, len);
                    snrDb = AcousticDiagnostics.EstimateSnrDb(slice);
                    isOverlap = AcousticDiagnostics.DetectSuspectedOverlap(slice, Math.Max(1, (int)Math.Round(duration)));
                }
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
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var mediaFiles = new List<string>();
        if (Directory.Exists(inputPath))
        {
            string[] extensions = { "*.wav", "*.flac", "*.mp3", "*.ogg", "*.mp4", "*.mkv", "*.m4a" };
            foreach (var ext in extensions)
            {
                mediaFiles.AddRange(Directory.GetFiles(inputPath, ext, SearchOption.TopDirectoryOnly));
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
