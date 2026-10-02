using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;

namespace VoiceScan.App.Core.ViewModels;

public sealed class ReviewViewModel : INotifyPropertyChanged
{
    private readonly IReviewRepository _reviewRepository;
    private readonly IAudioPlaybackController _playbackController;

    private ReviewQueueItem? _selectedItem;
    private string _reviewerNotes = string.Empty;
    private string? _activeProfilePath;
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
                    _playbackController.LoadFile(value.Segment.FilePath, value.Segment.DurationSeconds);
                }
                ReviewerNotes = string.Empty;
                OnPropertyChanged(nameof(HasSelectedItem));
            }
        }
    }

    public string ReviewerNotes
    {
        get => _reviewerNotes;
        set => SetField(ref _reviewerNotes, value);
    }

    public string? ActiveProfilePath
    {
        get => _activeProfilePath;
        set => SetField(ref _activeProfilePath, value);
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

    public void EnqueueSegments(IEnumerable<HitSegmentResult> segments, string profileName, string fileName)
    {
        foreach (var seg in segments)
        {
            PendingQueue.Add(new ReviewQueueItem(
                Segment: seg,
                FileName: fileName,
                ProfileName: profileName,
                DetectedAtUtc: DateTimeOffset.UtcNow));
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
        StatusMessage = "Recording confirmation in SQLite...";

        var item = _selectedItem;
        var decisionRecord = new ReviewDecisionRecord(
            SegmentId: item.Segment.SegmentId,
            FilePath: item.Segment.FilePath,
            FileHash: "hash-" + Path.GetFileName(item.Segment.FilePath),
            ProfileName: item.ProfileName,
            StartTimeSeconds: item.Segment.StartTimeSeconds,
            EndTimeSeconds: item.Segment.EndTimeSeconds,
            Confidence: item.Segment.Confidence,
            OriginalVerdict: item.Segment.Verdict,
            ReasonFlags: item.Segment.ReasonFlags,
            Decision: ReviewDecision.Confirmed,
            DecidedAtUtc: DateTimeOffset.UtcNow,
            Notes: _reviewerNotes);

        try
        {
            await _reviewRepository.RecordDecisionAsync(decisionRecord, cancellationToken);

            if (_addToProfileOnConfirm && !string.IsNullOrWhiteSpace(_activeProfilePath) && File.Exists(_activeProfilePath))
            {
                StatusMessage = "Augmenting voice profile with confirmed speech segment...";
                await _reviewRepository.AugmentProfileWithConfirmedHitAsync(_activeProfilePath, decisionRecord, cancellationToken);
            }

            PendingQueue.Remove(item);
            SelectedItem = PendingQueue.FirstOrDefault();
            await RefreshHistoryAsync(cancellationToken);
            StatusMessage = $"Confirmed segment [{item.Segment.StartTimeSeconds:F1}s - {item.Segment.EndTimeSeconds:F1}s]. Stored in SQLite.";
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
        StatusMessage = "Recording rejection and saving negative cohort in SQLite...";

        var item = _selectedItem;
        var decisionRecord = new ReviewDecisionRecord(
            SegmentId: item.Segment.SegmentId,
            FilePath: item.Segment.FilePath,
            FileHash: "hash-" + Path.GetFileName(item.Segment.FilePath),
            ProfileName: item.ProfileName,
            StartTimeSeconds: item.Segment.StartTimeSeconds,
            EndTimeSeconds: item.Segment.EndTimeSeconds,
            Confidence: item.Segment.Confidence,
            OriginalVerdict: item.Segment.Verdict,
            ReasonFlags: item.Segment.ReasonFlags,
            Decision: ReviewDecision.Rejected,
            DecidedAtUtc: DateTimeOffset.UtcNow,
            Notes: _reviewerNotes);

        try
        {
            await _reviewRepository.RecordDecisionAsync(decisionRecord, cancellationToken);
            PendingQueue.Remove(item);
            SelectedItem = PendingQueue.FirstOrDefault();
            await RefreshHistoryAsync(cancellationToken);
            StatusMessage = $"Rejected segment [{item.Segment.StartTimeSeconds:F1}s - {item.Segment.EndTimeSeconds:F1}s] stored as negative.";
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
