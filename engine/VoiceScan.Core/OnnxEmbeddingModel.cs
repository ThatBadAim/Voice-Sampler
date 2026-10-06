namespace VoiceScan.Core;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// ONNX Runtime implementation of ISpeakerEmbeddingModel for the supported models: SpeechBrain ECAPA-TDNN and
/// NVIDIA NeMo TitaNet-Small, each fed the feature front-end it was trained with. CUDA is used when available.
/// </summary>
public sealed class OnnxEmbeddingModel : ISpeakerEmbeddingModel
{
    public const string CudaSetupHint =
        "GPU needs a build with -p:VoiceScanGpu=true plus CUDA 12 and cuDNN 9 (on Linux: libraries visible via LD_LIBRARY_PATH).";

    /// <summary>TitaNet pads the time axis to a multiple of this many frames (NeMo pad_to).</summary>
    private const int TitaNetPadTo = 16;

    private sealed record ModelSpec(
        string ModelId,
        string FileName,
        FeatureFrontEnd FrontEnd,
        bool IsTitaNet,
        ModelOperatingPoint OperatingPoint,
        string[] Aliases);

    // Operating points come from eval/real_speech_eval.py on the LibriSpeech development set (docs/accuracy-log.md):
    // Threshold - 0.08 (the Possible floor) sits at or above the window-level impostor p99.9 cosine, and the cluster
    // distance only merges windows whose average cosine reaches that same impostor p99.9.
    private static readonly ModelSpec[] Supported =
    [
        new("speechbrain-ecapa-tdnn", "ecapa_tdnn.onnx", FeatureFrontEnd.SpeechBrainFbank, false,
            new ModelOperatingPoint(Threshold: 0.48, ClusterDistanceThreshold: 0.60), ["ecapa", "ecapa-tdnn", "speechbrain"]),
        new("nvidia-titanet-small", "titanet_small.onnx", FeatureFrontEnd.NemoMelSpectrogram, true,
            new ModelOperatingPoint(Threshold: 0.52, ClusterDistanceThreshold: 0.58), ["titanet", "titanet-small", "nvidia"]),
    ];

    /// <summary>Names accepted by the constructor (model ids and short aliases).</summary>
    public static IReadOnlyList<string> SupportedModelNames { get; } =
        Supported.SelectMany(s => s.Aliases.Prepend(s.ModelId)).ToArray();

    /// <summary>Model file names the engine can load.</summary>
    public static IReadOnlyList<string> SupportedModelFiles { get; } = Supported.Select(s => s.FileName).ToArray();

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly ModelSpec _spec;

    public string ModelId => _spec.ModelId;
    public string ModelVersion { get; }
    public int EmbeddingDimension { get; }
    public string ActiveProvider { get; }
    public bool IsCudaActive { get; }
    public ModelOperatingPoint OperatingPoint => _spec.OperatingPoint;

    /// <param name="modelNameOrPath">A model id or alias (see <see cref="SupportedModelNames"/>), or the path to a supported model file.</param>
    public OnnxEmbeddingModel(string modelNameOrPath = "ecapa", int deviceId = 0)
    {
        (_spec, string modelPath) = Resolve(modelNameOrPath);

        string sha256 = ModelIntegrity.Verify(modelPath);
        ModelVersion = $"{_spec.ModelId}@{sha256[..12].ToLowerInvariant()}+{SpeechFeatures.Fingerprint(_spec.FrontEnd)}";

        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            options.AppendExecutionProvider_CUDA(deviceId);
            _session = new InferenceSession(modelPath, options);
            IsCudaActive = true;
            ActiveProvider = "CUDAExecutionProvider";
        }
        catch (Exception ex)
        {
            Logging.VoiceScanLogger.Warn("OnnxEmbeddingModel",
                $"CUDAExecutionProvider unavailable for {ModelId}: {ex.Message}. Running on CPUExecutionProvider. {CudaSetupHint}");
            using var cpuOptions = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            _session = new InferenceSession(modelPath, cpuOptions);
            IsCudaActive = false;
            ActiveProvider = "CPUExecutionProvider";
        }

        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.FirstOrDefault(k => k.Contains("emb", StringComparison.OrdinalIgnoreCase))
                      ?? _session.OutputMetadata.Keys.First();

