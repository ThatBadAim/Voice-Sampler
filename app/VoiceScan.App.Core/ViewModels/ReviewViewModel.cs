using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.ViewModels;

public sealed class ReviewViewModel : INotifyPropertyChanged
{
    private readonly IReviewRepository _reviewRepository;
    private readonly IAudioPlaybackController _playbackController;

    private readonly List<ReviewQueueItem> _allPendingItems = [];
    private ReviewQueueItem? _selectedItem;
    private string _reviewerNotes = string.Empty;
    private bool _addToProfileOnConfirm = true;
    private string? _statusMessage;
    private bool _isProcessing;
    private string _selectedSegmentFilterMode = "All Segments";
    private string _selectedSpeaker = "All Speakers";

    public event PropertyChangedEventHandler? PropertyChanged;

    public static readonly IReadOnlyList<string> SegmentFilterModes = ["All Segments", "Flagged Violations Only"];

    public ObservableCollection<ReviewQueueItem> PendingQueue { get; } = [];
    public ObservableCollection<ReviewQueueItem> FilteredQueue => PendingQueue;
    public ObservableCollection<string> AvailableSpeakers { get; } = ["All Speakers"];
    public ObservableCollection<ReviewDecisionRecord> DecisionHistory { get; } = [];

    public ICommand FilterAllSegmentsCommand { get; }
    public ICommand FilterFlaggedViolationsOnlyCommand { get; }
    public ICommand FilterBySpeakerCommand { get; }

    public void FilterAllSegments()
    {
        SelectedSegmentFilterMode = "All Segments";
    }

    public void FilterFlaggedViolationsOnly()
    {
        SelectedSegmentFilterMode = "Flagged Violations Only";
    }

    public void FilterBySpeaker(string? speaker)
    {
        SelectedSpeaker = string.IsNullOrWhiteSpace(speaker) ? "All Speakers" : speaker;
    }

