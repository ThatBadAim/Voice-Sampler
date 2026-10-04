// Port of the WebRTC voice activity detector (common_audio/vad: vad_core.c, vad_filterbank.c, vad_gmm.c,
// vad_sp.c and the signal-processing helpers they use). Fixed-point arithmetic, including 16-bit wraparound,
// is reproduced exactly so decisions match the reference implementation frame for frame.
//
// Copyright (c) 2011, The WebRTC project authors. All rights reserved.
// Use of the original source is governed by the BSD 3-Clause license reproduced in THIRD-PARTY-NOTICES.md.

namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.Numerics;

public record SpeechInterval(double StartTimeSeconds, double EndTimeSeconds);

/// <summary>
/// Google WebRTC VAD: a two-Gaussian speech/noise model over six sub-band log energies with adaptive noise
/// tracking and hangover. Operates on 30 ms frames of 16 kHz audio (internally downsampled to 8 kHz).
/// Speech intervals then get minimum speech/silence durations and padding.
/// </summary>
public sealed class WebRtcVad
{
    public const int SampleRate = 16000;

    /// <summary>30 ms at 16 kHz, the longest frame WebRTC accepts (most stable decisions).</summary>
    public const int FrameSamples = 480;
    public const double FrameSeconds = (double)FrameSamples / SampleRate;

    /// <summary>Aggressiveness used unless another mode is chosen: 0 (quality) to 3 (very aggressive).</summary>
    public const int DefaultMode = 2;

    private const double MinSpeechSec = 0.25;
    private const double MinSilenceSec = 0.3;
    private const double SpeechPadSec = 0.03;

    public WebRtcVad(int mode = DefaultMode)
    {
        if (mode is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(mode), "WebRTC VAD mode must be 0-3.");
        Mode = mode;
    }

    public int Mode { get; }

    /// <summary>Identifies every setting that changes VAD output; part of the embedding cache key.</summary>
    public string SettingsFingerprint =>
        $"webrtc-vad-m{Mode}-30ms-min{MinSpeechSec}-sil{MinSilenceSec}-pad{SpeechPadSec}".Replace(',', '.');

    public IReadOnlyList<SpeechInterval> DetectSpeechIntervals(float[] audio)
    {
        if (audio.Length == 0) return Array.Empty<SpeechInterval>();

        var stream = StartProbabilityStream();
        stream.Feed(audio, audio.Length);
        return ProbabilitiesToIntervals(stream.Finish(), (double)audio.Length / SampleRate);
    }

    /// <summary>A stateful decision stream for one recording; the detector adapts to that recording's noise floor.</summary>
    public ProbabilityStream StartProbabilityStream() => new(Mode);

    public sealed class ProbabilityStream
    {
        private readonly Core _core;
        private readonly short[] _frame = new short[FrameSamples];
        private readonly List<float> _decisions = new();
        private int _filled;

        internal ProbabilityStream(int mode)
        {
            _core = new Core(mode);
        }

        public void Feed(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _frame[_filled++] = ToPcm16(samples[i]);
                if (_filled == FrameSamples)
                {
                    _decisions.Add(_core.Process16Khz(_frame));
                    _filled = 0;
                }
            }
        }

