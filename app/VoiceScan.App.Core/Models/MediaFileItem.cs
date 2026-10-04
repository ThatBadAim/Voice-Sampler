using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoiceScan.App.Core.Models;

/// <summary>A file the user added to a part of the app, with an optional on-demand waveform preview.</summary>
public class MediaFileItem(string path) : INotifyPropertyChanged
{
    private WaveformEnvelope? _waveform;
    private bool _isWaveformVisible;
    private bool _isLoadingWaveform;
    private string? _waveformError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);

    public WaveformEnvelope? Waveform
    {
        get => _waveform;
        set => SetField(ref _waveform, value);
    }

    public bool IsWaveformVisible
    {
        get => _isWaveformVisible;
        set => SetField(ref _isWaveformVisible, value);
    }

    public bool IsLoadingWaveform
    {
        get => _isLoadingWaveform;
        set => SetField(ref _isLoadingWaveform, value);
    }

    public string? WaveformError
    {
        get => _waveformError;
        set => SetField(ref _waveformError, value);
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>An enrollment voice sample together with its quality check outcome.</summary>
public sealed class EnrollmentSampleItem(string path) : MediaFileItem(path)
{
    private AudioQualityReport? _report;
    private bool _isAnalyzing;
    private string? _analysisError;

    public AudioQualityReport? Report
    {
        get => _report;
        set
        {
            if (SetField(ref _report, value)) Raise(nameof(IsAccepted));
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set => SetField(ref _isAnalyzing, value);
    }

    public string? AnalysisError
    {
        get => _analysisError;
        set => SetField(ref _analysisError, value);
    }

    public bool IsAccepted => _report?.IsAcceptableForEnrollment == true;
}
