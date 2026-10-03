namespace VoiceScan.Core;

using System;

/// <summary>Front-end variants used by the supported embedding models.</summary>
public enum FbankProfile
{
    /// <summary>WeSpeaker: int16-range samples, Hamming window.</summary>
    WeSpeaker,

    /// <summary>3D-Speaker CAM++: samples in [-1, 1], Kaldi default Povey window.</summary>
    CamPlusPlus
}

/// <summary>
/// 80-dim log-mel filterbank features with per-utterance mean normalization, following the
/// Kaldi / kaldi-native-fbank recipe the WeSpeaker and 3D-Speaker models were trained with
/// (per-frame DC removal and pre-emphasis, Hamming window, 20 Hz low edge,
/// triangular filters placed on continuous mel positions, float-epsilon floor).
/// Verified against kaldi-native-fbank in the unit tests.
/// </summary>
public static class Filterbank
{
    /// <summary>Changes whenever the feature recipe changes; part of the embedding cache key.</summary>
    public const string Fingerprint = "fbank-kaldi-v1";

    private const int SampleRate = 16000;
    private const int FrameLengthSamples = 400; // 25ms @ 16kHz
    private const int FrameStepSamples = 160;   // 10ms @ 16kHz
    private const int Nfft = 512;
    private const int NumMels = 80;
    private const double PreEmphasis = 0.97;
    private const double LowFreqHz = 20.0;
    private const double Int16Scale = 32768.0;
    private const double LogFloor = 1.1920928955078125e-7; // float epsilon, as in Kaldi

    private static readonly double[] HammingWindow = CreateWindow(povey: false);
    private static readonly double[] PoveyWindow = CreateWindow(povey: true);
    private static readonly (int First, double[] Weights)[] MelFilters = CreateMelFilterbank();
    private static readonly double[] CosTable = CreateTwiddles(Math.Cos);
    private static readonly double[] SinTable = CreateTwiddles(a => -Math.Sin(a));

    public static float[,] ComputeFbank(float[] audio, FbankProfile profile = FbankProfile.WeSpeaker)
    {
        if (audio.Length == 0)
        {
            return new float[0, NumMels];
        }

        double sampleScale = profile == FbankProfile.WeSpeaker ? Int16Scale : 1.0;
        double[] window = profile == FbankProfile.WeSpeaker ? HammingWindow : PoveyWindow;
        int length = Math.Max(audio.Length, FrameLengthSamples);
        int numFrames = 1 + (length - FrameLengthSamples) / FrameStepSamples;
        float[,] logMels = new float[numFrames, NumMels];

        double[] frame = new double[FrameLengthSamples];
        double[] fftReal = new double[Nfft];
        double[] fftImag = new double[Nfft];
        double[] power = new double[(Nfft / 2) + 1];

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * FrameStepSamples;
            double mean = 0.0;
            for (int i = 0; i < FrameLengthSamples; i++)
            {
                int idx = start + i;
                frame[i] = idx < audio.Length ? audio[idx] * sampleScale : 0.0;
                mean += frame[i];
            }
            mean /= FrameLengthSamples;

            for (int i = 0; i < FrameLengthSamples; i++)
            {
                frame[i] -= mean;
            }

            for (int i = FrameLengthSamples - 1; i > 0; i--)
            {
                frame[i] -= PreEmphasis * frame[i - 1];
            }
            frame[0] -= PreEmphasis * frame[0];

            for (int i = 0; i < FrameLengthSamples; i++)
            {
                fftReal[i] = frame[i] * window[i];
                fftImag[i] = 0.0;
            }
            Array.Clear(fftReal, FrameLengthSamples, Nfft - FrameLengthSamples);
            Array.Clear(fftImag, FrameLengthSamples, Nfft - FrameLengthSamples);

            ComputeFft(fftReal, fftImag);

            for (int k = 0; k < power.Length; k++)
            {
                power[k] = (fftReal[k] * fftReal[k]) + (fftImag[k] * fftImag[k]);
            }

            for (int m = 0; m < NumMels; m++)
            {
                var (first, weights) = MelFilters[m];
                double energy = 0.0;
                for (int k = 0; k < weights.Length; k++)
                {
                    energy += power[first + k] * weights[k];
                }

                logMels[f, m] = (float)Math.Log(Math.Max(energy, LogFloor));
            }
        }

        for (int m = 0; m < NumMels; m++)
        {
            double sum = 0.0;
            for (int f = 0; f < numFrames; f++)
            {
                sum += logMels[f, m];
            }
            float mean = (float)(sum / numFrames);
            for (int f = 0; f < numFrames; f++)
            {
                logMels[f, m] -= mean;
            }
        }

        return logMels;
    }

    private static double[] CreateWindow(bool povey)
    {
        double[] window = new double[FrameLengthSamples];
        for (int i = 0; i < FrameLengthSamples; i++)
        {
            double phase = 2.0 * Math.PI * i / (FrameLengthSamples - 1);
            window[i] = povey
                ? Math.Pow(0.5 - (0.5 * Math.Cos(phase)), 0.85)
                : 0.54 - (0.46 * Math.Cos(phase));
        }
        return window;
    }

    private static double HzToMel(double hz) => 1127.0 * Math.Log(1.0 + (hz / 700.0));

    private static (int First, double[] Weights)[] CreateMelFilterbank()
    {
        double lowMel = HzToMel(LowFreqHz);
        double highMel = HzToMel(SampleRate / 2.0);
        double delta = (highMel - lowMel) / (NumMels + 1);
        var filters = new (int First, double[] Weights)[NumMels];

        for (int m = 0; m < NumMels; m++)
        {
            double left = lowMel + (m * delta);
            double center = left + delta;
            double right = center + delta;

            int first = -1;
            int last = -1;
            var weights = new double[(Nfft / 2) + 1];
            for (int k = 0; k <= Nfft / 2; k++)
            {
                double mel = HzToMel((double)k * SampleRate / Nfft);
                if (mel <= left || mel >= right)
                {
                    continue;
                }
                weights[k] = mel <= center
                    ? (mel - left) / (center - left)
                    : (right - mel) / (right - center);
                if (first < 0) first = k;
                last = k;
            }

            filters[m] = (first, weights[first..(last + 1)]);
        }

        return filters;
    }

    private static double[] CreateTwiddles(Func<double, double> trig)
    {
        double[] table = new double[Nfft / 2];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = trig(2.0 * Math.PI * i / Nfft);
        }
        return table;
    }

    private static void ComputeFft(double[] real, double[] imag)
    {
        int n = real.Length;
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

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len / 2;
            int stride = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int m = 0; m < half; m++)
                {
                    double wReal = CosTable[m * stride];
                    double wImag = SinTable[m * stride];
                    int u = i + m;
                    int v = u + half;

                    double vReal = (real[v] * wReal) - (imag[v] * wImag);
                    double vImag = (real[v] * wImag) + (imag[v] * wReal);

                    real[v] = real[u] - vReal;
                    imag[v] = imag[u] - vImag;
                    real[u] += vReal;
                    imag[u] += vImag;
                }
            }
        }
    }
}
