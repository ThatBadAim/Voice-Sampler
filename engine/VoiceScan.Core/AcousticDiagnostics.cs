namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Provides acoustic analysis for estimating SNR, detecting audio degradation,
/// and computing diagnostic reason flags for speech segments.
/// </summary>
public static class AcousticDiagnostics
{
    /// <summary>
    /// Estimates Signal-to-Noise Ratio (SNR) in dB from 16 kHz mono float PCM samples.
    /// Uses frame RMS energy contrast between active speech peaks and ambient background floor.
    /// </summary>
    public static double EstimateSnrDb(float[] samples, int sampleRate = 16000)
    {
        if (samples == null || samples.Length == 0) return 0.0;

        int frameSize = sampleRate * 25 / 1000; // 25ms
        int hopSize = sampleRate * 10 / 1000;   // 10ms
        if (samples.Length < frameSize) return 20.0;

        int numFrames = (samples.Length - frameSize) / hopSize + 1;
        var energies = new List<double>(numFrames);

        for (int i = 0; i < numFrames; i++)
        {
            int start = i * hopSize;
            double sumSq = 0.0;
            for (int j = 0; j < frameSize; j++)
            {
                float s = samples[start + j];
                sumSq += s * s;
            }
            double frameRms = Math.Sqrt(sumSq / frameSize);
            energies.Add(frameRms);
        }

        energies.Sort();

        // Lowest 15% represents the background noise floor
        int noiseCount = Math.Max(1, (int)(energies.Count * 0.15));
        double noisePower = energies.Take(noiseCount).Average();

        // Top 30% represents active speech peaks
        int speechCount = Math.Max(1, (int)(energies.Count * 0.30));
        double speechPower = energies.Skip(energies.Count - speechCount).Average();

        if (noisePower <= 1e-6) return 30.0; // Very clean silence/background
        double ratio = Math.Max(1e-3, speechPower / noisePower);
        return 20.0 * Math.Log10(ratio);
    }

    /// <summary>
    /// Generates diagnostic reason flags for a segment.
    /// </summary>
    public static List<string> EvaluateReasonFlags(
        double durationSeconds,
        double snrDb,
        bool isCodecDegraded)
    {
        var flags = new List<string>();

        if (durationSeconds < 1.5)
        {
            flags.Add("SHORT_SEGMENT");
        }

        if (snrDb < 10.0)
        {
            flags.Add("LOW_SNR");
        }

        if (isCodecDegraded)
        {
            flags.Add("CODEC_DEGRADATION");
        }

        return flags;
    }
}
