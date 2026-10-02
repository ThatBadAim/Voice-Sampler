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

    public bool HasSelectedFile => _selectedFile != null;

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

    public void SetResults(IEnumerable<FileVerdictResult> results)
    {
        _allResults.Clear();
        _allResults.AddRange(results);
        ApplyFilterAndSort();

        if (FilteredFiles.Count > 0 && SelectedFile == null)
        {
            SelectedFile = FilteredFiles[0];
        }
    }

    public void AddResult(FileVerdictResult result)
    {
        _allResults.Add(result);
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

        FilteredFiles.Clear();
        foreach (var item in query)
        {
            FilteredFiles.Add(item);
        }

        if (SelectedFile != null && !FilteredFiles.Contains(SelectedFile))
        {
            SelectedFile = FilteredFiles.FirstOrDefault();
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