        /// <summary>Returns one 0/1 decision per 30 ms frame; a trailing partial frame is zero-padded.</summary>
        public float[] Finish()
        {
            if (_filled > 0)
            {
                Array.Clear(_frame, _filled, FrameSamples - _filled);
                _decisions.Add(_core.Process16Khz(_frame));
                _filled = 0;
            }
            return _decisions.ToArray();
        }
    }

    internal static short ToPcm16(float sample) =>
        (short)Math.Clamp(MathF.Round(sample * 32768f), short.MinValue, short.MaxValue);

    /// <summary>
    /// Turns per-frame speech decisions into intervals: a run ends after <c>MinSilenceSec</c> of non-speech,
    /// runs shorter than <c>MinSpeechSec</c> are dropped, and each interval is padded by <c>SpeechPadSec</c>.
    /// </summary>
    public static IReadOnlyList<SpeechInterval> ProbabilitiesToIntervals(
        float[] probabilities,
        double totalSeconds,
        float threshold = 0.5f,
        double frameSeconds = FrameSeconds)
    {
        float negThreshold = Math.Max(threshold - 0.15f, 0.01f);

        var raw = new List<(double Start, double End)>();
        bool triggered = false;
        double start = 0.0;
        double silenceStart = -1.0;

        for (int i = 0; i < probabilities.Length; i++)
        {
            double t = i * frameSeconds;
            float p = probabilities[i];

            if (!triggered)
            {
                if (p >= threshold)
                {
                    triggered = true;
                    start = t;
                    silenceStart = -1.0;
                }
                continue;
            }

            if (p >= threshold)
            {
                silenceStart = -1.0;
            }
            else if (p < negThreshold)
            {
                if (silenceStart < 0.0)
                {
                    silenceStart = t;
                }
                if (t + frameSeconds - silenceStart >= MinSilenceSec)
                {
                    raw.Add((start, silenceStart));
                    triggered = false;
                    silenceStart = -1.0;
                }
            }
        }

        if (triggered)
        {
            raw.Add((start, silenceStart >= 0.0 ? silenceStart : totalSeconds));
        }

        var intervals = new List<SpeechInterval>();
        foreach (var (segStart, segEnd) in raw)
        {
            if (segEnd - segStart < MinSpeechSec)
            {
                continue;
            }

            double paddedStart = Math.Max(0.0, segStart - SpeechPadSec);
            double paddedEnd = Math.Min(totalSeconds, segEnd + SpeechPadSec);
            if (intervals.Count > 0 && paddedStart <= intervals[^1].EndTimeSeconds)
            {
                intervals[^1] = new SpeechInterval(intervals[^1].StartTimeSeconds, paddedEnd);
            }
            else
            {
                intervals.Add(new SpeechInterval(paddedStart, paddedEnd));
            }
        }

        return intervals;
    }

    /// <summary>WebRTC VadInstT and the processing functions that operate on it.</summary>
    private sealed class Core
    {
        private const int NumChannels = 6;
        private const int NumGaussians = 2;
        private const int TableSize = NumChannels * NumGaussians;
        private const int MinEnergy = 10;

        private static readonly short[] SpectrumWeight = [6, 8, 10, 12, 14, 16];
        private const short NoiseUpdateConst = 655;
        private const short SpeechUpdateConst = 6554;
        private const short BackEta = 154;
        private static readonly short[] MinimumDifference = [544, 544, 576, 576, 576, 576];
        private static readonly short[] MaximumSpeech = [11392, 11392, 11520, 11520, 11520, 11520];
        private static readonly short[] MinimumMean = [640, 768];
        private static readonly short[] MaximumNoise = [9216, 9088, 8960, 8832, 8704, 8576];
        private static readonly short[] NoiseDataWeights = [34, 62, 72, 66, 53, 25, 94, 66, 56, 62, 75, 103];
        private static readonly short[] SpeechDataWeights = [48, 82, 45, 87, 50, 47, 80, 46, 83, 41, 78, 81];
        private static readonly short[] NoiseDataMeans = [6738, 4892, 7065, 6715, 6771, 3369, 7646, 3863, 7820, 7266, 5020, 4362];
        private static readonly short[] SpeechDataMeans = [8306, 10085, 10078, 11823, 11843, 6309, 9473, 9571, 10879, 7581, 8180, 7483];
        private static readonly short[] NoiseDataStds = [378, 1064, 493, 582, 688, 593, 474, 697, 475, 688, 421, 455];
        private static readonly short[] SpeechDataStds = [555, 505, 567, 524, 585, 1231, 509, 828, 492, 1540, 1079, 850];
        private const short MaxSpeechFrames = 6;
        private const short MinStd = 384;

        // Per mode: over_hang_max_1, over_hang_max_2, individual and total thresholds for 30 ms frames.
        private static readonly short[] OverHangMax1 = [3, 3, 2, 2];
        private static readonly short[] OverHangMax2 = [5, 5, 3, 3];
        private static readonly short[] LocalThreshold = [24, 37, 82, 94];
        private static readonly short[] GlobalThreshold = [57, 100, 285, 1100];

        // vad_filterbank.c
        private const short LogConst = 24660;
        private const short LogEnergyIntPart = 14336;
        private static readonly short[] HpZeroCoefs = [6631, -13262, 6631];
        private static readonly short[] HpPoleCoefs = [16384, -7756, 5620];
        private static readonly short[] AllPassCoefsQ15 = [20972, 5571];
        private static readonly short[] OffsetVector = [368, 368, 272, 176, 176, 176];

        // vad_gmm.c / vad_sp.c
        private const int CompVar = 22005;
        private const short Log2Exp = 5909;
        private static readonly short[] AllPassCoefsQ13 = [5243, 1392];
        private const short SmoothingDown = 6553;
        private const short SmoothingUp = 32439;

        private readonly short _overHangMax1, _overHangMax2, _individual, _total;
        private readonly int[] _downsamplingFilterStates = new int[4];
        private readonly short[] _noiseMeans = new short[TableSize];
        private readonly short[] _speechMeans = new short[TableSize];
        private readonly short[] _noiseStds = new short[TableSize];
        private readonly short[] _speechStds = new short[TableSize];
        private readonly short[] _indexVector = new short[16 * NumChannels];
        private readonly short[] _lowValueVector = new short[16 * NumChannels];
        private readonly short[] _meanValue = new short[NumChannels];
        private readonly short[] _upperState = new short[5];
        private readonly short[] _lowerState = new short[5];
        private readonly short[] _hpFilterState = new short[4];
        private int _frameCounter;
        private short _overHang;
        private short _numOfSpeech;

        private readonly short[] _speechNb = new short[FrameSamples / 2];
        private readonly short[] _hp120 = new short[120], _lp120 = new short[120], _hp60 = new short[60], _lp60 = new short[60];

        public Core(int mode)
        {
            Array.Copy(NoiseDataMeans, _noiseMeans, TableSize);
            Array.Copy(SpeechDataMeans, _speechMeans, TableSize);
            Array.Copy(NoiseDataStds, _noiseStds, TableSize);
            Array.Copy(SpeechDataStds, _speechStds, TableSize);
            Array.Fill(_lowValueVector, (short)10000);
            Array.Fill(_meanValue, (short)1600);
            _overHangMax1 = OverHangMax1[mode];
            _overHangMax2 = OverHangMax2[mode];
            _individual = LocalThreshold[mode];
            _total = GlobalThreshold[mode];
        }

        /// <summary>WebRtcVad_Process for one 30 ms frame at 16 kHz: 1 for speech, 0 otherwise.</summary>
        public float Process16Khz(short[] frame)
        {
            Downsampling(frame, _speechNb, _downsamplingFilterStates, frame.Length);
            Span<short> features = stackalloc short[NumChannels];
            short totalPower = CalculateFeatures(_speechNb, features);
            return GmmProbability(features, totalPower) > 0 ? 1f : 0f;
        }

        private static void Downsampling(short[] signalIn, short[] signalOut, int[] filterState, int inLength)
        {
            int tmp32_1 = filterState[0];
            int tmp32_2 = filterState[1];
            int halfLength = inLength >> 1;
            int inIdx = 0;
            for (int n = 0; n < halfLength; n++)
            {
                short tmp16_1 = (short)((tmp32_1 >> 1) + ((AllPassCoefsQ13[0] * signalIn[inIdx]) >> 14));
                signalOut[n] = tmp16_1;
                tmp32_1 = signalIn[inIdx++] - ((AllPassCoefsQ13[0] * tmp16_1) >> 12);

                short tmp16_2 = (short)((tmp32_2 >> 1) + ((AllPassCoefsQ13[1] * signalIn[inIdx]) >> 14));
                signalOut[n] = (short)(signalOut[n] + tmp16_2);
                tmp32_2 = signalIn[inIdx++] - ((AllPassCoefsQ13[1] * tmp16_2) >> 12);
            }
            filterState[0] = tmp32_1;
            filterState[1] = tmp32_2;
        }

        private short CalculateFeatures(short[] dataIn, Span<short> features)
        {
            short totalEnergy = 0;
            int halfDataLength = dataIn.Length >> 1;
            int length = halfDataLength;

            // [0-4000] Hz -> [2000-4000] (hp_120) and [0-2000] (lp_120)
            SplitFilter(dataIn, 0, dataIn.Length, ref _upperState[0], ref _lowerState[0], _hp120, _lp120);

            // [2000-4000] -> [3000-4000] (hp_60) and [2000-3000] (lp_60)
            SplitFilter(_hp120, 0, length, ref _upperState[1], ref _lowerState[1], _hp60, _lp60);
            length >>= 1;
            LogOfEnergy(_hp60, length, OffsetVector[5], ref totalEnergy, out features[5]);
            LogOfEnergy(_lp60, length, OffsetVector[4], ref totalEnergy, out features[4]);

            // [0-2000] -> [1000-2000] (hp_60) and [0-1000] (lp_60)
            length = halfDataLength;
            SplitFilter(_lp120, 0, length, ref _upperState[2], ref _lowerState[2], _hp60, _lp60);
            length >>= 1;
            LogOfEnergy(_hp60, length, OffsetVector[3], ref totalEnergy, out features[3]);

            // [0-1000] -> [500-1000] (hp_120) and [0-500] (lp_120)
            SplitFilter(_lp60, 0, length, ref _upperState[3], ref _lowerState[3], _hp120, _lp120);
            length >>= 1;
            LogOfEnergy(_hp120, length, OffsetVector[2], ref totalEnergy, out features[2]);

            // [0-500] -> [250-500] (hp_60) and [0-250] (lp_60)
            SplitFilter(_lp120, 0, length, ref _upperState[4], ref _lowerState[4], _hp60, _lp60);
            length >>= 1;
            LogOfEnergy(_hp60, length, OffsetVector[1], ref totalEnergy, out features[1]);

            // Remove 0-80 Hz with a high-pass filter.
            HighPassFilter(_lp60, length, _hpFilterState, _hp120);
            LogOfEnergy(_hp120, length, OffsetVector[0], ref totalEnergy, out features[0]);

            return totalEnergy;
        }

        private static void HighPassFilter(short[] dataIn, int dataLength, short[] filterState, short[] dataOut)
        {
            for (int i = 0; i < dataLength; i++)
            {
                int tmp32 = HpZeroCoefs[0] * dataIn[i];
                tmp32 += HpZeroCoefs[1] * filterState[0];
                tmp32 += HpZeroCoefs[2] * filterState[1];
                filterState[1] = filterState[0];
                filterState[0] = dataIn[i];

                tmp32 -= HpPoleCoefs[1] * filterState[2];
                tmp32 -= HpPoleCoefs[2] * filterState[3];
                filterState[3] = filterState[2];
                filterState[2] = (short)(tmp32 >> 14);
                dataOut[i] = filterState[2];
            }
        }

        private static void AllPassFilter(short[] dataIn, int start, int dataLength, short filterCoefficient, ref short filterState, short[] dataOut)
        {
            int state32 = filterState * (1 << 16);
            int inIdx = start;
            for (int i = 0; i < dataLength; i++)
            {
                int tmp32 = state32 + filterCoefficient * dataIn[inIdx];
                short tmp16 = (short)(tmp32 >> 16);
                dataOut[i] = tmp16;
                state32 = (dataIn[inIdx] * (1 << 14)) - filterCoefficient * tmp16;
                state32 *= 2;
                inIdx += 2;
            }
            filterState = (short)(state32 >> 16);
        }

        private static void SplitFilter(short[] dataIn, int start, int dataLength, ref short upperState, ref short lowerState, short[] hpOut, short[] lpOut)
        {
            int halfLength = dataLength >> 1;
            AllPassFilter(dataIn, start, halfLength, AllPassCoefsQ15[0], ref upperState, hpOut);
            AllPassFilter(dataIn, start + 1, halfLength, AllPassCoefsQ15[1], ref lowerState, lpOut);
            for (int i = 0; i < halfLength; i++)
            {
                short tmpOut = hpOut[i];
                hpOut[i] = (short)(hpOut[i] - lpOut[i]);
                lpOut[i] = (short)(lpOut[i] + tmpOut);
            }
        }

        private static void LogOfEnergy(short[] dataIn, int dataLength, short offset, ref short totalEnergy, out short logEnergy)
        {
            uint energy = (uint)Energy(dataIn, dataLength, out int totRshifts);
            if (energy == 0)
            {
                logEnergy = offset;
                return;
            }

            int normalizingRshifts = 17 - NormU32(energy);
            short log2Energy = LogEnergyIntPart;
            totRshifts += normalizingRshifts;
            if (normalizingRshifts < 0)
            {
                energy <<= -normalizingRshifts;
            }
            else
            {
                energy >>= normalizingRshifts;
            }

            log2Energy = (short)(log2Energy + (short)((energy & 0x00003FFF) >> 4));
            logEnergy = (short)(((LogConst * log2Energy) >> 19) + ((totRshifts * LogConst) >> 9));
            if (logEnergy < 0)
            {
                logEnergy = 0;
            }
            logEnergy = (short)(logEnergy + offset);

            if (totalEnergy <= MinEnergy)
            {
                if (totRshifts >= 0)
                {
                    totalEnergy = (short)(totalEnergy + MinEnergy + 1);
                }
                else
                {
                    totalEnergy = (short)(totalEnergy + (short)(energy >> -totRshifts));
                }
            }
        }

        private static int Energy(short[] vector, int length, out int scaleFactor)
        {
            int scaling = GetScalingSquare(vector, length, length);
            int en = 0;
            for (int i = 0; i < length; i++)
            {
                en += (vector[i] * vector[i]) >> scaling;
            }
            scaleFactor = scaling;
            return en;
        }

        private static short GetScalingSquare(short[] vector, int length, int times)
        {
            short nbits = (short)(32 - BitOperations.LeadingZeroCount((uint)times));
            short smax = -1;
            for (int i = 0; i < length; i++)
            {
                short sabs = (short)(vector[i] > 0 ? vector[i] : -vector[i]);
                smax = sabs > smax ? sabs : smax;
            }
            short t = NormW32(smax * smax);
            if (smax == 0) return 0;
            return (short)(t > nbits ? 0 : nbits - t);
        }

        private static short NormW32(int a) =>
            a == 0 ? (short)0 : (short)(BitOperations.LeadingZeroCount((uint)(a < 0 ? ~a : a)) - 1);

        private static short NormU32(uint a) =>
            a == 0 ? (short)0 : (short)BitOperations.LeadingZeroCount(a);

        private static int DivW32W16(int num, short den) => den != 0 ? num / den : 0x7FFFFFFF;

        private static int GaussianProbability(short input, short mean, short std, out short delta)
        {
            short exp_value = 0;
            int tmp32 = 131072 + (std >> 1);
            short inv_std = (short)DivW32W16(tmp32, std);
            short tmp16 = (short)(inv_std >> 2);
            short inv_std2 = (short)((tmp16 * tmp16) >> 2);
            tmp16 = (short)(input << 3);
            tmp16 = (short)(tmp16 - mean);
            delta = (short)((inv_std2 * tmp16) >> 10);
            tmp32 = (delta * tmp16) >> 9;

            if (tmp32 < CompVar)
            {
                tmp16 = (short)((Log2Exp * tmp32) >> 12);
                tmp16 = (short)-tmp16;
                exp_value = (short)(0x0400 | (tmp16 & 0x03FF));
                tmp16 = (short)(tmp16 ^ 0xFFFF);
                tmp16 >>= 10;
                tmp16 += 1;
                exp_value >>= tmp16;
            }

            return inv_std * exp_value;
        }

        private int WeightedAverage(short[] data, int start, short offset, short[] weights, int weightStart)
        {
            int weightedAverage = 0;
            for (int k = 0; k < NumGaussians; k++)
            {
                int idx = start + k * NumChannels;
                data[idx] = (short)(data[idx] + offset);
                weightedAverage += data[idx] * weights[weightStart + k * NumChannels];
            }
            return weightedAverage;
        }

        private short FindMinimum(short featureValue, int channel)
        {
            int position = -1;
            int offset = channel << 4;
            short currentMedian = 1600;
            short alpha = 0;
            var age = _indexVector.AsSpan(offset, 16);
            var smallest = _lowValueVector.AsSpan(offset, 16);

            for (int i = 0; i < 16; i++)
            {
                if (age[i] != 100)
                {
                    age[i]++;
                }
                else
                {
                    for (int j = i; j < 15; j++)
                    {
                        smallest[j] = smallest[j + 1];
                        age[j] = age[j + 1];
                    }
                    age[15] = 101;
                    smallest[15] = 10000;
                }
            }

            if (featureValue < smallest[7])
            {
                if (featureValue < smallest[3])
                {
                    if (featureValue < smallest[1])
                    {
                        position = featureValue < smallest[0] ? 0 : 1;
                    }
                    else
                    {
                        position = featureValue < smallest[2] ? 2 : 3;
                    }
                }
                else if (featureValue < smallest[5])
                {
                    position = featureValue < smallest[4] ? 4 : 5;
                }
                else
                {
                    position = featureValue < smallest[6] ? 6 : 7;
                }
            }
            else if (featureValue < smallest[15])
            {
                if (featureValue < smallest[11])
                {
                    if (featureValue < smallest[9])
                    {
                        position = featureValue < smallest[8] ? 8 : 9;
                    }
                    else
                    {
                        position = featureValue < smallest[10] ? 10 : 11;
                    }
                }
                else if (featureValue < smallest[13])
                {
                    position = featureValue < smallest[12] ? 12 : 13;
                }
                else
                {
                    position = featureValue < smallest[14] ? 14 : 15;
                }
            }

            if (position > -1)
            {
                for (int i = 15; i > position; i--)
                {
                    smallest[i] = smallest[i - 1];
                    age[i] = age[i - 1];
                }
                smallest[position] = featureValue;
                age[position] = 1;
            }

            if (_frameCounter > 2)
            {
                currentMedian = smallest[2];
            }
            else if (_frameCounter > 0)
            {
                currentMedian = smallest[0];
            }

            if (_frameCounter > 0)
            {
                alpha = currentMedian < _meanValue[channel] ? SmoothingDown : SmoothingUp;
            }

            int tmp32 = (alpha + 1) * _meanValue[channel];
            tmp32 += (short.MaxValue - alpha) * currentMedian;
            tmp32 += 16384;
            _meanValue[channel] = (short)(tmp32 >> 15);
            return _meanValue[channel];
        }

        private int GmmProbability(Span<short> features, short totalPower)
        {
            int vadflag = 0;
            Span<short> deltaN = stackalloc short[TableSize];
            Span<short> deltaS = stackalloc short[TableSize];
            Span<short> ngprvec = stackalloc short[TableSize];
            Span<short> sgprvec = stackalloc short[TableSize];
            Span<int> noiseProbability = stackalloc int[NumGaussians];
            Span<int> speechProbability = stackalloc int[NumGaussians];
            int sumLogLikelihoodRatios = 0;

            if (totalPower > MinEnergy)
            {
                for (int channel = 0; channel < NumChannels; channel++)
                {
                    int h0Test = 0;
                    int h1Test = 0;
                    for (int k = 0; k < NumGaussians; k++)
                    {
                        int gaussian = channel + k * NumChannels;
                        int p = GaussianProbability(features[channel], _noiseMeans[gaussian], _noiseStds[gaussian], out deltaN[gaussian]);
                        noiseProbability[k] = NoiseDataWeights[gaussian] * p;
                        h0Test += noiseProbability[k];

                        p = GaussianProbability(features[channel], _speechMeans[gaussian], _speechStds[gaussian], out deltaS[gaussian]);
                        speechProbability[k] = SpeechDataWeights[gaussian] * p;
                        h1Test += speechProbability[k];
                    }

                    short shiftsH0 = NormW32(h0Test);
                    short shiftsH1 = NormW32(h1Test);
                    if (h0Test == 0) shiftsH0 = 31;
                    if (h1Test == 0) shiftsH1 = 31;
                    short logLikelihoodRatio = (short)(shiftsH0 - shiftsH1);

                    sumLogLikelihoodRatios += logLikelihoodRatio * SpectrumWeight[channel];
                    if ((logLikelihoodRatio * 4) > _individual)
                    {
                        vadflag = 1;
                    }

                    short h0 = (short)(h0Test >> 12);
                    if (h0 > 0)
                    {
                        int tmp1 = (int)(((uint)noiseProbability[0] & 0xFFFFF000u) << 2);
                        ngprvec[channel] = (short)DivW32W16(tmp1, h0);
                        ngprvec[channel + NumChannels] = (short)(16384 - ngprvec[channel]);
                    }
                    else
                    {
                        ngprvec[channel] = 16384;
                    }

                    short h1 = (short)(h1Test >> 12);
                    if (h1 > 0)
                    {
                        int tmp1 = (int)(((uint)speechProbability[0] & 0xFFFFF000u) << 2);
                        sgprvec[channel] = (short)DivW32W16(tmp1, h1);
                        sgprvec[channel + NumChannels] = (short)(16384 - sgprvec[channel]);
                    }
                }

                if (sumLogLikelihoodRatios >= _total) vadflag |= 1;

                short maxspe = 12800;
                for (int channel = 0; channel < NumChannels; channel++)
                {
                    short featureMinimum = FindMinimum(features[channel], channel);
                    int noiseGlobalMean = WeightedAverage(_noiseMeans, channel, 0, NoiseDataWeights, channel);
                    short tmp1_s16 = (short)(noiseGlobalMean >> 6);

                    for (int k = 0; k < NumGaussians; k++)
                    {
                        int gaussian = channel + k * NumChannels;
                        short nmk = _noiseMeans[gaussian];
                        short smk = _speechMeans[gaussian];
                        short nsk = _noiseStds[gaussian];
                        short ssk = _speechStds[gaussian];
                        short tmp_s16;
                        short delt;

                        short nmk2 = nmk;
                        if (vadflag == 0)
                        {
                            delt = (short)((ngprvec[gaussian] * deltaN[gaussian]) >> 11);
                            nmk2 = (short)(nmk + (short)((delt * NoiseUpdateConst) >> 22));
                        }

                        short ndelt = (short)((featureMinimum << 4) - tmp1_s16);
                        short nmk3 = (short)(nmk2 + (short)((ndelt * BackEta) >> 9));

                        tmp_s16 = (short)((k + 5) << 7);
                        if (nmk3 < tmp_s16) nmk3 = tmp_s16;
                        tmp_s16 = (short)((72 + k - channel) << 7);
                        if (nmk3 > tmp_s16) nmk3 = tmp_s16;
                        _noiseMeans[gaussian] = nmk3;

                        if (vadflag != 0)
                        {
                            delt = (short)((sgprvec[gaussian] * deltaS[gaussian]) >> 11);
                            tmp_s16 = (short)((delt * SpeechUpdateConst) >> 21);
                            short smk2 = (short)(smk + ((tmp_s16 + 1) >> 1));

                            short maxmu = (short)(maxspe + 640);
                            if (smk2 < MinimumMean[k]) smk2 = MinimumMean[k];
                            if (smk2 > maxmu) smk2 = maxmu;
                            _speechMeans[gaussian] = smk2;

                            tmp_s16 = (short)((smk + 4) >> 3);
                            tmp_s16 = (short)(features[channel] - tmp_s16);
                            int tmp1_s32 = (deltaS[gaussian] * tmp_s16) >> 3;
                            int tmp2_s32 = tmp1_s32 - 4096;
                            tmp_s16 = (short)(sgprvec[gaussian] >> 2);
                            tmp1_s32 = unchecked(tmp_s16 * tmp2_s32);
                            tmp2_s32 = tmp1_s32 >> 4;

                            if (tmp2_s32 > 0)
                            {
                                tmp_s16 = (short)DivW32W16(tmp2_s32, (short)(ssk * 10));
                            }
                            else
                            {
                                tmp_s16 = (short)DivW32W16(-tmp2_s32, (short)(ssk * 10));
                                tmp_s16 = (short)-tmp_s16;
                            }
                            tmp_s16 += 128;
                            ssk = (short)(ssk + (tmp_s16 >> 8));
                            if (ssk < MinStd) ssk = MinStd;
                            _speechStds[gaussian] = ssk;
                        }
                        else
                        {
                            tmp_s16 = (short)(features[channel] - (nmk >> 3));
                            int tmp1_s32 = (deltaN[gaussian] * tmp_s16) >> 3;
                            tmp1_s32 -= 4096;
                            tmp_s16 = (short)((ngprvec[gaussian] + 2) >> 2);
                            int tmp2_s32 = unchecked(tmp_s16 * tmp1_s32);
                            tmp1_s32 = tmp2_s32 >> 14;

                            if (tmp1_s32 > 0)
                            {
                                tmp_s16 = (short)DivW32W16(tmp1_s32, nsk);
                            }
                            else
                            {
                                tmp_s16 = (short)DivW32W16(-tmp1_s32, nsk);
                                tmp_s16 = (short)-tmp_s16;
                            }
                            tmp_s16 += 32;
                            nsk = (short)(nsk + (tmp_s16 >> 6));
                            if (nsk < MinStd) nsk = MinStd;
                            _noiseStds[gaussian] = nsk;
                        }
                    }

                    noiseGlobalMean = WeightedAverage(_noiseMeans, channel, 0, NoiseDataWeights, channel);
                    int speechGlobalMean = WeightedAverage(_speechMeans, channel, 0, SpeechDataWeights, channel);

                    short diff = (short)((short)(speechGlobalMean >> 9) - (short)(noiseGlobalMean >> 9));
                    if (diff < MinimumDifference[channel])
                    {
                        short tmp = (short)(MinimumDifference[channel] - diff);
                        short tmp1 = (short)((13 * tmp) >> 2);
                        short tmp2 = (short)((3 * tmp) >> 2);
                        speechGlobalMean = WeightedAverage(_speechMeans, channel, tmp1, SpeechDataWeights, channel);
                        noiseGlobalMean = WeightedAverage(_noiseMeans, channel, (short)-tmp2, NoiseDataWeights, channel);
                    }

                    maxspe = MaximumSpeech[channel];
                    short tmp2_s16 = (short)(speechGlobalMean >> 7);
                    if (tmp2_s16 > maxspe)
                    {
                        tmp2_s16 -= maxspe;
                        for (int k = 0; k < NumGaussians; k++)
                        {
                            _speechMeans[channel + k * NumChannels] -= tmp2_s16;
                        }
                    }

                    tmp2_s16 = (short)(noiseGlobalMean >> 7);
                    if (tmp2_s16 > MaximumNoise[channel])
                    {
                        tmp2_s16 -= MaximumNoise[channel];
                        for (int k = 0; k < NumGaussians; k++)
                        {
                            _noiseMeans[channel + k * NumChannels] -= tmp2_s16;
                        }
                    }
                }
                _frameCounter++;
            }

            if (vadflag == 0)
            {
                if (_overHang > 0)
                {
                    vadflag = 2 + _overHang;
                    _overHang--;
                }
                _numOfSpeech = 0;
            }
            else
            {
                _numOfSpeech++;
                if (_numOfSpeech > MaxSpeechFrames)
                {
                    _numOfSpeech = MaxSpeechFrames;
                    _overHang = _overHangMax2;
                }
                else
                {
                    _overHang = _overHangMax1;
                }
            }
            return vadflag;
        }
    }
}
