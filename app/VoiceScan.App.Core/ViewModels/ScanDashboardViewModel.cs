using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;

namespace VoiceScan.App.Core.ViewModels;

public sealed class ScanDashboardViewModel : INotifyPropertyChanged
{
    private readonly IBackgroundScanController _scanController;
    private readonly IWaveformService _waveformService;
    private readonly UserSettingsStore? _settings;

    private string? _selectedProfilePath;
    private VoiceProfileSummary? _selectedProfile;
    private bool _useClustering = true;
    private bool _useTemporalSmoothing = true;
    private double? _clusterThreshold;
    private ReportExportSettings? _pendingScan;
    private OverallScanProgress _progress;
    private string? _statusMessage;
    private bool _isScanning;
    private bool _isPaused;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MediaFileItem> TargetFiles { get; } = [];
    public ObservableCollection<FileVerdictResult> CompletedFiles { get; } = [];
    public ObservableCollection<VoiceProfileSummary> Profiles { get; } = [];

    /// <summary>Raised on the UI context when a scan ends in the Completed state.</summary>
    public event Action? ScanFinished;

    /// <summary>Raised on the UI context when a scan starts, with the settings it actually runs with.</summary>
    public event Action<ReportExportSettings>? ScanStarted;

    public bool HasProfiles => Profiles.Count > 0;

