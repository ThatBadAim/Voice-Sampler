namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public record SpeechInterval(double StartTimeSeconds, double EndTimeSeconds);

/// <summary>
/// Voice Activity Detector using the Silero VAD v5 ONNX model.
/// </summary>
public sealed class SileroVad : IDisposable
{
    private const int FrameSize = 512; // 32ms @ 16kHz
    private const int ContextSize = 64;
    private const int SampleRate = 16000;
    private const double MinSpeechSec = 0.25;
    // Longer than Silero's 0.1 s default so short pauses stay inside one speaker turn and windows span continuous talk.
    private const double MinSilenceSec = 0.3;
    private const double SpeechPadSec = 0.03;

    /// <summary>Identifies every setting that changes VAD output; part of the embedding cache key.</summary>
    public static string SettingsFingerprint =>
        $"silero-v5-ctx{ContextSize}-min{MinSpeechSec}-sil{MinSilenceSec}-pad{SpeechPadSec}-hyst0.15";

    private readonly InferenceSession _session;
    private readonly bool _ownsSession;

    public SileroVad(string? modelPath = null, int deviceId = 0)
    {
        modelPath ??= ResolveVadModelPath();
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Silero VAD model not found at: {modelPath}");
        }

        using var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_CUDA(deviceId);
            _session = new InferenceSession(modelPath, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Silero VAD CUDA provider unavailable: {ex.Message}. Falling back to CPU. {OnnxEmbeddingModel.CudaSetupHint}");
            using var cpuOptions = new SessionOptions();
            _session = new InferenceSession(modelPath, cpuOptions);
        }

        _ownsSession = true;
    }

    public SileroVad(InferenceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsSession = false;
    }

    /// <summary>
    /// Detects contiguous speech intervals across an audio buffer using Silero's reference
    /// post-processing: hysteresis (exit below threshold - 0.15), minimum speech length,
    /// minimum silence length before a segment is closed, and symmetric padding.
    /// </summary>
    public IReadOnlyList<SpeechInterval> DetectSpeechIntervals(float[] audio, float threshold = 0.5f)
    {
        if (audio.Length == 0)
        {
            return Array.Empty<SpeechInterval>();
        }

        var stream = StartProbabilityStream();
        stream.Feed(audio, audio.Length);
        return ProbabilitiesToIntervals(stream.Finish(), (double)audio.Length / SampleRate, threshold);
    }

    public ProbabilityStream StartProbabilityStream() => new(this);

    /// <summary>
    /// Incremental frame-probability computation so arbitrarily long audio can be fed in chunks.
    /// Silero v5 requires the last 64 samples of the previous frame prepended to every frame; without
    /// that context the probabilities are unreliable. A partial last frame is zero-padded by <see cref="Finish"/>.
    /// </summary>
    public sealed class ProbabilityStream
    {
        private readonly SileroVad _owner;
        private readonly DenseTensor<float> _state = new(new float[2 * 1 * 128], [2, 1, 128]);
        private readonly DenseTensor<long> _sampleRate = new(new long[] { SampleRate }, Array.Empty<int>());
        private readonly float[] _input = new float[ContextSize + FrameSize];
        private readonly List<float> _probabilities = new();
        private int _filled;

        internal ProbabilityStream(SileroVad owner)
        {
            _owner = owner;
        }

        public void Feed(float[] samples, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int take = Math.Min(FrameSize - _filled, count - offset);
                Array.Copy(samples, offset, _input, ContextSize + _filled, take);
                _filled += take;
                offset += take;
                if (_filled == FrameSize)
                {
                    RunFrame();
                }
            }
        }

        public float[] Finish()
        {
            if (_filled > 0)
            {
                Array.Clear(_input, ContextSize + _filled, FrameSize - _filled);
                RunFrame();
            }
            return _probabilities.ToArray();
        }

        private void RunFrame()
        {
            var inputTensor = new DenseTensor<float>((float[])_input.Clone(), [1, ContextSize + FrameSize]);
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("state", _state),
                NamedOnnxValue.CreateFromTensor("sr", _sampleRate)
            };

            using var results = _owner._session.Run(inputs);
            _probabilities.Add(results.First(r => r.Name == "output").AsTensor<float>().GetValue(0));

            var nextState = results.First(r => r.Name == "stateN" || r.Name.StartsWith("state")).AsTensor<float>();
            for (int s = 0; s < 2 * 1 * 128; s++)
            {
                _state.SetValue(s, nextState.GetValue(s));
            }

            Array.Copy(_input, FrameSize, _input, 0, ContextSize);
            _filled = 0;
        }
    }

    public static IReadOnlyList<SpeechInterval> ProbabilitiesToIntervals(
        float[] probabilities,
        double totalSeconds,
        float threshold)
    {
        float negThreshold = Math.Max(threshold - 0.15f, 0.01f);
        const double frameSec = (double)FrameSize / SampleRate;

        var raw = new List<(double Start, double End)>();
        bool triggered = false;
        double start = 0.0;
        double silenceStart = -1.0;

        for (int i = 0; i < probabilities.Length; i++)
        {
            double t = i * frameSec;
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
                if (t + frameSec - silenceStart >= MinSilenceSec)
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

    private static string ResolveVadModelPath()
    {
        return AppPaths.FindModel("silero_vad.onnx") ?? Path.GetFullPath("models/silero_vad.onnx");
    }

    public void Dispose()
    {
        if (_ownsSession)
        {
            _session.Dispose();
        }
    }
}
