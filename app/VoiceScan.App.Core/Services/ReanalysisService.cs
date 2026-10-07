using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.Services;

/// <summary>A clip (or section of one) to analyse again with the active models.</summary>
public sealed record ReanalysisJob(long ClipId, string FilePath, string FileName, SidecarAnalysisRequest Request);

/// <summary>
/// Re-analyses clips one at a time in the background and records each run in the moderation store, including runs
/// that fail. Events are raised on the thread that created the service (the UI thread in the app).
/// </summary>
public sealed class ReanalysisService : IDisposable
{
    private readonly ClipAnalyzer _analyzer;
    private readonly ModerationStore _store;
    private readonly string? _profilesDirectory;
    private readonly SynchronizationContext? _context;
    private readonly object _gate = new();
    private readonly Queue<ReanalysisJob> _queue = new();
    private CancellationTokenSource _cts = new();
    private Task _worker = Task.CompletedTask;
    private bool _running;
    private int _completed;
    private int _failed;
    private int _total;
    private string? _currentFile;
    private string? _lastError;

    public ReanalysisService(ClipAnalyzer analyzer, ModerationStore store, string? profilesDirectory = null)
    {
        _analyzer = analyzer;
        _store = store;
        _profilesDirectory = profilesDirectory;
        _context = SynchronizationContext.Current;
    }

    /// <summary>Progress or running state changed.</summary>
    public event Action? StateChanged;

    /// <summary>A clip's data changed (its re-analysis finished, successfully or not).</summary>
    public event Action<long>? ClipFinished;

    public bool IsRunning { get { lock (_gate) return _running; } }
    public int Completed { get { lock (_gate) return _completed; } }
    public int Failed { get { lock (_gate) return _failed; } }
    public int Total { get { lock (_gate) return _total; } }
    public string? CurrentFile { get { lock (_gate) return _currentFile; } }
    public string? LastError { get { lock (_gate) return _lastError; } }

    /// <summary>One line describing progress, for the Clips and Models pages.</summary>
    public string StatusText
    {
        get
        {
            lock (_gate)
            {
                string failed = _failed > 0 ? $", {_failed} failed" : string.Empty;
                if (_running) return $"Re-analysing {_currentFile} ({_completed + 1} of {_total}{failed})";
                if (_total == 0) return string.Empty;
                string last = _lastError != null ? $" Last error: {_lastError}" : string.Empty;
                return $"Re-analysed {_completed - _failed} of {_total} clip(s){failed}.{last}";
            }
        }
    }

    /// <summary>Queues jobs and starts working through them if idle.</summary>
    public void Enqueue(IEnumerable<ReanalysisJob> jobs)
    {
        lock (_gate)
        {
            int added = 0;
            foreach (var job in jobs)
            {
                _queue.Enqueue(job);
                added++;
            }
            if (added == 0) return;

            if (!_running)
            {
                _running = true;
                _completed = 0;
                _failed = 0;
                _total = 0;
                _lastError = null;
                _cts.Dispose();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _worker = Task.Run(() => RunAsync(ct));
            }
            _total += added;
        }
        Raise(() => StateChanged?.Invoke());
    }

    /// <summary>Stops after the current step and drops the queued jobs.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _queue.Clear();
            _cts.Cancel();
        }
    }

    /// <summary>Completes when the queue is empty and the last job has finished.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _worker;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                ReanalysisJob job;
                lock (_gate)
                {
                    // Stopping under the lock means a job enqueued from now on starts a new worker.
                    if (_queue.Count == 0 || ct.IsCancellationRequested)
                    {
                        StopLocked();
                        break;
                    }
                    job = _queue.Dequeue();
                    _currentFile = job.FileName;
                }
                Raise(() => StateChanged?.Invoke());

                await RunJobAsync(job, ct);

                lock (_gate) _completed++;
                Raise(() => ClipFinished?.Invoke(job.ClipId));
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) VoiceScanLogger.Info(nameof(ReanalysisService), "Re-analysis cancelled.");
            else VoiceScanLogger.Error(nameof(ReanalysisService), "Re-analysis stopped", ex);
            lock (_gate) StopLocked();
        }
        Raise(() => StateChanged?.Invoke());
    }

    private void StopLocked()
    {
        _queue.Clear();
        _running = false;
        _currentFile = null;
    }

    private async Task RunJobAsync(ReanalysisJob job, CancellationToken ct)
    {
        // Held for the whole job so the model's version, vectors and threshold stay consistent.
        using var modelUse = _analyzer.EmbeddingModel is SwappableEmbeddingModel swappable ? swappable.BeginUse() : null;
        var model = _analyzer.EmbeddingModel;
        var request = job.Request;
        var run = new AnalysisRunInfo(model.ModelVersion, AnalysisRunInfo.ReanalysisTrigger, request.Preset, request.StartSeconds, request.EndSeconds);
        try
        {
            var result = await _analyzer.AnalyseAsync(job.FilePath, request, ct: ct);
            var voices = ProfileLibrary.LoadKnownVoices(model.ModelVersion, _profilesDirectory);
            await _store.IngestAsync(result, run, voices, model.OperatingPoint.Threshold, job.ClipId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VoiceScanLogger.Error(nameof(ReanalysisService), $"Re-analysis of {job.FilePath} failed", ex);
            lock (_gate)
            {
                _failed++;
                _lastError = $"{job.FileName}: {ex.Message}";
            }
            try
            {
                await _store.RecordFailedRunAsync(job.ClipId, run, ex.Message, CancellationToken.None);
            }
            catch (Exception recordEx)
            {
                VoiceScanLogger.Error(nameof(ReanalysisService), $"Could not record the failed run for {job.FilePath}", recordEx);
            }
        }
    }

    private void Raise(Action action)
    {
        if (_context != null) _context.Post(_ => action(), null);
        else action();
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }
}
