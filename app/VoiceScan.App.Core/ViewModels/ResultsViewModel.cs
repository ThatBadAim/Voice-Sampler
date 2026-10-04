using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;

namespace VoiceScan.App.Core.ViewModels;

public sealed class ResultsViewModel : INotifyPropertyChanged
{
    private readonly IAudioPlaybackController _playbackController;

    private readonly List<FileVerdictResult> _allResults = [];
    private VerdictFilter _selectedFilter = VerdictFilter.All;
    private ResultSortColumn _selectedSort = ResultSortColumn.MaxConfidence;
    private bool _sortDescending = true;
    private string _searchQuery = string.Empty;

    private FileVerdictResult? _selectedFile;
    private HitSegmentResult? _selectedSegment;
    private double _playbackPosition;
    private bool _isPlaying;
    private bool _isExporting;
    private string? _exportStatus;
    private bool _rebuildingList;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FileVerdictResult> FilteredFiles { get; } = [];

    public VerdictFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetField(ref _selectedFilter, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public ResultSortColumn SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetField(ref _selectedSort, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (SetField(ref _sortDescending, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetField(ref _searchQuery, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public FileVerdictResult? SelectedFile
    {
        get => _selectedFile;
        set
        {
            // Rebuilding FilteredFiles makes the bound list push a transient null selection; keep the real one.
            if (_rebuildingList) return;
            if (SetField(ref _selectedFile, value))
            {
                if (value != null)
                {
                    _playbackController.LoadFile(value.FilePath, value.DurationSeconds);
                    SelectedSegment = value.Segments.FirstOrDefault();
                }
                OnPropertyChanged(nameof(HasSelectedFile));
            }
        }
    }

    public HitSegmentResult? SelectedSegment
    {
        get => _selectedSegment;
        set => SetField(ref _selectedSegment, value);
    }

    public double PlaybackPosition
    {
        get => _playbackPosition;
        private set => SetField(ref _playbackPosition, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    /// <summary>Settings of the scan whose results are shown; recorded in exported reports.</summary>
    public ReportExportSettings? ExportSettings { get; private set; }

    public bool HasResults => _allResults.Count > 0;

    public bool IsExporting
    {
        get => _isExporting;
        private set => SetField(ref _isExporting, value);
    }

    public string? ExportStatus
    {
        get => _exportStatus;
        private set => SetField(ref _exportStatus, value);
    }

    public bool HasSelectedFile => _selectedFile != null;
    public bool IsAudioAvailable => _playbackController.IsAudioAvailable;

    public ResultsViewModel(IAudioPlaybackController playbackController)
    {
        _playbackController = playbackController;

        _playbackController.PositionChanged += (s, pos) =>
        {
            PlaybackPosition = pos;
        };

        _playbackController.PlayStateChanged += (s, playing) =>
        {
            IsPlaying = playing;
        };
    }

    /// <summary>Writes a CSV, a PDF and the matching audio clips for every scanned file into <paramref name="outputDirectory"/>.</summary>
    public async Task ExportReportAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (ExportSettings is null || _allResults.Count == 0) return;

        IsExporting = true;
        ExportStatus = "Exporting report and audio clips...";
        try
        {
            var result = await new EvidenceReportExporter().ExportReportAsync(
                _allResults.ToList(), ExportSettings, outputDirectory, cancellationToken: cancellationToken);
            ExportStatus = $"Exported {result.TotalSegmentsExported} hit(s) with {result.ExtractedAudioClipPaths.Count} audio clip(s) to {outputDirectory}.";
        }
        catch (OperationCanceledException)
        {
            ExportStatus = "Export cancelled.";
        }
        catch (Exception ex)
        {
            ExportStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    /// <summary>Starts a new result set for a scan run with <paramref name="settings"/>; earlier results are cleared.</summary>
    public void BeginScan(ReportExportSettings settings)
    {
        ExportSettings = settings;
        ExportStatus = null;
        _playbackController.Pause();
        SetResults([]);
        SelectedFile = null;
    }

    public void SetResults(IEnumerable<FileVerdictResult> results)
    {
        _allResults.Clear();
        _allResults.AddRange(results);
        OnPropertyChanged(nameof(HasResults));
        ApplyFilterAndSort();

        if (FilteredFiles.Count > 0 && SelectedFile == null)
        {
            SelectedFile = FilteredFiles[0];
        }
    }

    public void AddResult(FileVerdictResult result)
    {
        _allResults.Add(result);
        OnPropertyChanged(nameof(HasResults));
        ApplyFilterAndSort();
    }

    public void PlayPause()
    {
        _playbackController.TogglePlayPause();
    }

    public void SeekTo(double positionSeconds)
    {
        _playbackController.SeekTo(positionSeconds);
    }

    public void PlayHitSegment(HitSegmentResult segment)
    {
        SelectedSegment = segment;
        _playbackController.PlaySegment(segment.StartTimeSeconds, segment.EndTimeSeconds);
    }

    private void ApplyFilterAndSort()
    {
        IEnumerable<FileVerdictResult> query = _allResults;

        // Filter by Verdict
        query = _selectedFilter switch
        {
            VerdictFilter.Match => query.Where(r => r.OverallVerdict.Equals("Match", StringComparison.OrdinalIgnoreCase)),
            VerdictFilter.Possible => query.Where(r => r.OverallVerdict.Equals("Possible", StringComparison.OrdinalIgnoreCase)),
            VerdictFilter.NoMatch => query.Where(r => r.OverallVerdict.Equals("No match", StringComparison.OrdinalIgnoreCase)),
            VerdictFilter.Error => query.Where(r => r.IsError),
            _ => query
        };

        // Filter by Search Query
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            query = query.Where(r => r.FileName.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                                     r.FilePath.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        query = _selectedSort switch
        {
            ResultSortColumn.FileName => _sortDescending ? query.OrderByDescending(r => r.FileName) : query.OrderBy(r => r.FileName),
            ResultSortColumn.Verdict => _sortDescending ? query.OrderByDescending(r => r.OverallVerdict) : query.OrderBy(r => r.OverallVerdict),
            ResultSortColumn.Duration => _sortDescending ? query.OrderByDescending(r => r.DurationSeconds) : query.OrderBy(r => r.DurationSeconds),
            ResultSortColumn.HitCount => _sortDescending ? query.OrderByDescending(r => r.Segments.Count) : query.OrderBy(r => r.Segments.Count),
            _ => _sortDescending ? query.OrderByDescending(r => r.MaxConfidence) : query.OrderBy(r => r.MaxConfidence)
        };

        _rebuildingList = true;
        try
        {
            FilteredFiles.Clear();
            foreach (var item in query)
            {
                FilteredFiles.Add(item);
            }
        }
        finally
        {
            _rebuildingList = false;
        }

        if (SelectedFile != null && !FilteredFiles.Contains(SelectedFile))
        {
            SelectedFile = FilteredFiles.FirstOrDefault();
        }
        else
        {
            OnPropertyChanged(nameof(SelectedFile)); // re-select the kept item in the rebuilt list
        }
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
