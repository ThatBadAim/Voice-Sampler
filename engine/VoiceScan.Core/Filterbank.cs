namespace VoiceScan.Core;

using System;

/// <summary>
/// Computes 80-dimensional log-mel filterbank features with Cepstral Mean Normalization (CMN).
/// Matches the Python research implementation exactly for model compatibility.
/// </summary>
public static class Filterbank
{
    private const int SampleRate = 16000;
    private const int FrameLengthSamples = 400; // 25ms @ 16kHz
    private const int FrameStepSamples = 160;   // 10ms @ 16kHz
    private const int Nfft = 512;
    private const int NumMels = 80;
    private const float PreEmphasis = 0.97f;

    private static readonly float[] HammingWindow = CreateHammingWindow(FrameLengthSamples);
    private static readonly float[][] MelFilterbankMatrix = CreateMelFilterbank(SampleRate, Nfft, NumMels);

    public static float[,] ComputeFbank(float[] audio)
    {
        if (audio.Length == 0)
        {
            return new float[0, NumMels];
        }

        // Peak normalization if needed
        float maxVal = 0f;
        for (int i = 0; i < audio.Length; i++)
        {
            float abs = MathF.Abs(audio[i]);
            if (abs > maxVal) maxVal = abs;
        }

        float scale = maxVal > 1.0f ? 1.0f / maxVal : 1.0f;

        // Pre-emphasis: y[t] = x[t] - 0.97 * x[t-1]
        float[] pre = new float[audio.Length];
        pre[0] = audio[0] * scale;
        for (int i = 1; i < audio.Length; i++)
        {
            pre[i] = (audio[i] * scale) - (PreEmphasis * audio[i - 1] * scale);
        }

        // Pad if shorter than one frame
        if (pre.Length < FrameLengthSamples)
        {
            float[] padded = new float[FrameLengthSamples];
            Array.Copy(pre, padded, pre.Length);
            pre = padded;
        }

        int numFrames = 1 + (pre.Length - FrameLengthSamples) / FrameStepSamples;
        float[,] logMels = new float[numFrames, NumMels];

        float[] fftReal = new float[Nfft];
        float[] fftImag = new float[Nfft];
        float[] powerSpectrum = new float[(Nfft / 2) + 1];

        // Process each frame
        for (int frameIdx = 0; frameIdx < numFrames; frameIdx++)
        {
            int startSample = frameIdx * FrameStepSamples;

            // Windowing
            for (int i = 0; i < FrameLengthSamples; i++)
            {
                fftReal[i] = pre[startSample + i] * HammingWindow[i];
                fftImag[i] = 0f;
            }
            for (int i = FrameLengthSamples; i < Nfft; i++)
            {
                fftReal[i] = 0f;
                fftImag[i] = 0f;
            }

            // In-place Radix-2 Cooley-Tukey FFT
            ComputeFft(fftReal, fftImag);

            // Power spectrum: |X[k]|^2 / Nfft
            for (int k = 0; k <= Nfft / 2; k++)
            {
                float re = fftReal[k];
                float im = fftImag[k];
                powerSpectrum[k] = ((re * re) + (im * im)) / Nfft;
            }

            // Mel Filterbank dot product
            for (int m = 0; m < NumMels; m++)
            {
                float energy = 0f;
                float[] melFilter = MelFilterbankMatrix[m];
                for (int k = 0; k <= Nfft / 2; k++)
                {
                    energy += powerSpectrum[k] * melFilter[k];
                }

                if (energy < 1e-12f)
                {
                    energy = 1e-12f;
                }

                logMels[frameIdx, m] = MathF.Log(energy);
            }
        }

        // Cepstral Mean Normalization (CMN): subtract mean across time frames for each channel
        for (int m = 0; m < NumMels; m++)
        {
            float sum = 0f;
            for (int f = 0; f < numFrames; f++)
            {
                sum += logMels[f, m];
            }
            float mean = sum / numFrames;
            for (int f = 0; f < numFrames; f++)
            {
                logMels[f, m] -= mean;
            }
        }

        return logMels;
    }

    private static float[] CreateHammingWindow(int length)
    {
        float[] window = new float[length];
        for (int i = 0; i < length; i++)
        {
            // numpy.hamming(M): 0.54 - 0.46 * cos(2 * pi * n / (M - 1))
            window[i] = 0.54f - (0.46f * MathF.Cos((2f * MathF.PI * i) / (length - 1)));
        }
        return window;
    }

    private static float[][] CreateMelFilterbank(int sampleRate, int nfft, int numMels)
    {
        int numBins = (nfft / 2) + 1;
        float lowMel = 0f;
        float highMel = 2595f * MathF.Log10(1f + ((sampleRate / 2f) / 700f));

        float[] melPoints = new float[numMels + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = lowMel + (i * (highMel - lowMel) / (numMels + 1));
        }

        int[] binPoints = new int[numMels + 2];
        for (int i = 0; i < binPoints.Length; i++)
        {
            float hz = 700f * (MathF.Pow(10f, melPoints[i] / 2595f) - 1f);
            binPoints[i] = (int)MathF.Floor((nfft + 1) * hz / sampleRate);
        }

        float[][] filters = new float[numMels][];
        for (int m = 1; m <= numMels; m++)
        {
            filters[m - 1] = new float[numBins];
            int left = binPoints[m - 1];
            int center = binPoints[m];
            int right = binPoints[m + 1];

            for (int k = left; k < center; k++)
            {
                if (center != left)
                {
                    filters[m - 1][k] = (float)(k - left) / (center - left);
                }
            }

            for (int k = center; k < right; k++)
            {
                if (right != center)
                {
                    filters[m - 1][k] = (float)(right - k) / (right - center);
                }
            }
        }

        return filters;
    }

    private static void ComputeFft(float[] real, float[] imag)
    {
        int n = real.Length;
        // Bit reversal permutation
        int j = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int k = n >> 1;
            while (k <= j)
            {
                j -= k;
                k >>= 1;
            }
            j += k;
        }

        // Cooley-Tukey computation
        for (int len = 2; len <= n; len <<= 1)
        {
            float angle = -2f * MathF.PI / len;
            float wlenReal = MathF.Cos(angle);
            float wlenImag = MathF.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                float wReal = 1f;
                float wImag = 0f;

                for (int m = 0; m < len / 2; m++)
                {
                    int u = i + m;
                    int v = i + m + (len / 2);

                    float vReal = (real[v] * wReal) - (imag[v] * wImag);
                    float vImag = (real[v] * wImag) + (imag[v] * wReal);

                    real[v] = real[u] - vReal;
                    imag[v] = imag[u] - vImag;
                    real[u] += vReal;
                    imag[u] += vImag;

                    float nextWReal = (wReal * wlenReal) - (wImag * wlenImag);
                    float nextWImag = (wReal * wlenImag) + (wImag * wlenReal);
                    wReal = nextWReal;
                    wImag = nextWImag;
                }
            }
        }
    }
}
