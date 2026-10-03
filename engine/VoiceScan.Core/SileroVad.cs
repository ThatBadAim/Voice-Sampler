namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Alias and wrapper maintaining backward compatibility while routing to WebRtcVad.
/// </summary>
public sealed class SileroVad : IDisposable
{
    private readonly WebRtcVad _vad = new();

    public static string SettingsFingerprint => WebRtcVad.SettingsFingerprint;

    public SileroVad(string? modelPath = null, int deviceId = 0)
    {
    }

    public SileroVad(object? session)
    {
    }

    public IReadOnlyList<SpeechInterval> DetectSpeechIntervals(float[] audio, float threshold = 0.5f) =>
        _vad.DetectSpeechIntervals(audio, threshold);

    public WebRtcVad.ProbabilityStream StartProbabilityStream() => _vad.StartProbabilityStream();

    public static IReadOnlyList<SpeechInterval> ProbabilitiesToIntervals(
        float[] probabilities,
        double totalSeconds,
        float threshold) =>
        WebRtcVad.ProbabilitiesToIntervals(probabilities, totalSeconds, threshold);

    public void Dispose() => _vad.Dispose();
}
