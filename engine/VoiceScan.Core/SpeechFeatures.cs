namespace VoiceScan.Core;

using System;

/// <summary>The feature front-end an embedding model was trained with.</summary>
public enum FeatureFrontEnd
{
    /// <summary>SpeechBrain <c>Fbank(n_mels=80)</c> + sentence mean normalization (spkrec-ecapa-voxceleb).</summary>
    SpeechBrainFbank,

    /// <summary>NeMo <c>AudioToMelSpectrogramPreprocessor</c> with the TitaNet settings (per-feature normalization).</summary>
    NemoMelSpectrogram
}

/// <summary>Features for one window: <c>Frames[t, mel]</c>, of which the first <c>ValidFrames</c> carry audio.</summary>
public readonly record struct FeatureMatrix(float[,] Frames, int ValidFrames);

/// <summary>
/// 80-dim log-mel features computed exactly as the reference toolkits do for the shipped models, so the ONNX
/// encoders see the input they were trained on. Verified against the SpeechBrain and NeMo implementations in the unit tests.
/// Input is 16 kHz mono float PCM in [-1, 1].
/// </summary>
public static class SpeechFeatures
{
    public const int NumMels = 80;
    private const int SampleRate = 16000;
    private const int HopSamples = 160;

    public static string Fingerprint(FeatureFrontEnd frontEnd) => frontEnd switch
    {
        FeatureFrontEnd.SpeechBrainFbank => "sb-fbank80-v1",
        FeatureFrontEnd.NemoMelSpectrogram => "nemo-mel80-perfeat-v1",
        _ => throw new ArgumentOutOfRangeException(nameof(frontEnd))
    };

    public static FeatureMatrix Compute(float[] audio, FeatureFrontEnd frontEnd) => frontEnd switch
    {
        FeatureFrontEnd.SpeechBrainFbank => SpeechBrain.Compute(audio),
        FeatureFrontEnd.NemoMelSpectrogram => Nemo.Compute(audio),
        _ => throw new ArgumentOutOfRangeException(nameof(frontEnd))
    };

    /// <summary>
    /// SpeechBrain STFT (n_fft = win = 400, hop 160, periodic Hamming, centered with zero padding), power spectrum,
    /// triangular mel filters on the HTK scale (0-8000 Hz), 10*log10 with amin 1e-10 and an 80 dB floor below the
    /// utterance maximum, then per-mel mean subtraction over time.
    /// </summary>
    private static class SpeechBrain
    {
        private const int NFft = 400;
        private const double AminPower = 1e-10;
        private const double TopDb = 80.0;

        private static readonly double[] Window = CreateWindow();
        private static readonly double[][] MelWeights = CreateMelWeights();
        private static readonly RealSpectrum Spectrum = new(NFft);

        public static FeatureMatrix Compute(float[] audio)
        {
            int frames = 1 + audio.Length / HopSamples;
            var db = new float[frames, NumMels];
            double[] frame = new double[NFft];
            double[] power = new double[NFft / 2 + 1];
            double maxDb = double.NegativeInfinity;

            for (int f = 0; f < frames; f++)
            {
                int start = f * HopSamples - NFft / 2;
                for (int i = 0; i < NFft; i++)
                {
                    int idx = start + i;
                    frame[i] = idx >= 0 && idx < audio.Length ? audio[idx] * Window[i] : 0.0;
                }

                Spectrum.Power(frame, power);
                for (int m = 0; m < NumMels; m++)
                {
                    double[] w = MelWeights[m];
                    double energy = 0.0;
                    for (int k = 0; k < w.Length; k++) energy += power[k] * w[k];
                    double value = 10.0 * Math.Log10(Math.Max(energy, AminPower));
                    db[f, m] = (float)value;
                    if (value > maxDb) maxDb = value;
                }
            }

            float floor = (float)(maxDb - TopDb);
            for (int m = 0; m < NumMels; m++)
            {
                double sum = 0.0;
                for (int f = 0; f < frames; f++)
                {
                    if (db[f, m] < floor) db[f, m] = floor;
                    sum += db[f, m];
                }
                float mean = (float)(sum / frames);
                for (int f = 0; f < frames; f++) db[f, m] -= mean;
            }

            return new FeatureMatrix(db, frames);
        }

        private static double[] CreateWindow()
        {
            var w = new double[NFft];
            for (int i = 0; i < NFft; i++) w[i] = 0.54 - 0.46 * Math.Cos(2.0 * Math.PI * i / NFft);
            return w;
        }

        private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
        private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

        // Symmetric triangles centred on each mel point with half-width equal to the gap to the previous point.
        private static double[][] CreateMelWeights()
        {
            int bins = NFft / 2 + 1;
            double lowMel = HzToMel(0.0), highMel = HzToMel(SampleRate / 2.0);
            var hz = new double[NumMels + 2];
            for (int i = 0; i < hz.Length; i++) hz[i] = MelToHz(lowMel + (highMel - lowMel) * i / (NumMels + 1));

            var weights = new double[NumMels][];
            for (int m = 0; m < NumMels; m++)
            {
                double centre = hz[m + 1];
                double band = hz[m + 1] - hz[m];
                weights[m] = new double[bins];
                for (int k = 0; k < bins; k++)
                {
                    double freq = (SampleRate / 2.0) * k / (bins - 1);
                    double slope = (freq - centre) / band;
                    weights[m][k] = Math.Max(0.0, Math.Min(slope + 1.0, 1.0 - slope));
                }
            }
            return weights;
        }
    }

