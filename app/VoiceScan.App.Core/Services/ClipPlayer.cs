using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoiceScan.App.Core.Services;

/// <summary>
/// Plays one transcribed line at a time, with a little context either side, and always stops at the end of that range.
/// Each page owns its own player, so selecting something on one page never changes what another page plays.
/// </summary>
public sealed class ClipPlayer : INotifyPropertyChanged, IDisposable
{
    public const double ContextSeconds = 1.0;

    private readonly IAudioPlaybackController _playback;
    private readonly SynchronizationContext? _uiContext;
    private bool _isPlaying;
    private long? _playingId;

    public ClipPlayer(IAudioPlaybackController playback)
    {
        _playback = playback;
        _uiContext = SynchronizationContext.Current;
        // Raised on the playback timer thread.
        _playback.PlayStateChanged += (_, playing) => OnUi(() =>
        {
            IsPlaying = playing;
            if (!playing) PlayingId = null;
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsAvailable => _playback.IsAudioAvailable;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    /// <summary>Id of the line being played, so a list can highlight it.</summary>
    public long? PlayingId
    {
        get => _playingId;
        private set => SetField(ref _playingId, value);
    }

    /// <param name="clipDurationSeconds">Length of the whole file; 0 when unknown, in which case the range end is used.</param>
    public void Play(long id, string filePath, double clipDurationSeconds, double startSeconds, double endSeconds)
    {
        double end = endSeconds + ContextSeconds;
        double duration = clipDurationSeconds > 0 ? clipDurationSeconds : end;
        if (_playback.CurrentFilePath != filePath || _playback.TotalDurationSeconds != duration)
        {
            _playback.LoadFile(filePath, duration);
        }
        _playback.PlaySegment(Math.Max(0.0, startSeconds - ContextSeconds), Math.Min(duration, end));
        PlayingId = id;
        IsPlaying = _playback.IsPlaying;
    }

    public void Stop()
    {
        _playback.Pause();
        PlayingId = null;
        IsPlaying = false;
    }

    private void OnUi(Action action)
    {
        if (_uiContext == null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose() => _playback.Dispose();
}
