namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// ONNX Runtime implementation of ISpeakerEmbeddingModel with CUDA support and batched tensor execution.
/// </summary>
public sealed class OnnxEmbeddingModel : ISpeakerEmbeddingModel
{
    private readonly InferenceSession _session;
    private readonly string _inputName;

    public string ModelId { get; }
    public int EmbeddingDimension { get; }
    public string ActiveProvider { get; }
    public bool IsCudaActive { get; }

    public OnnxEmbeddingModel(string modelTypeOrPath = "wespeaker", int deviceId = 0)
    {
        string modelPath = ResolveModelPath(modelTypeOrPath);
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Embedding model weights not found at: {modelPath}");
        }

        string fileName = Path.GetFileName(modelPath);
        if (fileName.Contains("campplus", StringComparison.OrdinalIgnoreCase))
        {
            ModelId = "3dspeaker-campplus";
            EmbeddingDimension = 192;
        }
        else if (fileName.Contains("wespeaker", StringComparison.OrdinalIgnoreCase))
        {
            ModelId = "wespeaker-resnet34";
            EmbeddingDimension = 256;
        }
        else
        {
            ModelId = Path.GetFileNameWithoutExtension(modelPath);
            EmbeddingDimension = 256;
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
            string warnMsg = $"CUDAExecutionProvider failed for {ModelId}: {ex.Message}. Falling back cleanly to CPUExecutionProvider.";
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
            fbanks[b] = Filterbank.ComputeFbank(audioWindows[b]);
            int frames = fbanks[b].GetLength(0);
            if (frames > maxFrames) maxFrames = frames;
        }

        if (maxFrames == 0)
        {
            return Enumerable.Range(0, batchSize).Select(_ => new float[EmbeddingDimension]).ToArray();
        }

        // Create dense batch tensor: [batchSize, maxFrames, 80]
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
        var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, inputTensor) };

        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

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
            "campplus" or "3dspeaker" or "3dspeaker-campplus" =>
                "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
            _ => "wespeaker_en_voxceleb_resnet34.onnx"
        };

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", targetFileName),
            Path.Combine(Directory.GetCurrentDirectory(), "models", targetFileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", targetFileName)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return Path.GetFullPath(Path.Combine("models", targetFileName));
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
