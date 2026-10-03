namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// ONNX Runtime implementation of ISpeakerEmbeddingModel supporting European (SpeechBrain ECAPA-TDNN)
/// and American (NVIDIA NeMo TitaNet) models with CUDA acceleration and batched tensor execution.
/// </summary>
public sealed class OnnxEmbeddingModel : ISpeakerEmbeddingModel
{
    public const string CudaSetupHint =
        "GPU needs a build with -p:VoiceScanGpu=true plus CUDA 12 and cuDNN 9 (on Linux: libraries visible via LD_LIBRARY_PATH).";

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly bool _isTitaNet;
    private readonly FbankProfile _fbankProfile;

    public string ModelId { get; }
    public int EmbeddingDimension { get; }
    public string ActiveProvider { get; }
    public bool IsCudaActive { get; }

    public OnnxEmbeddingModel(string modelTypeOrPath = "ecapa", int deviceId = 0)
    {
        string modelPath = ResolveModelPath(modelTypeOrPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Embedding model weights not found at: {modelPath}");
        }

        string fileName = Path.GetFileName(modelPath);
        if (fileName.Contains("titanet", StringComparison.OrdinalIgnoreCase))
        {
            ModelId = "nvidia-titanet-small";
            _fbankProfile = FbankProfile.WeSpeaker;
            EmbeddingDimension = 192;
            _isTitaNet = true;
        }
        else if (fileName.Contains("ecapa", StringComparison.OrdinalIgnoreCase))
        {
            ModelId = "speechbrain-ecapa-tdnn";
            _fbankProfile = FbankProfile.WeSpeaker;
            EmbeddingDimension = 192;
            _isTitaNet = false;
        }
        else
        {
            ModelId = Path.GetFileNameWithoutExtension(modelPath);
            EmbeddingDimension = 192;
            _isTitaNet = fileName.Contains("titanet", StringComparison.OrdinalIgnoreCase);
        }

        using var options = new SessionOptions();
        bool cudaActive = false;
        string provider = "CPUExecutionProvider";

        try
        {
            options.AppendExecutionProvider_CUDA(deviceId);
            _session = new InferenceSession(modelPath, options);
            cudaActive = true;
            provider = "CUDAExecutionProvider";
        }
        catch (Exception ex)
        {
            string warnMsg = $"CUDAExecutionProvider failed for {ModelId}: {ex.Message}. Falling back cleanly to CPUExecutionProvider. {CudaSetupHint}";
            Console.WriteLine($"[WARNING] {warnMsg}");
            Logging.VoiceScanLogger.Warn("OnnxEmbeddingModel", warnMsg);
            using var cpuOptions = new SessionOptions();
            _session = new InferenceSession(modelPath, cpuOptions);
            cudaActive = false;
            provider = "CPUExecutionProvider";
        }

        ActiveProvider = provider;
        IsCudaActive = cudaActive;
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("emb", StringComparison.OrdinalIgnoreCase)) 
                      ?? _session.OutputMetadata.Keys.First();
    }

    public float[] ExtractEmbedding(float[] audioWindow)
    {
        var batch = ExtractEmbeddingsBatch(new[] { audioWindow });
        return batch[0];
    }

    public float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows)
    {
        if (audioWindows.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        int batchSize = audioWindows.Count;

        // Compute 80-dim log-mel filterbank features for each window
        var fbanks = new float[batchSize][,];
        int maxFrames = 0;

        for (int b = 0; b < batchSize; b++)
        {
            fbanks[b] = Filterbank.ComputeFbank(audioWindows[b], _fbankProfile);
            int frames = fbanks[b].GetLength(0);
            if (frames > maxFrames) maxFrames = frames;
        }

        if (maxFrames == 0)
        {
            return Enumerable.Range(0, batchSize).Select(_ => new float[EmbeddingDimension]).ToArray();
        }

        IReadOnlyCollection<NamedOnnxValue> inputs;

        if (_isTitaNet)
        {
            // TitaNet expects [batch, 80, frames] and length [batch]
            var tensorData = new float[batchSize * 80 * maxFrames];
            for (int b = 0; b < batchSize; b++)
            {
                int frames = fbanks[b].GetLength(0);
                for (int m = 0; m < 80; m++)
                {
                    for (int t = 0; t < frames; t++)
                    {
                        int idx = (b * 80 * maxFrames) + (m * maxFrames) + t;
                        tensorData[idx] = fbanks[b][t, m];
                    }
                }
            }

            var lengths = new long[batchSize];
            for (int b = 0; b < batchSize; b++) lengths[b] = fbanks[b].GetLength(0);

            var signalTensor = new DenseTensor<float>(tensorData, [batchSize, 80, maxFrames]);
            var lengthTensor = new DenseTensor<long>(lengths, [batchSize]);

            inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("audio_signal", signalTensor),
                NamedOnnxValue.CreateFromTensor("length", lengthTensor)
            };
        }
        else
        {
            // ECAPA-TDNN expects [batch, frames, 80]
            var tensorData = new float[batchSize * maxFrames * 80];
            for (int b = 0; b < batchSize; b++)
            {
                int frames = fbanks[b].GetLength(0);
                for (int t = 0; t < frames; t++)
                {
                    int baseIdx = ((b * maxFrames) + t) * 80;
                    for (int m = 0; m < 80; m++)
                    {
                        tensorData[baseIdx + m] = fbanks[b][t, m];
                    }
                }
            }

            var inputTensor = new DenseTensor<float>(tensorData, [batchSize, maxFrames, 80]);
            inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };
        }

        using var results = _session.Run(inputs);
        var outputTensor = results.First(r => r.Name == _outputName).AsTensor<float>();

        int embDim = outputTensor.Dimensions[^1];
        float[][] embeddings = new float[batchSize][];

        for (int b = 0; b < batchSize; b++)
        {
            float[] emb = new float[embDim];
            float sumSq = 0f;

            for (int d = 0; d < embDim; d++)
            {
                float val = outputTensor.GetValue((b * embDim) + d);
                emb[d] = val;
                sumSq += val * val;
            }

            // L2 Normalization
            float norm = MathF.Sqrt(sumSq + 1e-12f);
            for (int d = 0; d < embDim; d++)
            {
                emb[d] /= norm;
            }

            embeddings[b] = emb;
        }

        return embeddings;
    }

    private static string ResolveModelPath(string modelTypeOrPath)
    {
        if (File.Exists(modelTypeOrPath))
        {
            return Path.GetFullPath(modelTypeOrPath);
        }

        string targetFileName = modelTypeOrPath.ToLowerInvariant() switch
        {
            "titanet" or "titanet-small" or "nvidia" or "nvidia-titanet-small" => "titanet_small.onnx",
            _ => "ecapa_tdnn.onnx"
        };

        return AppPaths.FindModel(targetFileName) ?? Path.GetFullPath(Path.Combine("models", targetFileName));
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
