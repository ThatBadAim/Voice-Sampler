namespace VoiceScan.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceScan.Core.Logging;

/// <summary>A voice-embedding model the app can load.</summary>
/// <param name="Sha256">Expected SHA-256 (upper- or lower-case hex): from the bundled manifest for built-ins, recorded at approval otherwise.</param>
/// <param name="OperatingPoint">Measured for built-ins; borrowed from the built-in model with the same front-end otherwise.</param>
public sealed record EmbeddingModelEntry(
    string ModelId,
    string DisplayName,
    string FilePath,
    string Sha256,
    FeatureFrontEnd FrontEnd,
    ModelOperatingPoint OperatingPoint,
    bool IsBuiltIn)
{
    /// <summary>Only built-in models have operating points measured on the development set.</summary>
    public bool IsEvaluated => IsBuiltIn;
}

/// <summary>
/// Built-in embedding models plus models the user approved by importing them. Importing copies the file into
/// <c>imported/</c> and records its SHA-256 in <c>approved-models.json</c>; a file that is only placed in a model
/// directory is never listed.
/// </summary>
public sealed class EmbeddingModelCatalog
{
    public const string ApprovedFileName = "approved-models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly Func<IReadOnlyList<EmbeddingModelEntry>> _builtIns;
    private readonly object _gate = new();

    /// <param name="directory">Holds <c>approved-models.json</c> and <c>imported/</c>; defaults to the user's model directory.</param>
    /// <param name="builtIns">Built-in models found on disk; defaults to <see cref="OnnxEmbeddingModel.AvailableBuiltIns"/>.</param>
    public EmbeddingModelCatalog(string? directory = null, Func<IReadOnlyList<EmbeddingModelEntry>>? builtIns = null)
    {
        _directory = directory ?? AppPaths.ModelsDirectory;
        _builtIns = builtIns ?? OnnxEmbeddingModel.AvailableBuiltIns;
    }

    public string ImportedDirectory => Path.Combine(_directory, "imported");

    private string ApprovedPath => Path.Combine(_directory, ApprovedFileName);

    /// <summary>Built-in models found on disk, then approved imports whose file still exists.</summary>
    public IReadOnlyList<EmbeddingModelEntry> List()
    {
        lock (_gate)
        {
            return _builtIns()
                .Concat(ReadApproved().Select(ToEntry).Where(e => File.Exists(e.FilePath)))
                .ToList();
        }
    }

    public EmbeddingModelEntry? Find(string modelId) =>
        List().FirstOrDefault(e => e.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Approves an ONNX embedding model: copies it into <see cref="ImportedDirectory"/> and records its SHA-256, name and
    /// feature front-end. Importing the same file twice returns the existing entry.
    /// </summary>
    /// <exception cref="ArgumentException">Not an existing <c>.onnx</c> file, or it is a built-in model's released weights.</exception>
    public EmbeddingModelEntry Import(string sourcePath, string? displayName, FeatureFrontEnd frontEnd)
    {
        if (!string.Equals(Path.GetExtension(sourcePath), ".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .onnx model files can be imported.", nameof(sourcePath));
        }
        if (!File.Exists(sourcePath))
        {
            throw new ArgumentException($"Model file not found: {sourcePath}", nameof(sourcePath));
        }

        lock (_gate)
        {
            Directory.CreateDirectory(ImportedDirectory);
            // The copy is what gets hashed and loaded, so a source changed after hashing cannot slip through.
            string temp = Path.Combine(ImportedDirectory, $".import-{Guid.NewGuid():N}.tmp");
            File.Copy(sourcePath, temp);
            try
            {
                string sha = ModelIntegrity.HashFile(temp);
                string? builtIn = OnnxEmbeddingModel.BuiltInNameForHash(sha);
                if (builtIn != null)
                {
                    throw new ArgumentException($"This file is the built-in {builtIn} model; it does not need importing.", nameof(sourcePath));
                }

                var approved = ReadApproved();
                var existing = approved.FirstOrDefault(a => a.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase));
                if (existing != null && File.Exists(Path.Combine(ImportedDirectory, existing.File)))
                {
                    return ToEntry(existing);
                }

                string fileName = sha[..12].ToLowerInvariant() + ".onnx";
                File.Move(temp, Path.Combine(ImportedDirectory, fileName), overwrite: true);

                string name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(sourcePath) : displayName.Trim();
                var record = new ApprovedModel("custom-" + sha[..12].ToLowerInvariant(), name, fileName, sha, frontEnd, DateTimeOffset.UtcNow);
                approved.RemoveAll(a => a.Id == record.Id);
                approved.Add(record);
                WriteApproved(approved);
                return ToEntry(record);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    /// <summary>Withdraws approval of an imported model and deletes its copy.</summary>
    public void Remove(string modelId)
    {
        lock (_gate)
        {
            var approved = ReadApproved();
            var record = approved.FirstOrDefault(a => a.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"'{modelId}' is not an imported model.");
            approved.Remove(record);
            WriteApproved(approved);

            string path = Path.Combine(ImportedDirectory, record.File);
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException ex)
            {
                VoiceScanLogger.Warn(nameof(EmbeddingModelCatalog), $"Approval removed, but {path} could not be deleted: {ex.Message}");
            }
        }
    }

    private EmbeddingModelEntry ToEntry(ApprovedModel a) => new(
        a.Id, a.Name, Path.Combine(ImportedDirectory, a.File), a.Sha256, a.FrontEnd,
        OnnxEmbeddingModel.BuiltInOperatingPoint(a.FrontEnd), IsBuiltIn: false);

    private List<ApprovedModel> ReadApproved()
    {
        try
        {
            if (File.Exists(ApprovedPath))
            {
                return JsonSerializer.Deserialize<List<ApprovedModel>>(File.ReadAllText(ApprovedPath), JsonOptions) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            VoiceScanLogger.Warn(nameof(EmbeddingModelCatalog), $"Ignoring unreadable {ApprovedPath}: {ex.Message}");
        }
        return [];
    }

    private void WriteApproved(List<ApprovedModel> approved)
    {
        Directory.CreateDirectory(_directory);
        string temp = ApprovedPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(approved, JsonOptions));
        File.Move(temp, ApprovedPath, overwrite: true);
    }

    private sealed record ApprovedModel(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("front_end")] FeatureFrontEnd FrontEnd,
        [property: JsonPropertyName("approved_at")] DateTimeOffset ApprovedAt);
}
