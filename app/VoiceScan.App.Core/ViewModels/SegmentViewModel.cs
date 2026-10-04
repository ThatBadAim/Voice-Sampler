using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceScan.App.Core.Models;

namespace VoiceScan.App.Core.ViewModels;

public enum SegmentFilterOption
{
    AllSegments,
    FlaggedOnly
}

/// <summary>
/// View model wrapper for an individual hit segment, exposing timing, speaker diarization,
/// transcription, and content moderation status.
/// </summary>
public sealed class HitSegmentViewModel : INotifyPropertyChanged
{
    private HitSegmentResult _segment;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HitSegmentResult Segment
    {
        get => _segment;
        set
        {
            if (_segment != value)
            {
                _segment = value;
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public string SegmentId => _segment.SegmentId;
    public string FilePath => _segment.FilePath;
    public double Start => _segment.Start;
    public double End => _segment.End;
    public double StartTimeSeconds => _segment.StartTimeSeconds;
    public double EndTimeSeconds => _segment.EndTimeSeconds;
    public double DurationSeconds => _segment.DurationSeconds;
    public string Verdict => _segment.Verdict;
    public double Confidence => _segment.Confidence;
    public IReadOnlyList<string> ReasonFlags => _segment.ReasonFlags;
    public ReviewDecision Decision => _segment.Decision;
    public float[]? SegmentEmbedding => _segment.SegmentEmbedding;
    public string? FileHash => _segment.FileHash;

    public string? SpeakerLabel => _segment.SpeakerLabel;
    public string? DisplaySpeakerLabel => _segment.DisplaySpeakerLabel;
    public string? Transcript => _segment.Transcript;
    public bool IsOffensive => _segment.IsOffensive;
    public bool IsFlagged => _segment.IsFlagged;
    public IReadOnlyList<string> ModerationViolations => _segment.ModerationViolations;
    public string ViolationsSummary => _segment.ViolationsSummary;

    public HitSegmentViewModel(HitSegmentResult segment)
    {
        _segment = segment;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
