using System.Diagnostics;

namespace VoiceScan.App.Core.Services;

public interface IAudioPlaybackController : IDisposable
{
    event EventHandler<double>? PositionChanged;
    event EventHandler<bool>? PlayStateChanged;

    bool IsPlaying { get; }
    bool IsAudioAvailable { get; }
    double CurrentPositionSeconds { get; }
    double TotalDurationSeconds { get; }
    string? CurrentFilePath { get; }

    void LoadFile(string filePath, double durationSeconds);
    void Play();
    void Pause();
    void TogglePlayPause();
    void SeekTo(double positionSeconds);
    void PlaySegment(double startTimeSeconds, double endTimeSeconds);
}

public sealed class AudioPlaybackController : IAudioPlaybackController
{
    /// <summary>
    /// ffplay stops by itself at the end of a segment (-t) or file (-autoexit). The wall clock runs ahead of the audio
    /// by ffplay's start-up and seek time, so the clock alone must not end playback; this margin only stops a player
    /// that never exits.
    /// </summary>
    private const double StuckPlayerGraceSeconds = 10.0;

    private readonly IAudioOutput _output;
    private readonly System.Timers.Timer _playbackTimer;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();

    private IAudioOutputSession? _session;
    private double _positionAtStart;
    private double _totalDurationSeconds;
    private double? _stopAtPositionSeconds;
    private bool _isPlaying;
    private string? _currentFilePath;

    public event EventHandler<double>? PositionChanged;
    public event EventHandler<bool>? PlayStateChanged;

    public bool IsPlaying => _isPlaying;
    public bool IsAudioAvailable => _output.IsAvailable;
    public double CurrentPositionSeconds => _isPlaying ? ClampedLivePosition() : _positionAtStart;
    public double TotalDurationSeconds => _totalDurationSeconds;
    public string? CurrentFilePath => _currentFilePath;

    public AudioPlaybackController() : this(new FfplayAudioOutput()) { }

    public AudioPlaybackController(IAudioOutput output)
    {
        _output = output;
        // 50ms tick interval for 20fps smooth cursor movement
        _playbackTimer = new System.Timers.Timer(50);
        _playbackTimer.Elapsed += OnTimerElapsed;
        _playbackTimer.AutoReset = true;
    }

    public void LoadFile(string filePath, double durationSeconds)
    {
        Pause();
        _currentFilePath = filePath;
        _totalDurationSeconds = durationSeconds;
        _positionAtStart = 0.0;
        _stopAtPositionSeconds = null;
        PositionChanged?.Invoke(this, _positionAtStart);
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_isPlaying || _totalDurationSeconds <= 0.0 || _currentFilePath is null || !_output.IsAvailable) return;
            StartSession();
        }
        PlayStateChanged?.Invoke(this, true);
    }

    public void Pause()
    {
        double position;
        lock (_gate)
        {
            if (!_isPlaying) return;
            position = ClampedLivePosition();
            StopSession();
            _positionAtStart = position;
        }
        PlayStateChanged?.Invoke(this, false);
    }

    public void TogglePlayPause()
    {
        if (_isPlaying) Pause();
        else Play();
    }

    public void SeekTo(double positionSeconds)
    {
        bool wasPlaying;
        lock (_gate)
        {
            wasPlaying = _isPlaying;
            if (wasPlaying) StopSession();
            _positionAtStart = Math.Clamp(positionSeconds, 0.0, _totalDurationSeconds);
            _stopAtPositionSeconds = null;
            if (wasPlaying) StartSession();
        }
        PositionChanged?.Invoke(this, _positionAtStart);
    }

    public void PlaySegment(double startTimeSeconds, double endTimeSeconds)
    {
        Pause();
        SeekTo(startTimeSeconds);
        _stopAtPositionSeconds = endTimeSeconds;
        Play();
    }

    private void StartSession()
    {
        double? length = _stopAtPositionSeconds.HasValue ? _stopAtPositionSeconds.Value - _positionAtStart : null;
        _session = _output.Start(_currentFilePath!, _positionAtStart, length);
        _clock.Restart();
        _isPlaying = true;
        _playbackTimer.Start();
    }

    private void StopSession()
    {
        _playbackTimer.Stop();
        _clock.Stop();
        _session?.Dispose();
        _session = null;
        _isPlaying = false;
    }

    private double ClampedLivePosition()
    {
        double live = _positionAtStart + _clock.Elapsed.TotalSeconds;
        double limit = _stopAtPositionSeconds ?? _totalDurationSeconds;
        return Math.Min(live, Math.Min(limit, _totalDurationSeconds));
    }

    private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        double position;
        bool finished;
        lock (_gate)
        {
            if (!_isPlaying) return;
            position = ClampedLivePosition();
            double limit = _stopAtPositionSeconds ?? _totalDurationSeconds;
            double elapsedPastLimit = _positionAtStart + _clock.Elapsed.TotalSeconds - limit;
            finished = (_session?.HasExited ?? true) || elapsedPastLimit > StuckPlayerGraceSeconds;
            if (finished)
            {
                position = Math.Min(position, limit);
                StopSession();
                _positionAtStart = position;
                _stopAtPositionSeconds = null;
            }
        }

        PositionChanged?.Invoke(this, position);
        if (finished) PlayStateChanged?.Invoke(this, false);
    }

    public void Dispose()
    {
        lock (_gate) StopSession();
        _playbackTimer.Dispose();
    }
}
