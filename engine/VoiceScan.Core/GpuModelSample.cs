namespace VoiceScan.Core;

using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

public record ModelVerificationResult(
    string ModelPath,
    string ModelName,
    string ActiveProvider,
    bool CudaRequested,
    bool CudaActive,
    int InputCount,
    int OutputCount,
    string OutputSummary);

public static class GpuModelSample
{
    public static ModelVerificationResult LoadAndRunSample(string? modelPath = null, int deviceId = 0)
    {
        modelPath ??= ResolveDefaultModelPath();

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"ONNX model not found at resolved path: {modelPath}");
        }

        var modelName = Path.GetFileName(modelPath);
        using var options = new SessionOptions();
        bool cudaRequested = true;
        bool cudaActive = false;
        string activeProvider = "CPUExecutionProvider";

        try
        {
            options.AppendExecutionProvider_CUDA(deviceId);
            using (var testSession = new InferenceSession(modelPath, options))
            {
                cudaActive = true;
                activeProvider = "CUDAExecutionProvider";
                Console.WriteLine($"[INFO] CUDAExecutionProvider successfully loaded and verified for '{modelName}'.");
            }
        }
        catch (Exception ex)
        {
            cudaActive = false;
            Console.WriteLine($"[WARNING] CUDAExecutionProvider failed or is unavailable on this host: {ex.Message}");
            Console.WriteLine("[INFO] Falling back cleanly to CPUExecutionProvider.");
        }

        using var runOptions = new SessionOptions();
        if (cudaActive)
        {
            runOptions.AppendExecutionProvider_CUDA(deviceId);
        }

        using var session = new InferenceSession(modelPath, runOptions);
        var inputCount = session.InputMetadata.Count;
        var outputCount = session.OutputMetadata.Count;

        Console.WriteLine($"=== Model Loaded: {modelName} ===");
        Console.WriteLine($"Active Provider: {activeProvider} (CUDA Active: {cudaActive})");

        string outputSummary = RunEmbeddingForwardPass(session, modelName);

        Console.WriteLine($"Execution output verification: {outputSummary}");
        return new ModelVerificationResult(
            modelPath,
            modelName,
            activeProvider,
            cudaRequested,
            cudaActive,
            inputCount,
            outputCount,
            outputSummary);
    }

    private static string RunEmbeddingForwardPass(InferenceSession session, string modelName)
    {
        bool isTitaNet = modelName.Contains("titanet", StringComparison.OrdinalIgnoreCase);
        IReadOnlyCollection<NamedOnnxValue> inputs;

        if (isTitaNet)
        {
            // TitaNet input: audio_signal [1, 80, 100], length [1]
            var signal = new DenseTensor<float>(new float[1 * 80 * 100], [1, 80, 100]);
            var length = new DenseTensor<long>(new long[] { 100 }, [1]);
            inputs = new[]
            {
                NamedOnnxValue.CreateFromTensor("audio_signal", signal),
                NamedOnnxValue.CreateFromTensor("length", length)
            };
        }
        else
        {
            // ECAPA-TDNN input: features [1, 100, 80]
            var inputName = session.InputMetadata.First().Key;
            var tensor = new DenseTensor<float>(new float[1 * 100 * 80], [1, 100, 80]);
            inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
        }

        using var results = session.Run(inputs);
        var output = results.First(r => r.Name.Contains("emb", StringComparison.OrdinalIgnoreCase) || results.Count == 1).AsTensor<float>();
        var dimensions = string.Join("x", output.Dimensions.ToArray());
        var sampleValue = output.GetValue(0);
        return $"Speaker embedding forward pass executed. Output shape: [{dimensions}], first element: {sampleValue:F4}";
    }

    private static string ResolveDefaultModelPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", "ecapa_tdnn.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "ecapa_tdnn.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "titanet_small.onnx"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "ecapa_tdnn.onnx"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "titanet_small.onnx")
        };

        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.GetFullPath("models/ecapa_tdnn.onnx");
    }
}
