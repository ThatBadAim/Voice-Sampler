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
            // Attempt enabling CUDA execution provider
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

        // Recreate clean options with fallback if CUDA failed
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
        Console.WriteLine("Inputs:");
        foreach (var (name, meta) in session.InputMetadata)
        {
            Console.WriteLine($"  - {name}: Type={meta.ElementType}, Dims=[{string.Join(",", meta.Dimensions)}]");
        }
        Console.WriteLine("Outputs:");
        foreach (var (name, meta) in session.OutputMetadata)
        {
            Console.WriteLine($"  - {name}: Type={meta.ElementType}, Dims=[{string.Join(",", meta.Dimensions)}]");
        }

        string outputSummary;

        // Perform test forward pass based on model architecture
        if (modelName.Contains("silero_vad", StringComparison.OrdinalIgnoreCase))
        {
            outputSummary = RunSileroVadForwardPass(session);
        }
        else
        {
            outputSummary = RunEmbeddingForwardPass(session);
        }

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

    private static string RunSileroVadForwardPass(InferenceSession session)
    {
        // Silero VAD v5 inputs: input [1, 512], state [2, 1, 128], sr []
        var inputTensor = new DenseTensor<float>(new float[512], [1, 512]);
        var stateTensor = new DenseTensor<float>(new float[2 * 1 * 128], [2, 1, 128]);
        var srTensor = new DenseTensor<long>(new long[] { 16000 }, Array.Empty<int>());

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", srTensor)
        };

        using var results = session.Run(inputs);
        var output = results.First(r => r.Name == "output").AsTensor<float>();
        var speechProb = output.GetValue(0);
        return $"Silero VAD forward pass executed. Speech probability: {speechProb:F4}";
    }

    private static string RunEmbeddingForwardPass(InferenceSession session)
    {
        // 80-dim log-mel filterbanks over 100 frames [1, 100, 80]
        var inputMeta = session.InputMetadata.First();
        var inputName = inputMeta.Key;
        var dummyFeatures = new float[1 * 100 * 80];
        var tensor = new DenseTensor<float>(dummyFeatures, [1, 100, 80]);

        var inputs = new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) };
        using var results = session.Run(inputs);
        var output = results.First().AsTensor<float>();
        var dimensions = string.Join("x", output.Dimensions.ToArray());
        var sampleValue = output.GetValue(0);
        return $"Speaker embedding forward pass executed. Output shape: [{dimensions}], first element: {sampleValue:F4}";
    }

    private static string ResolveDefaultModelPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "models", "silero_vad.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "silero_vad.onnx"),
            Path.Combine(Directory.GetCurrentDirectory(), "models", "wespeaker_en_voxceleb_resnet34.onnx"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "silero_vad.onnx"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "wespeaker_en_voxceleb_resnet34.onnx")
        };

        foreach (var path in candidates)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.GetFullPath("models/silero_vad.onnx");
    }
}