    public string SelectedSegmentFilterMode
    {
        get => _selectedSegmentFilterMode;
        set
        {
            if (SetField(ref _selectedSegmentFilterMode, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedSpeaker
    {
        get => _selectedSpeaker;
        set
        {
            if (SetField(ref _selectedSpeaker, value))
            {
                ApplyFilter();
            }
        }
    }

    public ReviewQueueItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetField(ref _selectedItem, value))
            {
                if (value != null)
                {
                    // The queue only knows the segment, not the file length; cover everything up to the segment end so seeks are not clamped.
                    _playbackController.LoadFile(value.Segment.FilePath, Math.Max(value.Segment.DurationSeconds, value.Segment.EndTimeSeconds));
                }
                ReviewerNotes = string.Empty;
                OnPropertyChanged(nameof(HasSelectedItem));
                OnPropertyChanged(nameof(CanPlaySnippet));
                OnPropertyChanged(nameof(SpeakerLabel));
                OnPropertyChanged(nameof(Transcript));
                OnPropertyChanged(nameof(IsFlagged));
                OnPropertyChanged(nameof(IsOffensive));
                OnPropertyChanged(nameof(ModerationViolations));
            }
        }
    }

    public string? SpeakerLabel => _selectedItem?.SpeakerLabel;
    public string Transcript => _selectedItem?.Transcript ?? string.Empty;
    public bool IsFlagged => _selectedItem?.IsFlagged ?? false;
    public bool IsOffensive => _selectedItem?.IsOffensive ?? false;
    public IReadOnlyList<string> ModerationViolations => _selectedItem?.ModerationViolations ?? Array.Empty<string>();

    public string ReviewerNotes
    {
        get => _reviewerNotes;
        set => SetField(ref _reviewerNotes, value);
    }

    public bool AddToProfileOnConfirm
    {
        get => _addToProfileOnConfirm;
        set => SetField(ref _addToProfileOnConfirm, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetField(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanAct));
            }
        }
    }

    public bool HasSelectedItem => _selectedItem != null;
    public bool CanAct => HasSelectedItem && !IsProcessing;
    public bool IsAudioAvailable => _playbackController.IsAudioAvailable;
    public bool CanPlaySnippet => HasSelectedItem && IsAudioAvailable;

    public ReviewViewModel(IReviewRepository reviewRepository, IAudioPlaybackController playbackController)
    {
        _reviewRepository = reviewRepository;
        _playbackController = playbackController;

        FilterAllSegmentsCommand = new RelayCommand(FilterAllSegments);
        FilterFlaggedViolationsOnlyCommand = new RelayCommand(FilterFlaggedViolationsOnly);
        FilterBySpeakerCommand = new RelayCommand<string>(FilterBySpeaker);
    }

    public async Task InitializeAsync()
    {
        await _reviewRepository.InitializeAsync();
        await RefreshHistoryAsync();
    }

    public void EnqueueSegments(IEnumerable<HitSegmentResult> segments, string profileName, string fileName, string? profilePath = null)
    {
        foreach (var seg in segments)
        {
            _allPendingItems.Add(new ReviewQueueItem(
                Segment: seg,
                FileName: fileName,
                ProfileName: profileName,
                DetectedAtUtc: DateTimeOffset.UtcNow,
                ProfilePath: profilePath));
        }

        UpdateAvailableSpeakers();
        ApplyFilter();
    }

    public void PlaySelectedSnippet()
    {
        if (_selectedItem != null)
        {
            _playbackController.PlaySegment(_selectedItem.Segment.StartTimeSeconds, _selectedItem.Segment.EndTimeSeconds);
        }
    }

    public async Task ConfirmSegmentAsync(CancellationToken cancellationToken = default)
    {
        if (_selectedItem == null || IsProcessing) return;

        IsProcessing = true;
        StatusMessage = "Recording confirmation...";

        var item = _selectedItem;
        try
        {
            var decisionRecord = await Task.Run(() => CreateDecisionRecord(item, ReviewDecision.Confirmed), cancellationToken);
            await _reviewRepository.RecordDecisionAsync(decisionRecord, cancellationToken);

            string profileNote = string.Empty;
            if (_addToProfileOnConfirm)
            {
                bool added = item.ProfilePath is { } profilePath && File.Exists(profilePath)
                    && await _reviewRepository.AugmentProfileWithConfirmedHitAsync(profilePath, decisionRecord, cancellationToken);
                profileNote = added
                    ? $" Added to voice '{item.ProfileName}'."
                    : $" Not added to voice '{item.ProfileName}': its profile file is missing or the segment has no usable embedding.";
            }

            _allPendingItems.Remove(item);
            UpdateAvailableSpeakers();
            ApplyFilter();
            await RefreshHistoryAsync(cancellationToken);
            StatusMessage = $"Confirmed segment [{item.Segment.StartTimeSeconds:F1}s - {item.Segment.EndTimeSeconds:F1}s].{profileNote}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to record decision: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task RejectSegmentAsync(CancellationToken cancellationToken = default)
    {
        if (_selectedItem == null || IsProcessing) return;

        IsProcessing = true;
        StatusMessage = "Recording rejection...";

        var item = _selectedItem;
        try
        {
            var decisionRecord = await Task.Run(() => CreateDecisionRecord(item, ReviewDecision.Rejected), cancellationToken);
            await _reviewRepository.RecordDecisionAsync(decisionRecord, cancellationToken);
            _allPendingItems.Remove(item);
            UpdateAvailableSpeakers();
            ApplyFilter();
            await RefreshHistoryAsync(cancellationToken);
            StatusMessage = $"Rejected segment [{item.Segment.StartTimeSeconds:F1}s - {item.Segment.EndTimeSeconds:F1}s].";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to record rejection: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void SeekToSelectedStart()
    {
        if (_selectedItem != null)
        {
            _playbackController.SeekTo(_selectedItem.Segment.Start);
        }
    }

    private void UpdateAvailableSpeakers()
    {
        var currentSpeaker = _selectedSpeaker;
        AvailableSpeakers.Clear();
        AvailableSpeakers.Add("All Speakers");

        var distinct = _allPendingItems
            .Select(i => i.SpeakerLabel)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s);

        foreach (var spk in distinct)
        {
            AvailableSpeakers.Add(spk!);
        }

        if (!AvailableSpeakers.Contains(currentSpeaker))
        {
            _selectedSpeaker = "All Speakers";
            OnPropertyChanged(nameof(SelectedSpeaker));
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<ReviewQueueItem> query = _allPendingItems;
        if (string.Equals(_selectedSegmentFilterMode, "Flagged Violations Only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_selectedSegmentFilterMode, "Offensive / Flagged Only", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(i => i.IsOffensive || i.IsFlagged || (i.ModerationViolations != null && i.ModerationViolations.Count > 0));
        }

        if (!string.Equals(_selectedSpeaker, "All Speakers", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_selectedSpeaker))
        {
            query = query.Where(i => string.Equals(i.SpeakerLabel, _selectedSpeaker, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.ToList();
        PendingQueue.Clear();
        foreach (var item in list)
        {
            PendingQueue.Add(item);
        }

        if (SelectedItem != null && !PendingQueue.Contains(SelectedItem))
        {
            SelectedItem = PendingQueue.FirstOrDefault();
        }
        else if (SelectedItem == null && PendingQueue.Count > 0)
        {
            SelectedItem = PendingQueue[0];
        }
    }

    /// <summary>
    /// The decision id combines profile and segment: the same segment can be judged separately for each voice,
    /// and a decision for one voice never overwrites another's.
    /// </summary>
    private ReviewDecisionRecord CreateDecisionRecord(ReviewQueueItem item, ReviewDecision decision) => new(
        SegmentId: $"{item.ProfileName}|{item.Segment.SegmentId}",
        FilePath: item.Segment.FilePath,
        FileHash: !string.IsNullOrEmpty(item.Segment.FileHash)
            ? item.Segment.FileHash
            : FastFileHasher.ComputeFastHash(item.Segment.FilePath),
        ProfileName: item.ProfileName,
        StartTimeSeconds: item.Segment.StartTimeSeconds,
        EndTimeSeconds: item.Segment.EndTimeSeconds,
        Confidence: item.Segment.Confidence,
        OriginalVerdict: item.Segment.Verdict,
        ReasonFlags: item.Segment.ReasonFlags,
        Decision: decision,
        DecidedAtUtc: DateTimeOffset.UtcNow,
        Notes: _reviewerNotes,
        SegmentEmbedding: item.Segment.SegmentEmbedding);

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        var records = await _reviewRepository.GetDecisionsAsync(cancellationToken: cancellationToken);
        DecisionHistory.Clear();
        foreach (var r in records)
        {
            DecisionHistory.Add(r);
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
