namespace VoiceScan.App.Core.Services;

public interface IAudioPlaybackController : IDisposable
{
    event EventHandler<double>? PositionChanged;
    event EventHandler<bool>? PlayStateChanged;

    bool IsPlaying { get; }
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
    private readonly System.Timers.Timer _playbackTimer;
    private double _currentPositionSeconds;
    private double _totalDurationSeconds;
    private double? _stopAtPositionSeconds;
    private bool _isPlaying;
    private string? _currentFilePath;

    public event EventHandler<double>? PositionChanged;
    public event EventHandler<bool>? PlayStateChanged;

    public bool IsPlaying => _isPlaying;
    public double CurrentPositionSeconds => _currentPositionSeconds;
    public double TotalDurationSeconds => _totalDurationSeconds;
    public string? CurrentFilePath => _currentFilePath;

    public AudioPlaybackController()
    {
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
        _currentPositionSeconds = 0.0;
        _stopAtPositionSeconds = null;
        PositionChanged?.Invoke(this, _currentPositionSeconds);
    }

    public void Play()
    {
        if (_isPlaying || _totalDurationSeconds <= 0.0) return;
        _isPlaying = true;
        _playbackTimer.Start();
        PlayStateChanged?.Invoke(this, true);
    }

    public void Pause()
    {
        if (!_isPlaying) return;
        _isPlaying = false;
        _playbackTimer.Stop();
        PlayStateChanged?.Invoke(this, false);
    }

    public void TogglePlayPause()
    {
        if (_isPlaying) Pause();
        else Play();
    }

    public void SeekTo(double positionSeconds)
    {
        _currentPositionSeconds = Math.Clamp(positionSeconds, 0.0, _totalDurationSeconds);
        PositionChanged?.Invoke(this, _currentPositionSeconds);
    }

    public void PlaySegment(double startTimeSeconds, double endTimeSeconds)
    {
        SeekTo(startTimeSeconds);
        _stopAtPositionSeconds = endTimeSeconds;
        Play();
    }

    private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        _currentPositionSeconds += 0.050; // +50ms

        if (_stopAtPositionSeconds.HasValue && _currentPositionSeconds >= _stopAtPositionSeconds.Value)
        {
            _currentPositionSeconds = _stopAtPositionSeconds.Value;
            Pause();
            _stopAtPositionSeconds = null;
        }
        else if (_currentPositionSeconds >= _totalDurationSeconds)
        {
            _currentPositionSeconds = _totalDurationSeconds;
            Pause();
        }

        PositionChanged?.Invoke(this, _currentPositionSeconds);
    }

    public void Dispose()
    {
        _playbackTimer.Stop();
        _playbackTimer.Dispose();
    }
}
