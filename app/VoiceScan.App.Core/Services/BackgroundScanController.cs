using System.Diagnostics;
using VoiceScan.App.Core.Models;
using VoiceScan.Core;
using VoiceScan.Core.Logging;

namespace VoiceScan.App.Core.Services;

public interface IBackgroundScanController : IDisposable
{
    event EventHandler<OverallScanProgress>? ProgressChanged;
    event EventHandler<FileVerdictResult>? FileCompleted;
    event EventHandler<ScanExecutionState>? StateChanged;

    ScanExecutionState CurrentState { get; }
    OverallScanProgress CurrentProgress { get; }

    /// <summary>Decision settings of the loaded embedding model, used when the options leave them unset.</summary>
    ModelOperatingPoint OperatingPoint { get; }

    /// <summary>Model id of the loaded embedding model.</summary>
    string ModelId { get; }

    Task<IReadOnlyList<FileVerdictResult>> StartScanAsync(
        IReadOnlyList<string> targetFiles,
        string profilePath,
        PipelineScanOptions? options = null,
        CancellationToken cancellationToken = default);

    void Pause();
    void Resume();
    void Cancel();
}

/// <param name="Threshold">Match threshold; null uses the loaded model's operating point.</param>
/// <param name="ClusterThreshold">AHC stopping distance; null uses the loaded model's operating point.</param>
public sealed record PipelineScanOptions(
    double? Threshold = null,
    int AudioTrackIndex = 0,
    double WindowDurationSec = 2.0,
    double HopDurationSec = 1.0,
    double MergeToleranceSec = 1.0,
    double? ClusterThreshold = null,
    bool UseClustering = true,
    bool TemporalSmoothing = true,
    double PeakDelta = 0.04,
    double NeighborToleranceSec = 2.0,
    ScoreNormalizer? Normalizer = null);

public sealed class BackgroundScanController : IBackgroundScanController
{
    private readonly PipelineScanner _scanner;
    private readonly IWaveformService _waveformService;
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    private CancellationTokenSource? _scanCts;
    private ScanExecutionState _state = ScanExecutionState.Idle;
    private OverallScanProgress _currentProgress;
    private readonly Stopwatch _stopwatch = new();
    private readonly GpuUtilizationSampler _gpuSampler = new();
    private int _running;

    public event EventHandler<OverallScanProgress>? ProgressChanged;
    public event EventHandler<FileVerdictResult>? FileCompleted;
    public event EventHandler<ScanExecutionState>? StateChanged;

    public ScanExecutionState CurrentState => _state;
    public OverallScanProgress CurrentProgress => _currentProgress;
    public ModelOperatingPoint OperatingPoint => _scanner.EmbeddingModel.OperatingPoint;
    public string ModelId => _scanner.EmbeddingModel.ModelId;

    public BackgroundScanController(PipelineScanner scanner, IWaveformService? waveformService = null)
    {
        _scanner = scanner;
        _waveformService = waveformService ?? new WaveformService();
        _currentProgress = CreateInitialProgress(0);
    }

    public async Task<IReadOnlyList<FileVerdictResult>> StartScanAsync(
        IReadOnlyList<string> targetFiles,
        string profilePath,
        PipelineScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // A run stays "running" until its last file has unwound, even after Cancel(), so two runs never overlap
        // and share the stopwatch, sampler and pause gate.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A scan is still running or stopping. Wait for it to finish first.");
        }