        // Also pays lazy kernel/graph initialisation now rather than inside the first file's scan.
        int warmUpDim = RunBatch([new float[2 * 16000]])[0].Length;
        int declared = _session.OutputMetadata[_outputName].Dimensions[^1];
        EmbeddingDimension = declared > 0 ? declared : warmUpDim;
    }

    public float[] ExtractEmbedding(float[] audioWindow) => ExtractEmbeddingsBatch([audioWindow])[0];

    public float[][] ExtractEmbeddingsBatch(IReadOnlyList<float[]> audioWindows) =>
        audioWindows.Count == 0 ? [] : RunBatch(audioWindows);

    private float[][] RunBatch(IReadOnlyList<float[]> audioWindows)
    {
        int batchSize = audioWindows.Count;
        var features = new FeatureMatrix[batchSize];
        if (batchSize > 1)
        {
            Parallel.For(0, batchSize, b => features[b] = SpeechFeatures.Compute(audioWindows[b], _spec.FrontEnd));
        }
        else
        {
            features[0] = SpeechFeatures.Compute(audioWindows[0], _spec.FrontEnd);
        }

        int maxFrames = features.Max(f => f.Frames.GetLength(0));
        if (_spec.IsTitaNet)
        {
            maxFrames = (maxFrames + TitaNetPadTo - 1) / TitaNetPadTo * TitaNetPadTo;
        }

        const int mels = SpeechFeatures.NumMels;
        int tensorLength = batchSize * mels * maxFrames;
        float[] pooled = ArrayPool<float>.Shared.Rent(tensorLength);
        try
        {
            var data = pooled.AsMemory(0, tensorLength);
            var span = data.Span;
            span.Clear(); // shorter windows must be zero-padded

            IReadOnlyCollection<NamedOnnxValue> inputs;
            if (_spec.IsTitaNet)
            {
                // TitaNet expects [batch, 80, frames] and the valid frame count per item.
                var lengths = new long[batchSize];
                for (int b = 0; b < batchSize; b++)
                {
                    var frames = features[b].Frames;
                    int count = frames.GetLength(0);
                    for (int m = 0; m < mels; m++)
                    {
                        int rowBase = (b * mels * maxFrames) + (m * maxFrames);
                        for (int t = 0; t < count; t++)
                        {
                            span[rowBase + t] = frames[t, m];
                        }
                    }
                    lengths[b] = features[b].ValidFrames;
                }

                inputs =
                [
                    NamedOnnxValue.CreateFromTensor("audio_signal", new DenseTensor<float>(data, [batchSize, mels, maxFrames])),
                    NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(lengths, [batchSize]))
                ];
            }
            else
            {
                // ECAPA-TDNN expects [batch, frames, 80]
                for (int b = 0; b < batchSize; b++)
                {
                    var frames = features[b].Frames;
                    int count = frames.GetLength(0);
                    for (int t = 0; t < count; t++)
                    {
                        int baseIdx = ((b * maxFrames) + t) * mels;
                        for (int m = 0; m < mels; m++)
                        {
                            span[baseIdx + m] = frames[t, m];
                        }
                    }
                }

                inputs = [NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(data, [batchSize, maxFrames, mels]))];
            }

            using var results = _session.Run(inputs, [_outputName]);
            var outputTensor = results.First().AsTensor<float>();

            int embDim = outputTensor.Dimensions[^1];
            float[][] embeddings = new float[batchSize][];
            // Row-major [batch, embDim]; DenseTensor exposes the buffer directly, avoiding a per-element indexer call.
            ReadOnlySpan<float> output = outputTensor is DenseTensor<float> dense ? dense.Buffer.Span : outputTensor.ToArray();

            for (int b = 0; b < batchSize; b++)
            {
                float[] emb = output.Slice(b * embDim, embDim).ToArray();
                float sumSq = 0f;
                for (int d = 0; d < embDim; d++)
                {
                    sumSq += emb[d] * emb[d];
                }

                float norm = MathF.Sqrt(sumSq + 1e-12f);
                for (int d = 0; d < embDim; d++)
                {
                    emb[d] /= norm;
                }

                embeddings[b] = emb;
            }

            return embeddings;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(pooled);
        }
    }

    private static (ModelSpec Spec, string Path) Resolve(string modelNameOrPath)
    {
        if (File.Exists(modelNameOrPath))
        {
            string fileName = Path.GetFileName(modelNameOrPath);
            var byFile = Supported.FirstOrDefault(s => s.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"'{fileName}' is not a supported embedding model file. Supported files: {string.Join(", ", SupportedModelFiles)}.",
                    nameof(modelNameOrPath));
            return (byFile, Path.GetFullPath(modelNameOrPath));
        }

        var spec = Supported.FirstOrDefault(s =>
                s.ModelId.Equals(modelNameOrPath, StringComparison.OrdinalIgnoreCase)
                || s.Aliases.Contains(modelNameOrPath, StringComparer.OrdinalIgnoreCase)
                || s.FileName.Equals(modelNameOrPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Unknown embedding model '{modelNameOrPath}'. Supported: {string.Join(", ", SupportedModelNames)}.",
                nameof(modelNameOrPath));

        // 1. AppContext.BaseDirectory/models/
        string baseDirModel = Path.Combine(AppContext.BaseDirectory, "models", spec.FileName);
        if (File.Exists(baseDirModel))
        {
            return (spec, Path.GetFullPath(baseDirModel));
        }

        // 2. Repo-root models/
        string? repoRoot = AppPaths.FindRepoRoot();
        if (repoRoot != null)
        {
            string repoModel = Path.Combine(repoRoot, "models", spec.FileName);
            if (File.Exists(repoModel))
            {
                return (spec, Path.GetFullPath(repoModel));
            }
        }

        string path = AppPaths.FindModel(spec.FileName)
            ?? throw new FileNotFoundException(
                $"Embedding model file '{spec.FileName}' was not found in: {string.Join(", ", AppPaths.ModelSearchDirectories())}.");
        return (spec, path);
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
