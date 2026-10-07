namespace VoiceScan.Core;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>
/// ONNX Runtime implementation of ISpeakerEmbeddingModel for the built-in models (SpeechBrain ECAPA-TDNN and NVIDIA NeMo
/// TitaNet-Small) and user-approved models with the same input layout, each fed the feature front-end it was trained
/// with. CUDA is used when available.
/// </summary>
public sealed class OnnxEmbeddingModel : ISpeakerEmbeddingModel
{
    public const string CudaSetupHint =
        "GPU needs a build with -p:VoiceScanGpu=true plus CUDA 12 and cuDNN 9 (on Linux: libraries visible via LD_LIBRARY_PATH).";

    /// <summary>TitaNet pads the time axis to a multiple of this many frames (NeMo pad_to).</summary>
    private const int TitaNetPadTo = 16;

    private sealed record ModelSpec(
        string ModelId,
        string DisplayName,
        string FileName,
        FeatureFrontEnd FrontEnd,
        ModelOperatingPoint OperatingPoint,
        string[] Aliases);

    // Operating points come from eval/real_speech_eval.py on the LibriSpeech development set (docs/accuracy-log.md):
    // Threshold - 0.08 (the Possible floor) sits at or above the window-level impostor p99.9 cosine, and the cluster
    // distance only merges windows whose average cosine reaches that same impostor p99.9.
    private static readonly ModelSpec[] Supported =
    [
        new("speechbrain-ecapa-tdnn", "ECAPA-TDNN (SpeechBrain)", "ecapa_tdnn.onnx", FeatureFrontEnd.SpeechBrainFbank,
            new ModelOperatingPoint(Threshold: 0.48, ClusterDistanceThreshold: 0.60), ["ecapa", "ecapa-tdnn", "speechbrain"]),
        new("nvidia-titanet-small", "TitaNet-Small (NVIDIA)", "titanet_small.onnx", FeatureFrontEnd.NemoMelSpectrogram,
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
    private readonly FeatureFrontEnd _frontEnd;
    private readonly bool _isTitaNet;

    public EmbeddingModelEntry Entry { get; }
    public string ModelId => Entry.ModelId;
    public string ModelVersion { get; }
    public int EmbeddingDimension { get; }
    public string ActiveProvider { get; }
    public bool IsCudaActive { get; }
    public ModelOperatingPoint OperatingPoint => Entry.OperatingPoint;

    /// <param name="modelNameOrPath">A model id or alias (see <see cref="SupportedModelNames"/>), or the path to a supported model file.</param>
    public OnnxEmbeddingModel(string modelNameOrPath = "ecapa", int deviceId = 0)
        : this(ResolveBuiltIn(modelNameOrPath), deviceId)
    {
    }

    /// <summary>
    /// Loads a catalog entry. Built-in files must match the SHA-256 compiled into the engine; imported files must match
    /// the SHA-256 recorded when the user approved them.
    /// </summary>
    public OnnxEmbeddingModel(EmbeddingModelEntry entry, int deviceId = 0)
    {
        Entry = entry;
        _frontEnd = entry.FrontEnd;
        _isTitaNet = entry.FrontEnd == FeatureFrontEnd.NemoMelSpectrogram;
        string modelPath = entry.FilePath;

        string sha256 = entry.IsBuiltIn ? ModelIntegrity.Verify(modelPath) : ModelIntegrity.VerifyHash(modelPath, entry.Sha256);
        ModelVersion = $"{entry.ModelId}@{sha256[..12].ToLowerInvariant()}+{SpeechFeatures.Fingerprint(_frontEnd)}";

        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            // The default EXHAUSTIVE search benchmarks every conv algorithm for each new input length; transcript
            // lines have arbitrary lengths, so that cost would be paid on almost every line.
            using var cudaOptions = new OrtCUDAProviderOptions();
            cudaOptions.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = deviceId.ToString(CultureInfo.InvariantCulture),
                ["cudnn_conv_algo_search"] = "HEURISTIC"
            });
            options.AppendExecutionProvider_CUDA(cudaOptions);
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
            Parallel.For(0, batchSize, b => features[b] = SpeechFeatures.Compute(audioWindows[b], _frontEnd));
        }
        else
        {
            features[0] = SpeechFeatures.Compute(audioWindows[0], _frontEnd);
        }

        int maxFrames = features.Max(f => f.Frames.GetLength(0));
        if (_isTitaNet)
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
            if (_isTitaNet)
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

    /// <summary>Built-in models whose files were found, with the SHA-256 the engine expects for each.</summary>
    public static IReadOnlyList<EmbeddingModelEntry> AvailableBuiltIns()
    {
        var entries = new List<EmbeddingModelEntry>();
        foreach (var spec in Supported)
        {
            string? path = FindFile(spec);
            if (path != null) entries.Add(ToEntry(spec, path));
        }
        return entries;
    }

    /// <summary>Measured operating point of the built-in model that uses <paramref name="frontEnd"/>.</summary>
    public static ModelOperatingPoint BuiltInOperatingPoint(FeatureFrontEnd frontEnd) =>
        Supported.First(s => s.FrontEnd == frontEnd).OperatingPoint;

    /// <summary>Display name of the built-in model whose released weights have this SHA-256, or null.</summary>
    public static string? BuiltInNameForHash(string sha256) =>
        Supported.FirstOrDefault(s => string.Equals(ModelIntegrity.ExpectedHash(s.FileName), sha256, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName;

    private static EmbeddingModelEntry ToEntry(ModelSpec spec, string path) => new(
        spec.ModelId, spec.DisplayName, path, ModelIntegrity.ExpectedHash(spec.FileName) ?? string.Empty,
        spec.FrontEnd, spec.OperatingPoint, IsBuiltIn: true);

    private static EmbeddingModelEntry ResolveBuiltIn(string modelNameOrPath)
    {
        var (spec, path) = Resolve(modelNameOrPath);
        return ToEntry(spec, path);
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

        string path = FindFile(spec)
            ?? throw new FileNotFoundException(
                $"Embedding model file '{spec.FileName}' was not found in: {string.Join(", ", AppPaths.ModelSearchDirectories())}.");
        return (spec, path);
    }

    private static string? FindFile(ModelSpec spec)
    {
        // 1. AppContext.BaseDirectory/models/
        string baseDirModel = Path.Combine(AppContext.BaseDirectory, "models", spec.FileName);
        if (File.Exists(baseDirModel))
        {
            return Path.GetFullPath(baseDirModel);
        }

        // 2. Repo-root models/
        string? repoRoot = AppPaths.FindRepoRoot();
        if (repoRoot != null)
        {
            string repoModel = Path.Combine(repoRoot, "models", spec.FileName);
            if (File.Exists(repoModel))
            {
                return Path.GetFullPath(repoModel);
            }
        }

        return AppPaths.FindModel(spec.FileName);
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