    /// <summary>
    /// NeMo FilterbankFeatures with the TitaNet config: 0.97 pre-emphasis over the whole signal, centered STFT
    /// (n_fft 512, symmetric Hann of 400 samples, hop 160), power spectrum, librosa Slaney mel filters (0-8000 Hz),
    /// ln(x + 2^-24), then per-mel mean / unbiased std (+1e-5) over the valid frames; frames past the valid length are zero.
    /// </summary>
    private static class Nemo
    {
        private const int NFft = 512;
        private const int WinLength = 400;
        private const double PreEmphasis = 0.97;
        private const double LogGuard = 1.0 / (1 << 24);
        private const double StdGuard = 1e-5;

        // Slaney mel scale constants; declared before MelWeights because static initializers run in order.
        private const double SlaneyLinearStep = 200.0 / 3.0;
        private const double SlaneyMinLogHz = 1000.0;
        private const double SlaneyMinLogMel = SlaneyMinLogHz / SlaneyLinearStep;
        private static readonly double SlaneyLogStep = Math.Log(6.4) / 27.0;

        private static readonly double[] Window = CreateWindow();
        private static readonly double[][] MelWeights = CreateMelWeights();
        private static readonly RealSpectrum Spectrum = new(NFft);

        public static FeatureMatrix Compute(float[] audio)
        {
            int frames = 1 + audio.Length / HopSamples;
            int valid = audio.Length / HopSamples;

            double[] emphasized = new double[audio.Length];
            for (int i = 0; i < audio.Length; i++)
            {
                emphasized[i] = i == 0 ? audio[0] : audio[i] - PreEmphasis * audio[i - 1];
            }

            var logMel = new float[frames, NumMels];
            double[] frame = new double[NFft];
            double[] power = new double[NFft / 2 + 1];
            int windowOffset = (NFft - WinLength) / 2;

            for (int f = 0; f < frames; f++)
            {
                int start = f * HopSamples - NFft / 2;
                Array.Clear(frame);
                for (int i = 0; i < WinLength; i++)
                {
                    int idx = start + windowOffset + i;
                    if (idx >= 0 && idx < emphasized.Length) frame[windowOffset + i] = emphasized[idx] * Window[i];
                }

                Spectrum.Power(frame, power);
                for (int m = 0; m < NumMels; m++)
                {
                    double[] w = MelWeights[m];
                    double energy = 0.0;
                    for (int k = 0; k < w.Length; k++) energy += power[k] * w[k];
                    logMel[f, m] = (float)Math.Log(energy + LogGuard);
                }
            }

            for (int m = 0; m < NumMels; m++)
            {
                double sum = 0.0;
                for (int f = 0; f < valid; f++) sum += logMel[f, m];
                double mean = valid > 0 ? sum / valid : 0.0;

                double sq = 0.0;
                for (int f = 0; f < valid; f++)
                {
                    double d = logMel[f, m] - mean;
                    sq += d * d;
                }
                double std = (valid > 1 ? Math.Sqrt(sq / (valid - 1)) : 0.0) + StdGuard;

                for (int f = 0; f < frames; f++)
                {
                    logMel[f, m] = f < valid ? (float)((logMel[f, m] - mean) / std) : 0f;
                }
            }

            return new FeatureMatrix(logMel, valid);
        }

        private static double[] CreateWindow()
        {
            var w = new double[WinLength];
            for (int i = 0; i < WinLength; i++) w[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (WinLength - 1));
            return w;
        }

        private static double HzToMel(double hz) =>
            hz < SlaneyMinLogHz ? hz / SlaneyLinearStep : SlaneyMinLogMel + Math.Log(hz / SlaneyMinLogHz) / SlaneyLogStep;

        private static double MelToHz(double mel) =>
            mel < SlaneyMinLogMel ? mel * SlaneyLinearStep : SlaneyMinLogHz * Math.Exp(SlaneyLogStep * (mel - SlaneyMinLogMel));

        // librosa.filters.mel(sr=16000, n_fft=512, n_mels=80, fmin=0, fmax=8000, htk=False, norm="slaney")
        private static double[][] CreateMelWeights()
        {
            int bins = NFft / 2 + 1;
            double lowMel = HzToMel(0.0), highMel = HzToMel(SampleRate / 2.0);
            var melHz = new double[NumMels + 2];
            for (int i = 0; i < melHz.Length; i++) melHz[i] = MelToHz(lowMel + (highMel - lowMel) * i / (NumMels + 1));

            var weights = new double[NumMels][];
            for (int m = 0; m < NumMels; m++)
            {
                double lowerWidth = melHz[m + 1] - melHz[m];
                double upperWidth = melHz[m + 2] - melHz[m + 1];
                double areaNorm = 2.0 / (melHz[m + 2] - melHz[m]);
                weights[m] = new double[bins];
                for (int k = 0; k < bins; k++)
                {
                    double freq = (double)k * SampleRate / NFft;
                    double lower = (freq - melHz[m]) / lowerWidth;
                    double upper = (melHz[m + 2] - freq) / upperWidth;
                    weights[m][k] = Math.Max(0.0, Math.Min(lower, upper)) * areaNorm;
                }
            }
            return weights;
        }
    }

