namespace VoiceScan.Core;

using System;
using System.Collections.Generic;

/// <summary>
/// Audio augmentation algorithms for multi-condition profile enrollment:
/// - Game audio / ambient noise mixing at controlled SNR
/// - Automatic Gain Control (AGC) dynamic compression simulation
/// - Band-limiting and codec spectral smearing simulation (Opus / VoIP)
/// </summary>
public static class AudioAugmenter
{
    /// <summary>
    /// Simulates Automatic Gain Control (AGC) using an envelope follower with attack and release dynamics.
    /// </summary>
    public static float[] ApplyAgc(
        float[] audio,
        int sampleRate = 16000,
        double targetRmsDb = -18.0,
        double attackMs = 10.0,
        double releaseMs = 150.0,
        double maxGainDb = 18.0)
    {
        if (audio == null || audio.Length == 0) return Array.Empty<float>();

        double alphaAttack = Math.Exp(-1.0 / (sampleRate * attackMs / 1000.0));
        double alphaRelease = Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0));

        double targetRms = Math.Pow(10.0, targetRmsDb / 20.0);
        double maxGain = Math.Pow(10.0, maxGainDb / 20.0);

        float[] output = new float[audio.Length];
        double env = 0.0;

        for (int i = 0; i < audio.Length; i++)
        {
            double val = Math.Abs(audio[i]);
            if (val > env)
            {
                env = alphaAttack * env + (1.0 - alphaAttack) * val;
            }
            else
            {
                env = alphaRelease * env + (1.0 - alphaRelease) * val;
            }

            double gain = targetRms / (env + 1e-4);
            if (gain < 0.2) gain = 0.2;
            if (gain > maxGain) gain = maxGain;

            output[i] = (float)(audio[i] * gain);
        }

        // Soft clip if peak exceeds 0.95
        float peak = 0f;
        for (int i = 0; i < output.Length; i++)
        {
            float p = MathF.Abs(output[i]);
            if (p > peak) peak = p;
        }

        if (peak > 0.95f)
        {
            float scale = 0.95f / peak;
            for (int i = 0; i < output.Length; i++)
            {
                output[i] *= scale;
            }
        }

        return output;
    }

    /// <summary>
    /// Mixes background noise or simulated game ambience into speech audio at a target SNR (in dB).
    /// </summary>
    public static float[] ApplyNoiseMix(
        float[] speech,
        float[]? backgroundNoise = null,
        double targetSnrDb = 12.0)
    {
        if (speech == null || speech.Length == 0) return Array.Empty<float>();

        double speechPower = 0.0;
        for (int i = 0; i < speech.Length; i++)
        {
            speechPower += speech[i] * speech[i];
        }
        speechPower /= speech.Length;
        if (speechPower < 1e-12) return (float[])speech.Clone();

        double targetNoisePower = speechPower / Math.Pow(10.0, targetSnrDb / 10.0);
        float[] mixed = new float[speech.Length];

        if (backgroundNoise != null && backgroundNoise.Length > 0)
        {
            double bgPower = 0.0;
            for (int i = 0; i < backgroundNoise.Length; i++)
            {
                bgPower += backgroundNoise[i] * backgroundNoise[i];
            }
            bgPower /= backgroundNoise.Length;

            double scale = bgPower > 1e-12 ? Math.Sqrt(targetNoisePower / bgPower) : 0.01;
            for (int i = 0; i < speech.Length; i++)
            {
                float noiseSample = backgroundNoise[i % backgroundNoise.Length];
                mixed[i] = (float)(speech[i] + noiseSample * scale);
            }
        }
        else
        {
            // Deterministic synthetic ambient rumble (low-frequency hum + pink-like noise)
            var rng = new Random(42);
            double genPower = 0.0;
            double[] synthNoise = new double[speech.Length];
            double pinkFilter = 0.0;

            for (int i = 0; i < speech.Length; i++)
            {
                double white = rng.NextDouble() * 2.0 - 1.0;
                pinkFilter = 0.85 * pinkFilter + 0.15 * white;
                double hum = 0.3 * Math.Sin(2.0 * Math.PI * 120.0 * i / 16000.0);
                double n = pinkFilter + hum;
                synthNoise[i] = n;
                genPower += n * n;
            }
            genPower /= speech.Length;

            double scale = Math.Sqrt(targetNoisePower / Math.Max(1e-12, genPower));
            for (int i = 0; i < speech.Length; i++)
            {
                mixed[i] = (float)(speech[i] + synthNoise[i] * scale);
            }
        }

        // Normalize peak
        float peak = 0f;
        for (int i = 0; i < mixed.Length; i++)
        {
            float p = MathF.Abs(mixed[i]);
            if (p > peak) peak = p;
        }

        if (peak > 0.95f)
        {
            float scale = 0.95f / peak;
            for (int i = 0; i < mixed.Length; i++)
            {
                mixed[i] *= scale;
            }
        }

        return mixed;
    }

    /// <summary>
    /// Simulates bandpass filtering and low-bitrate codec degradation (Opus/telephony voice chat).
    /// </summary>
    public static float[] ApplyCodecDegradation(float[] audio, int sampleRate = 16000)
    {
        if (audio == null || audio.Length == 0) return Array.Empty<float>();

        // 1st order highpass at ~300 Hz + lowpass at ~3400 Hz (telephony/VoIP bandlimit)
        double dt = 1.0 / sampleRate;

        // Highpass at 300Hz
        double rcHigh = 1.0 / (2.0 * Math.PI * 300.0);
        double alphaHigh = rcHigh / (rcHigh + dt);

        // Lowpass at 3400Hz
        double rcLow = 1.0 / (2.0 * Math.PI * 3400.0);
        double alphaLow = dt / (rcLow + dt);

        float[] hp = new float[audio.Length];
        double prevAudio = audio[0];
        double prevHp = 0.0;
        for (int i = 0; i < audio.Length; i++)
        {
            prevHp = alphaHigh * (prevHp + audio[i] - prevAudio);
            prevAudio = audio[i];
            hp[i] = (float)prevHp;
        }

        float[] bp = new float[audio.Length];
        double prevLp = 0.0;
        for (int i = 0; i < audio.Length; i++)
        {
            prevLp = prevLp + alphaLow * (hp[i] - prevLp);
            // Simulate light 8-bit quantization / spectral smearing of low-bitrate Opus
            double quantized = Math.Round(prevLp * 64.0) / 64.0;
            bp[i] = (float)quantized;
        }

        return bp;
    }
}
