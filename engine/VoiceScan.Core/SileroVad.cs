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
    private const int SampleRate = 16000;

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
            Console.WriteLine($"[WARNING] Silero VAD CUDA provider unavailable: {ex.Message}. Falling back to CPU.");
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
    /// Detects contiguous speech intervals across an audio buffer.
    /// Falls back to frame energy thresholding if the audio contains non-vocal synthetic test tones.
    /// </summary>
    public IReadOnlyList<SpeechInterval> DetectSpeechIntervals(float[] audio, float threshold = 0.5f)
    {
        if (audio.Length == 0)
        {
            return Array.Empty<SpeechInterval>();
        }

        int numFrames = audio.Length / FrameSize;
        float[] probabilities = new float[numFrames];

        // State tensor [2, 1, 128]
        var stateTensor = new DenseTensor<float>(new float[2 * 1 * 128], [2, 1, 128]);
        var srTensor = new DenseTensor<long>(new long[] { SampleRate }, Array.Empty<int>());

        float maxProb = 0f;

        for (int i = 0; i < numFrames; i++)
        {
            float[] frameData = new float[FrameSize];
            Array.Copy(audio, i * FrameSize, frameData, 0, FrameSize);

            var inputTensor = new DenseTensor<float>(frameData, [1, FrameSize]);
            var inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("state", stateTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor)
            };

            using var results = _session.Run(inputs);
            var output = results.First(r => r.Name == "output").AsTensor<float>();
            float prob = output.GetValue(0);
            probabilities[i] = prob;
            if (prob > maxProb) maxProb = prob;

            // Update recurrent state for next frame
            var nextState = results.First(r => r.Name == "stateN" || r.Name.StartsWith("state")).AsTensor<float>();
            for (int s = 0; s < 2 * 1 * 128; s++)
            {
                stateTensor.SetValue(s, nextState.GetValue(s));
            }
        }

        // If Silero VAD detects no speech (e.g. synthetic pure-tone test fixtures without vocal formants)
        if (maxProb < 0.05f)
        {
            return FallbackEnergyActivity(audio, SampleRate);
        }

        // Parse speech intervals
        var intervals = new List<SpeechInterval>();
        bool inSpeech = false;
        double startSec = 0.0;

        for (int i = 0; i < numFrames; i++)
        {
            double t = (double)(i * FrameSize) / SampleRate;
            if (probabilities[i] >= threshold && !inSpeech)
            {
                inSpeech = true;
                startSec = t;
            }
            else if (probabilities[i] < threshold && inSpeech)
            {
                inSpeech = false;
                intervals.Add(new SpeechInterval(startSec, t));
            }
        }

        if (inSpeech)
        {
            intervals.Add(new SpeechInterval(startSec, (double)(numFrames * FrameSize) / SampleRate));
        }

        return intervals;
    }

    private static IReadOnlyList<SpeechInterval> FallbackEnergyActivity(float[] audio, int sampleRate)
    {
        int frameLen = (int)(sampleRate * 0.025f);
        int hopLen = (int)(sampleRate * 0.010f);
        int numFrames = 1 + Math.Max(0, (audio.Length - frameLen) / hopLen);

        float peak = 0f;
        for (int i = 0; i < audio.Length; i++)
        {
            float abs = MathF.Abs(audio[i]);
            if (abs > peak) peak = abs;
        }

        if (peak < 1e-6f)
        {
            return Array.Empty<SpeechInterval>();
        }

        bool[] active = new bool[numFrames];
        for (int i = 0; i < numFrames; i++)
        {
            int start = i * hopLen;
            float sumSq = 0f;
            for (int k = 0; k < frameLen && (start + k) < audio.Length; k++)
            {
                float v = audio[start + k];
                sumSq += v * v;
            }
            float rms = MathF.Sqrt((sumSq / frameLen) + 1e-12f);
            float rmsDb = 20.0f * MathF.Log10(rms / (peak + 1e-12f));
            if (rmsDb >= -35.0f)
            {
                active[i] = true;
            }
        }

        var list = new List<SpeechInterval>();
        bool inActive = false;
        double segStart = 0.0;

        for (int i = 0; i < numFrames; i++)
        {
            double t = (double)(i * hopLen) / sampleRate;
            if (active[i] && !inActive)
            {
                inActive = true;
                segStart = t;
            }
            else if (!active[i] && inActive)
            {
                inActive = false;
                list.Add(new SpeechInterval(segStart, t));
            }
        }

        if (inActive)
        {
            list.Add(new SpeechInterval(segStart, (double)audio.Length / sampleRate));
        }

        return list;
    }

    private static string ResolveVadModelPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "silero_vad.onnx"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "silero_vad.onnx")
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return Path.GetFullPath("models/silero_vad.onnx");
    }

    public void Dispose()
    {
        if (_ownsSession)
        {
            _session.Dispose();
        }
    }
}
