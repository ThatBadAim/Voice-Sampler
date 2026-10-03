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

    private string? _selectedTargetFolderPath;
    private string? _selectedProfilePath;
    private VoiceProfileSummary? _selectedProfile;
    private bool _useClustering = true;
    private bool _useTemporalSmoothing = true;
    private double _clusterThreshold = 0.40;
    private OverallScanProgress _progress;
    private string? _statusMessage;
    private bool _isScanning;
    private bool _isPaused;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> TargetFiles { get; } = [];
    public ObservableCollection<FileVerdictResult> CompletedFiles { get; } = [];
    public ObservableCollection<VoiceProfileSummary> Profiles { get; } = [];

    /// <summary>Raised on the UI context when a scan ends in the Completed state.</summary>
    public event Action? ScanFinished;

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

    public string? SelectedTargetFolderPath
    {
        get => _selectedTargetFolderPath;
        set
        {
            if (SetField(ref _selectedTargetFolderPath, value))
            {
                LoadFilesFromFolder(value);
                OnPropertyChanged(nameof(CanStartScan));
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
            }
        }
    }

    public bool UseClustering
    {
        get => _useClustering;
        set => SetField(ref _useClustering, value);
    }

    public bool UseTemporalSmoothing
    {
        get => _useTemporalSmoothing;
        set => SetField(ref _useTemporalSmoothing, value);
    }

    public double ClusterThreshold
    {
        get => _clusterThreshold;
        set => SetField(ref _clusterThreshold, value);
    }

    public OverallScanProgress Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

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
            }
        }
    }

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

    public ScanDashboardViewModel(IBackgroundScanController scanController)
    {
        _scanController = scanController;
        RefreshProfiles();
        _progress = _scanController.CurrentProgress;

        // Controller events fire on worker threads; collections bound by the UI must change on the creating context.
        var uiContext = SynchronizationContext.Current;
        void OnUi(Action action)
        {
            if (uiContext == null || SynchronizationContext.Current == uiContext) action();
            else uiContext.Post(_ => action(), null);
        }

        _scanController.ProgressChanged += (s, e) => OnUi(() =>
        {
            Progress = e;
        });

        _scanController.FileCompleted += (s, file) => OnUi(() =>
        {
            CompletedFiles.Add(file);
        });

        _scanController.StateChanged += (s, state) => OnUi(() =>
        {
            IsScanning = state == ScanExecutionState.Scanning || state == ScanExecutionState.Paused;
            IsPaused = state == ScanExecutionState.Paused;

            StatusMessage = state switch
            {
                ScanExecutionState.Scanning => "Scan in progress...",
                ScanExecutionState.Paused => "Scan paused.",
                ScanExecutionState.Completed => $"Scan completed. Processed {CompletedFiles.Count} files.",
                ScanExecutionState.Cancelled => "Scan cancelled by user.",
                ScanExecutionState.Failed => "Scan encountered an error.",
                _ => null
            };

            if (state == ScanExecutionState.Completed) ScanFinished?.Invoke();
        });
    }

    /// <summary>Reloads saved profiles; keeps the current choice, or selects <paramref name="selectPath"/> when given.</summary>
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

    public void LoadFilesFromFolder(string? folderPath)
    {
        TargetFiles.Clear();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        string[] extensions = [".wav", ".mp3", ".flac", ".ogg", ".mp4", ".mkv", ".m4a"];
        var files = Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f);

        foreach (var file in files)
        {
            TargetFiles.Add(file);
        }

        StatusMessage = $"Loaded {TargetFiles.Count} media files from {Path.GetFileName(folderPath)}.";
        OnPropertyChanged(nameof(CanStartScan));
    }

    public async Task StartScanAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStartScan || string.IsNullOrWhiteSpace(_selectedProfilePath)) return;

        CompletedFiles.Clear();
        StatusMessage = "Initiating scan on background worker thread...";

        var options = new PipelineScanOptions(
            ClusterThreshold: _clusterThreshold,
            TemporalSmoothing: _useTemporalSmoothing,
            UseClustering: _useClustering);

        try
        {
            await _scanController.StartScanAsync(
                TargetFiles.ToList(),
                _selectedProfilePath,
                options,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan was cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
    }

    public void PauseScan() => _scanController.Pause();
    public void ResumeScan() => _scanController.Resume();
    public void CancelScan() => _scanController.Cancel();

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
