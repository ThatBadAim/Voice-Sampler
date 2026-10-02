namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

public record SpeechAudioWindow(
    double StartTimeSeconds,
    double EndTimeSeconds,
    float[] AudioSamples);

/// <summary>
/// Partitions active speech intervals into fixed-duration sliding windows with configurable hop.
/// </summary>
public static class SpeechWindowExtractor
{
    public const int DefaultSampleRate = 16000;
    public const double DefaultWindowDurationSeconds = 2.0;
    public const double DefaultHopDurationSeconds = 1.0;

    public static IReadOnlyList<SpeechAudioWindow> ExtractWindows(
        float[] audio,
        IReadOnlyList<SpeechInterval> intervals,
        int sampleRate = DefaultSampleRate,
        double windowSec = DefaultWindowDurationSeconds,
        double hopSec = DefaultHopDurationSeconds)
    {
        int winSamples = (int)(windowSec * sampleRate);
        int hopSamples = (int)(hopSec * sampleRate);
        var windows = new List<SpeechAudioWindow>();

        foreach (var interval in intervals)
        {
            int startSamp = Math.Max(0, (int)(interval.StartTimeSeconds * sampleRate));
            int endSamp = Math.Min(audio.Length, (int)(interval.EndTimeSeconds * sampleRate));
            int segLen = endSamp - startSamp;

            if (segLen <= 0)
            {
                continue;
            }

            if (segLen < winSamples)
            {
                // Pad segment shorter than window duration with silence to full window length
                float[] padded = new float[winSamples];
                Array.Copy(audio, startSamp, padded, 0, segLen);
                windows.Add(new SpeechAudioWindow(
                    interval.StartTimeSeconds,
                    interval.StartTimeSeconds + windowSec,
                    padded));
            }
            else
            {
                for (int w = 0; w <= segLen - winSamples; w += hopSamples)
                {
                    float[] winAudio = new float[winSamples];
                    Array.Copy(audio, startSamp + w, winAudio, 0, winSamples);

                    double tStart = interval.StartTimeSeconds + ((double)w / sampleRate);
                    double tEnd = tStart + windowSec;

                    windows.Add(new SpeechAudioWindow(tStart, tEnd, winAudio));
                }
            }
        }

        return windows;
    }
}
