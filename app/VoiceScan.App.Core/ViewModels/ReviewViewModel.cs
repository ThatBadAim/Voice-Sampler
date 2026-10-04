using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.ViewModels;

public sealed class ReviewViewModel : INotifyPropertyChanged
{
    private readonly IReviewRepository _reviewRepository;
    private readonly IAudioPlaybackController _playbackController;

    private ReviewQueueItem? _selectedItem;
    private string _reviewerNotes = string.Empty;
    private bool _addToProfileOnConfirm = true;
    private string? _statusMessage;
    private bool _isProcessing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ReviewQueueItem> PendingQueue { get; } = [];
    public ObservableCollection<ReviewDecisionRecord> DecisionHistory { get; } = [];

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
            }
        }
    }

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
            PendingQueue.Add(new ReviewQueueItem(
                Segment: seg,
                FileName: fileName,
                ProfileName: profileName,
                DetectedAtUtc: DateTimeOffset.UtcNow,
                ProfilePath: profilePath));
        }

        if (SelectedItem == null && PendingQueue.Count > 0)
        {
            SelectedItem = PendingQueue[0];
        }
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

            PendingQueue.Remove(item);
            SelectedItem = PendingQueue.FirstOrDefault();
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
            PendingQueue.Remove(item);
            SelectedItem = PendingQueue.FirstOrDefault();
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