    /// <summary>
    /// Power spectrum |DFT(x)|^2 for bins 0..n/2 of a real frame of length n. Powers of two use a radix-2 FFT;
    /// other lengths use Bluestein's chirp-z transform on a power-of-two FFT. Thread-safe: tables are read-only
    /// and every call allocates its own work buffers.
    /// </summary>
    private sealed class RealSpectrum
    {
        private readonly int _n;
        private readonly Fft _fft;
        private readonly double[]? _chirpRe, _chirpIm;     // w[k] = exp(-i*pi*k^2/n)
        private readonly double[]? _kernelRe, _kernelIm;   // FFT of the conjugate chirp, wrapped

        public RealSpectrum(int n)
        {
            _n = n;
            if ((n & (n - 1)) == 0)
            {
                _fft = new Fft(n);
                return;
            }

            int m = 1;
            while (m < 2 * n - 1) m <<= 1;
            _fft = new Fft(m);

            _chirpRe = new double[n];
            _chirpIm = new double[n];
            for (int k = 0; k < n; k++)
            {
                // k^2 mod 2n keeps the angle small so the chirp stays accurate for long frames.
                double angle = Math.PI * ((long)k * k % (2L * n)) / n;
                _chirpRe[k] = Math.Cos(angle);
                _chirpIm[k] = -Math.Sin(angle);
            }

            _kernelRe = new double[m];
            _kernelIm = new double[m];
            for (int k = 0; k < n; k++)
            {
                _kernelRe[k] = _chirpRe[k];
                _kernelIm[k] = -_chirpIm[k];
                if (k > 0)
                {
                    _kernelRe[m - k] = _chirpRe[k];
                    _kernelIm[m - k] = -_chirpIm[k];
                }
            }
            _fft.Transform(_kernelRe, _kernelIm, inverse: false);
        }

        public void Power(double[] frame, double[] power)
        {
            int size = _fft.Size;
            var re = new double[size];
            var im = new double[size];

            if (_chirpRe is null)
            {
                Array.Copy(frame, re, _n);
                _fft.Transform(re, im, inverse: false);
                for (int k = 0; k < power.Length; k++) power[k] = re[k] * re[k] + im[k] * im[k];
                return;
            }

            for (int k = 0; k < _n; k++)
            {
                re[k] = frame[k] * _chirpRe[k];
                im[k] = frame[k] * _chirpIm![k];
            }
            _fft.Transform(re, im, inverse: false);
            for (int k = 0; k < size; k++)
            {
                double r = re[k] * _kernelRe![k] - im[k] * _kernelIm![k];
                double i = re[k] * _kernelIm[k] + im[k] * _kernelRe[k];
                re[k] = r;
                im[k] = i;
            }
            _fft.Transform(re, im, inverse: true);

            // |w[k] * c[k]| = |c[k]| / size (the inverse transform is unscaled), as |w[k]| = 1.
            double scale = 1.0 / ((double)size * size);
            for (int k = 0; k < power.Length; k++) power[k] = (re[k] * re[k] + im[k] * im[k]) * scale;
        }
    }

    /// <summary>In-place iterative radix-2 complex FFT (unscaled in both directions).</summary>
    private sealed class Fft
    {
        private readonly double[] _cos;
        private readonly double[] _sin;

        public Fft(int size)
        {
            Size = size;
            _cos = new double[size / 2];
            _sin = new double[size / 2];
            for (int i = 0; i < size / 2; i++)
            {
                _cos[i] = Math.Cos(2.0 * Math.PI * i / size);
                _sin[i] = -Math.Sin(2.0 * Math.PI * i / size);
            }
        }

        public int Size { get; }

        public void Transform(double[] real, double[] imag, bool inverse)
        {
            int n = Size;
            for (int i = 0, j = 0; i < n - 1; i++)
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

            double sign = inverse ? -1.0 : 1.0;
            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len / 2;
                int stride = n / len;
                for (int i = 0; i < n; i += len)
                {
                    for (int m = 0; m < half; m++)
                    {
                        double wr = _cos[m * stride];
                        double wi = sign * _sin[m * stride];
                        int u = i + m;
                        int v = u + half;
                        double vr = real[v] * wr - imag[v] * wi;
                        double vi = real[v] * wi + imag[v] * wr;
                        real[v] = real[u] - vr;
                        imag[v] = imag[u] - vi;
                        real[u] += vr;
                        imag[u] += vi;
                    }
                }
            }
        }
    }
}