        try
        {
            return await RunAsync(targetFiles, profilePath, options ?? new PipelineScanOptions(), cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<IReadOnlyList<FileVerdictResult>> RunAsync(
        IReadOnlyList<string> targetFiles,
        string profilePath,
        PipelineScanOptions opt,
        CancellationToken cancellationToken)
    {
        var targetProfile = VoiceProfile.LoadFromFile(profilePath);
        _scanner.EnsureCompatible(targetProfile);
        double threshold = opt.Threshold ?? OperatingPoint.Threshold;
        double clusterThreshold = opt.ClusterThreshold ?? OperatingPoint.ClusterDistanceThreshold;

        _scanCts?.Dispose();
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _scanCts.Token;

        _pauseEvent.Set();
        _stopwatch.Restart();
        SetState(ScanExecutionState.Scanning);

        int totalFiles = targetFiles.Count;
        _currentProgress = CreateInitialProgress(totalFiles) with { State = ScanExecutionState.Scanning };
        NotifyProgress();

        List<FileVerdictResult> results = new(totalFiles);
        double totalProcessedAudioSeconds = 0.0;
        int matches = 0;
        int possibles = 0;
        int noMatches = 0;
        int errors = 0;

        // Overall progress is weighted by file size, a cheap stand-in for duration that keeps the bar and ETA honest
        // when a queue mixes short clips with multi-hour recordings.
        long[] weights = targetFiles.Select(FileWeight).ToArray();
        double totalWeight = weights.Sum();
        double completedWeight = 0.0;
        var fileFraction = new double[totalFiles];
        int headIndex = 0;

        void Publish(string fileName)
        {
            int head = Volatile.Read(ref headIndex);
            double headProgress = head < totalFiles ? Volatile.Read(ref fileFraction[head]) : 1.0;
            double fraction = head >= totalFiles ? 1.0
                : totalWeight > 0 ? (completedWeight + headProgress * weights[head]) / totalWeight : 0.0;
            UpdateProgress(head, totalFiles, fileName, headProgress, fraction, totalProcessedAudioSeconds, matches, possibles, noMatches, errors);
        }

        // Pause holds every file, including those already decoding or embedding, at the next chunk or batch boundary.
        Task WaitIfPaused(CancellationToken token) =>
            _pauseEvent.IsSet ? Task.CompletedTask : Task.Run(() => _pauseEvent.Wait(token), token);

        async Task<FileScanResult> ScanOne(int index, string filePath, CancellationToken token)
        {
            await WaitIfPaused(token);
            try
            {
                return await _scanner.ScanFileAsync(
                    filePath,
                    targetProfile,
                    threshold: threshold,
                    audioTrackIndex: opt.AudioTrackIndex,
                    windowDurationSec: opt.WindowDurationSec,
                    hopDurationSec: opt.HopDurationSec,
                    mergeToleranceSec: opt.MergeToleranceSec,
                    clusterDistanceThreshold: clusterThreshold,
                    enableClustering: opt.UseClustering,
                    enableTemporalSmoothing: opt.TemporalSmoothing,
                    peakDelta: opt.PeakDelta,
                    neighborToleranceSec: opt.NeighborToleranceSec,
                    normalizer: opt.Normalizer,
                    reportProgress: fraction =>
                    {
                        Volatile.Write(ref fileFraction[index], fraction);
                        if (index == Volatile.Read(ref headIndex)) Publish(Path.GetFileName(filePath));
                    },
                    cancellationToken: token,
                    pauseGate: WaitIfPaused);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                VoiceScanLogger.Error("BackgroundScanController", $"Error scanning file {filePath}", ex);
                return PipelineScanner.CreateErrorResult(filePath, opt.AudioTrackIndex, ex);
            }
        }

        try
        {
            _gpuSampler.Start(_scanner.EmbeddingModel.IsCudaActive);
            Publish(Path.GetFileName(targetFiles.FirstOrDefault() ?? string.Empty));

            await Task.Run(async () =>
            {
                int i = 0;
                await foreach (var scanResult in OrderedPrefetch.RunAsync(
                    targetFiles, PipelineScanner.FilePrefetchDepth, ScanOne, ct))
                {
                    string filePath = targetFiles[i];
                    string fileName = Path.GetFileName(filePath);

                    WaveformEnvelope? waveform = await BuildWaveformAsync(filePath, scanResult, ct);

                    List<HitSegmentResult> hits = [];
                    for (int s = 0; s < scanResult.Segments.Count; s++)
                    {
                        var seg = scanResult.Segments[s];
                        hits.Add(new HitSegmentResult(
                            SegmentId: HitSegmentResult.CreateId(filePath, scanResult.FileHash, s, seg.StartTimeSeconds),
                            FilePath: filePath,
                            StartTimeSeconds: seg.StartTimeSeconds,
                            EndTimeSeconds: seg.EndTimeSeconds,
                            DurationSeconds: seg.EndTimeSeconds - seg.StartTimeSeconds,
                            Verdict: seg.Verdict,
                            Confidence: seg.Confidence,
                            ReasonFlags: seg.ReasonFlags,
                            SegmentEmbedding: seg.Embedding,
                            FileHash: scanResult.FileHash));
                    }

                    if (scanResult.Verdict.Equals(PipelineScanner.ErrorVerdict, StringComparison.OrdinalIgnoreCase)) errors++;
                    else if (scanResult.Verdict.Equals("Match", StringComparison.OrdinalIgnoreCase)) matches++;
                    else if (scanResult.Verdict.Equals("Possible", StringComparison.OrdinalIgnoreCase)) possibles++;
                    else noMatches++;

                    totalProcessedAudioSeconds += scanResult.DurationSeconds;

                    var fileVerdict = new FileVerdictResult(
                        FilePath: filePath,
                        FileName: fileName,
                        FileHash: scanResult.FileHash,
                        DurationSeconds: scanResult.DurationSeconds,
                        OverallVerdict: scanResult.Verdict,
                        MaxConfidence: scanResult.MaxConfidence,
                        Segments: hits,
                        Waveform: waveform,
                        ErrorMessage: scanResult.Error,
                        AudioTrackIndex: scanResult.AudioTrackIndex,
                        ProfileName: targetProfile.ProfileName,
                        ProfilePath: profilePath);

                    results.Add(fileVerdict);
                    FileCompleted?.Invoke(this, fileVerdict);

                    completedWeight += weights[i];
                    i++;
                    Volatile.Write(ref headIndex, i);
                    Publish(i < totalFiles ? Path.GetFileName(targetFiles[i]) : fileName);
                }
            }, ct);

            SetState(ScanExecutionState.Completed);
        }
        catch (OperationCanceledException)
        {
            SetState(ScanExecutionState.Cancelled);
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Error("BackgroundScanController", "Scan failed", ex);
            SetState(ScanExecutionState.Failed);
            throw;
        }
        finally
        {
            _stopwatch.Stop();
            _gpuSampler.Stop();
        }

        return results;
    }

    /// <summary>Envelope computed during the scan, or decoded separately for cache hits that predate stored envelopes.</summary>
    private async Task<WaveformEnvelope?> BuildWaveformAsync(string filePath, FileScanResult scanResult, CancellationToken ct)
    {
        if (scanResult.WaveformMinPeaks is { Length: > 0 } minPeaks && scanResult.WaveformMaxPeaks != null)
        {
            return new WaveformEnvelope(minPeaks, scanResult.WaveformMaxPeaks, scanResult.DurationSeconds, minPeaks.Length);
        }
        if (scanResult.Error is not null)
        {
            return null;
        }

        try
        {
            return await _waveformService.GenerateEnvelopeAsync(filePath, 300, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing preview must not fail the whole scan; the verdict for this file is already known.
            VoiceScanLogger.Warn("BackgroundScanController", $"Could not build waveform for {filePath}: {ex.Message}");
            return null;
        }
    }

    public void Pause()
    {
        if (_state == ScanExecutionState.Scanning)
        {
            _pauseEvent.Reset();
            _stopwatch.Stop();
            SetState(ScanExecutionState.Paused);
        }
    }

    public void Resume()
    {
        if (_state == ScanExecutionState.Paused)
        {
            _pauseEvent.Set();
            _stopwatch.Start();
            SetState(ScanExecutionState.Scanning);
        }
    }

    /// <summary>Requests cancellation. The state becomes Cancelled once in-flight work has actually stopped.</summary>
    public void Cancel()
    {
        if (_state == ScanExecutionState.Scanning || _state == ScanExecutionState.Paused)
        {
            _scanCts?.Cancel();
            _pauseEvent.Set(); // unblock if paused
        }
    }

    private void UpdateProgress(
        int completedFiles,
        int totalFiles,
        string currentFileName,
        double fileProgress,
        double overallFraction,
        double processedAudioSeconds,
        int matches,
        int possibles,
        int noMatches,
        int errors)
    {
        TimeSpan elapsed = _stopwatch.Elapsed;
        double overallPct = Math.Clamp(overallFraction * 100.0, 0.0, 100.0);

        TimeSpan? eta = null;
        if (overallPct > 1.0 && elapsed.TotalSeconds > 0.5)
        {
            double remainingPct = 100.0 - overallPct;
            double secondsRemaining = (elapsed.TotalSeconds / overallPct) * remainingPct;
            eta = TimeSpan.FromSeconds(Math.Max(0, secondsRemaining));
        }

        double realtimeMultiple = elapsed.TotalSeconds > 0 ? processedAudioSeconds / elapsed.TotalSeconds : 0.0;
        double ramMb = Environment.WorkingSet / (1024.0 * 1024.0);

        _currentProgress = new OverallScanProgress(
            State: _state,
            TotalFiles: totalFiles,
            ProcessedFiles: completedFiles,
            CurrentFileIndex: Math.Min(completedFiles + 1, totalFiles),
            CurrentFileName: currentFileName,
            CurrentFileProgress: fileProgress,
            OverallProgressPercent: overallPct,
            ElapsedTime: elapsed,
            EstimatedTimeRemaining: eta,
            RealtimeMultiple: realtimeMultiple,
            GpuUtilizationPercent: _gpuSampler.Latest,
            MemoryMegabytes: ramMb,
            TotalMatchesFound: matches,
            TotalPossibleFound: possibles,
            TotalNoMatchFound: noMatches,
            TotalErrors: errors);

        NotifyProgress();
    }

    private static long FileWeight(string path)
    {
        try { return Math.Max(1, new FileInfo(path).Length); }
        catch (IOException) { return 1; }
    }

    private void SetState(ScanExecutionState newState)
    {
        _state = newState;
        _currentProgress = _currentProgress with { State = newState };
        StateChanged?.Invoke(this, newState);
        NotifyProgress();
    }

    private void NotifyProgress()
    {
        ProgressChanged?.Invoke(this, _currentProgress);
    }

    private static OverallScanProgress CreateInitialProgress(int totalFiles) =>
        new(ScanExecutionState.Idle, totalFiles, 0, 0, null, 0.0, 0.0, TimeSpan.Zero, null, 0.0, null, 0.0, 0, 0, 0);

    public void Dispose()
    {
        _pauseEvent.Dispose();
        _scanCts?.Dispose();
        _gpuSampler.Dispose();
    }
}