    public VoiceProfileSummary? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetField(ref _selectedProfile, value))
            {
                SelectedProfilePath = value?.Path;
            }
        }
    }

    public string? SelectedProfilePath
    {
        get => _selectedProfilePath;
        set
        {
            if (SetField(ref _selectedProfilePath, value))
            {
                OnPropertyChanged(nameof(CanStartScan));
                if (_settings is not null && value is not null)
                {
                    _settings.Current.LastProfilePath = value;
                    _settings.Save();
                }
            }
        }
    }

    public bool UseClustering
    {
        get => _useClustering;
        set
        {
            if (!SetField(ref _useClustering, value) || _settings is null) return;
            _settings.Current.UseClustering = value;
            _settings.Save();
        }
    }

    public bool UseTemporalSmoothing
    {
        get => _useTemporalSmoothing;
        set
        {
            if (!SetField(ref _useTemporalSmoothing, value) || _settings is null) return;
            _settings.Current.UseTemporalSmoothing = value;
            _settings.Save();
        }
    }

    /// <summary>AHC stopping distance; the loaded model's measured default until the user chooses one.</summary>
    public double ClusterThreshold
    {
        get => _clusterThreshold ?? _scanController.OperatingPoint.ClusterDistanceThreshold;
        set
        {
            if (_clusterThreshold == value) return;
            _clusterThreshold = value;
            OnPropertyChanged();
            if (_settings is null) return;
            _settings.Current.ClusterThreshold = value;
            _settings.Save();
        }
    }

    /// <summary>Folder the file pickers should open in; the parent of the last file or folder added.</summary>
    public string? LastBrowseFolder
    {
        get => _settings?.Current.LastBrowseFolder;
        set
        {
            if (_settings is null || _settings.Current.LastBrowseFolder == value) return;
            _settings.Current.LastBrowseFolder = value;
            _settings.Save();
        }
    }

    public OverallScanProgress Progress
    {
        get => _progress;
        private set
        {
            if (SetField(ref _progress, value))
            {
                OnPropertyChanged(nameof(HasErrors));
                OnPropertyChanged(nameof(EstimatedTimeRemainingText));
            }
        }
    }

    public string EstimatedTimeRemainingText =>
        Progress.EstimatedTimeRemaining is { } eta ? TimeFormat.Clock(eta.TotalSeconds) : "--:--";

    public bool HasErrors => Progress.TotalErrors > 0;

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(CanStartScan));
                OnPropertyChanged(nameof(CanCancelScan));
                OnPropertyChanged(nameof(CanPauseScan));
                OnPropertyChanged(nameof(CanEditFiles));
            }
        }
    }

    public bool CanEditFiles => !IsScanning;

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetField(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(CanPauseScan));
                OnPropertyChanged(nameof(CanResumeScan));
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool CanStartScan => !IsScanning && TargetFiles.Count > 0 && !string.IsNullOrWhiteSpace(_selectedProfilePath) && File.Exists(_selectedProfilePath);
    public bool CanCancelScan => IsScanning;
    public bool CanPauseScan => IsScanning && !IsPaused;
    public bool CanResumeScan => IsScanning && IsPaused;

    public ScanDashboardViewModel(
        IBackgroundScanController scanController,
        IWaveformService? waveformService = null,
        UserSettingsStore? settings = null)
    {
        _scanController = scanController;
        _waveformService = waveformService ?? new WaveformService();
        _settings = settings;
        if (settings is not null)
        {
            _useClustering = settings.Current.UseClustering;
            _useTemporalSmoothing = settings.Current.UseTemporalSmoothing;
            _clusterThreshold = settings.Current.ClusterThreshold;
            _selectedProfilePath = settings.Current.LastProfilePath;
        }
        TargetFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanStartScan));
        RefreshProfiles();
        _progress = _scanController.CurrentProgress;

        // Controller events fire on worker threads; collections bound by the UI must change on the creating context.
        var uiContext = SynchronizationContext.Current;
        void OnUi(Action action)
        {
            if (uiContext == null || SynchronizationContext.Current == uiContext) action();
            else uiContext.Post(_ => action(), null);
        }

        // Workers report progress far faster than the display refreshes. Only the newest value matters, so keep at
        // most one UI callback queued instead of flooding the dispatcher and delaying frames.
        var progressGate = new object();
        OverallScanProgress latestProgress = default;
        bool progressQueued = false;
        _scanController.ProgressChanged += (s, e) =>
        {
            lock (progressGate)
            {
                latestProgress = e;
                if (progressQueued) return;
                progressQueued = true;
            }

            OnUi(() =>
            {
                OverallScanProgress next;
                lock (progressGate)
                {
                    next = latestProgress;
                    progressQueued = false;
                }
                Progress = next;
            });
        };

        _scanController.FileCompleted += (s, file) => OnUi(() =>
        {
            CompletedFiles.Add(file);
        });

        _scanController.StateChanged += (s, state) => OnUi(() =>
        {
            IsScanning = state == ScanExecutionState.Scanning || state == ScanExecutionState.Paused;
            IsPaused = state == ScanExecutionState.Paused;

            if (state == ScanExecutionState.Scanning && _pendingScan is { } started)
            {
                _pendingScan = null;
                ScanStarted?.Invoke(started);
            }

            StatusMessage = state switch
            {
                ScanExecutionState.Scanning => "Scan in progress...",
                ScanExecutionState.Paused => "Scan paused.",
                ScanExecutionState.Completed => CompletionMessage(),
                ScanExecutionState.Cancelled => CompletedFiles.Count > 0
                    ? $"Scan cancelled. {CompletedFiles.Count} finished file{(CompletedFiles.Count == 1 ? "" : "s")} kept in Results."
                    : "Scan cancelled.",
                ScanExecutionState.Failed => "Scan stopped because of an error. Details are in the log.",
                _ => null
            };

            if (state == ScanExecutionState.Completed) ScanFinished?.Invoke();
        });
    }

    private string CompletionMessage()
    {
        int failed = CompletedFiles.Count(f => f.IsError);
        return failed == 0
            ? $"Scan completed. Processed {CompletedFiles.Count} files."
            : $"Scan completed. {failed} of {CompletedFiles.Count} files could not be scanned; see Results for the reasons.";
    }

    /// <summary>Reloads saved profiles; keeps the current choice, or selects <paramref name="selectPath"/> when given.</summary>
    /// <summary>The default cluster threshold comes from the active embedding model.</summary>
    public void OnEmbeddingModelChanged() => OnPropertyChanged(nameof(ClusterThreshold));

    public void RefreshProfiles(string? selectPath = null)
    {
        selectPath ??= _selectedProfilePath;
        Profiles.Clear();
        foreach (var profile in ProfileLibrary.List())
        {
            Profiles.Add(profile);
        }

        OnPropertyChanged(nameof(HasProfiles));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Path == selectPath) ?? Profiles.FirstOrDefault();
    }

    /// <summary>Adds files and folders (searched recursively); returns how many new media files were added.</summary>
    public int AddPaths(IEnumerable<string> paths)
    {
        var existing = new HashSet<string>(TargetFiles.Select(f => f.Path));
        int added = 0;
        foreach (var file in MediaFileCollector.Collect(paths))
        {
            if (!existing.Add(file)) continue;
            TargetFiles.Add(new MediaFileItem(file));
            added++;
        }

        StatusMessage = added == 0
            ? "No new audio or video files found in that selection."
            : $"Added {added} file{(added == 1 ? "" : "s")}. {TargetFiles.Count} queued for scanning.";
        return added;
    }

    public void RemoveFile(MediaFileItem file)
    {
        if (!IsScanning) TargetFiles.Remove(file);
    }

    public void ClearFiles()
    {
        if (IsScanning) return;
        TargetFiles.Clear();
        StatusMessage = null;
    }

    public Task ToggleWaveformAsync(MediaFileItem file) => WaveformPreview.ToggleAsync(file, _waveformService);

    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStartScan || string.IsNullOrWhiteSpace(_selectedProfilePath)) return;

        CompletedFiles.Clear();
        StatusMessage = "Initiating scan on background worker thread...";

        var options = new PipelineScanOptions(
            ClusterThreshold: _clusterThreshold,
            TemporalSmoothing: _useTemporalSmoothing,
            UseClustering: _useClustering);

        _pendingScan = new ReportExportSettings(
            ProfileName: SelectedProfile?.Name ?? Path.GetFileNameWithoutExtension(_selectedProfilePath),
            ModelId: _scanController.ModelId,
            EngineVersion: PipelineScanner.EngineVersion,
            Threshold: _scanController.OperatingPoint.Threshold,
            ClusterThreshold: ClusterThreshold,
            TemporalSmoothing: _useTemporalSmoothing,
            ScanDateUtc: DateTimeOffset.UtcNow,
            ClusteringEnabled: _useClustering);

        try
        {
            await _scanController.StartScanAsync(
                TargetFiles.Select(f => f.Path).ToList(),
                _selectedProfilePath,
                options,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // StateChanged already reported the cancellation.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan stopped: {ex.Message}";
        }
        finally
        {
            _pendingScan = null;
        }
    }

    public void PauseScan() => _scanController.Pause();
    public void ResumeScan() => _scanController.Resume();

    public void CancelScan()
    {
        if (!IsScanning) return;
        StatusMessage = "Cancelling… files in progress stop at the next safe point.";
        _scanController.Cancel();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
