using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.Services;

/// <summary>The embedding model loaded at start, and why it is not the one the user chose, if it is not.</summary>
public sealed record StartupModel(ISpeakerEmbeddingModel Model, string? Warning);

/// <summary>
/// Swaps the voice-embedding model and the sidecar's models while the app runs (docs/SPEC-model-swap.md), and tells
/// which clips were analysed with other models.
/// </summary>
public sealed class ModelManager
{
    private readonly Func<EmbeddingModelEntry, ISpeakerEmbeddingModel> _load;
    private readonly UserSettingsStore? _settings;
    private readonly string? _profilesDirectory;

    /// <param name="load">Loads and verifies a catalog entry; the app passes <c>e => new OnnxEmbeddingModel(e)</c>.</param>
    /// <param name="sidecar">Null when the app has no sidecar; sidecar model calls then fail with a clear message.</param>
    public ModelManager(
        EmbeddingModelCatalog catalog,
        SwappableEmbeddingModel embeddingModel,
        Func<EmbeddingModelEntry, ISpeakerEmbeddingModel> load,
        ISidecarModelControl? sidecar = null,
        UserSettingsStore? settings = null,
        string? profilesDirectory = null)
    {
        Catalog = catalog;
        EmbeddingModel = embeddingModel;
        _load = load;
        Sidecar = sidecar;
        _settings = settings;
        _profilesDirectory = profilesDirectory;
    }

    /// <summary>Raised (on the caller's thread) after the sidecar's active models are read or changed.</summary>
    public event Action? SidecarModelsChanged;

    public EmbeddingModelCatalog Catalog { get; }
    public SwappableEmbeddingModel EmbeddingModel { get; }
    public ISidecarModelControl? Sidecar { get; }

    /// <summary>The sidecar's active models as last read or set; null until the sidecar has answered.</summary>
    public IReadOnlyDictionary<string, string>? SidecarModels { get; private set; }

    /// <summary>Loads the model chosen in settings, falling back to the built-in default when it is gone or broken.</summary>
    public static StartupModel LoadStartupModel(
        EmbeddingModelCatalog catalog,
        string? preferredModelId,
        Func<EmbeddingModelEntry, ISpeakerEmbeddingModel> load,
        Func<ISpeakerEmbeddingModel> loadDefault)
    {
        string? warning = null;
        if (!string.IsNullOrEmpty(preferredModelId))
        {
            var entry = catalog.Find(preferredModelId);
            if (entry == null)
            {
                warning = $"The chosen voice model '{preferredModelId}' is no longer available; using the default model.";
            }
            else
            {
                try
                {
                    return new StartupModel(load(entry), null);
                }
                catch (Exception ex)
                {
                    warning = $"The chosen voice model '{entry.DisplayName}' could not be loaded ({ex.Message}); using the default model.";
                }
            }
            VoiceScanLogger.Warn(nameof(ModelManager), warning);
        }
        return new StartupModel(loadDefault(), warning);
    }

    /// <summary>
    /// Loads <paramref name="modelId"/> off the calling thread and makes it the active model. The previous model stays
    /// active if loading fails or the model is in use by a scan or re-analysis.
    /// </summary>
    public async Task SwitchEmbeddingModelAsync(string modelId, CancellationToken ct = default)
    {
        var entry = Catalog.Find(modelId) ?? throw new InvalidOperationException($"Voice model '{modelId}' is not available.");
        if (entry.ModelId == EmbeddingModel.ModelId) return;

        var loaded = await Task.Run(() => _load(entry), ct);
        ISpeakerEmbeddingModel previous;
        try
        {
            previous = EmbeddingModel.Swap(loaded);
        }
        catch
        {
            loaded.Dispose();
            throw;
        }
        previous.Dispose();
        VoiceScanLogger.Info(nameof(ModelManager), $"Voice model switched to {loaded.ModelVersion}");

        if (_settings != null)
        {
            _settings.Current.EmbeddingModelId = entry.ModelId;
            _settings.Save();
        }
    }

    /// <summary>Approves an ONNX model and test-loads it; a model that does not load is withdrawn again.</summary>
    public async Task<EmbeddingModelEntry> ImportEmbeddingModelAsync(
        string path, string? displayName, FeatureFrontEnd frontEnd, CancellationToken ct = default)
    {
        var entry = await Task.Run(() => Catalog.Import(path, displayName, frontEnd), ct);
        try
        {
            await Task.Run(() => _load(entry).Dispose(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (entry.ModelId != EmbeddingModel.ModelId) Catalog.Remove(entry.ModelId);
            throw new InvalidOperationException($"The model could not be loaded, so it was not imported: {ex.Message}", ex);
        }
        return entry;
    }

    public void RemoveEmbeddingModel(string modelId)
    {
        if (modelId.Equals(EmbeddingModel.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This model is in use. Switch to another model before removing it.");
        }
        Catalog.Remove(modelId);
    }

    /// <summary>Enrolled voices that must be enrolled again before they can be scanned with the active model.</summary>
    public IReadOnlyList<string> ProfilesNeedingReenrollment() =>
        ProfileLibrary.IncompatibleProfiles(EmbeddingModel.ModelVersion, _profilesDirectory);

    public async Task<SidecarModelStatus> GetSidecarModelsAsync(CancellationToken ct = default)
    {
        var status = await RequireSidecar().GetModelsAsync(ct);
        Remember(status);
        return status;
    }

    /// <summary>Hot-swaps the given sidecar stages; stages that fail keep their model and report the error.</summary>
    public async Task<SidecarModelStatus> SetSidecarModelsAsync(IReadOnlyDictionary<string, string> models, CancellationToken ct = default)
    {
        var status = await RequireSidecar().SetModelsAsync(models, ct);
        Remember(status);
        return status;
    }

    /// <summary>
    /// True when the clip's last whole-clip analysis used another embedding model, or (once the sidecar has answered)
    /// sidecar models that differ from the active ones.
    /// </summary>
    public bool IsOutdated(ClipSummary clip)
    {
        if (clip.Status is not (ClipAnalysisStatus.Analysed or ClipAnalysisStatus.NoSpeech)) return false;
        if (clip.EmbeddingModel != EmbeddingModel.ModelVersion) return true;
        return SidecarModels != null && clip.SidecarModels.Count > 0
            && clip.SidecarModels.Any(kv => !SidecarModels.TryGetValue(kv.Key, out var active) || active != kv.Value);
    }

    private ISidecarModelControl RequireSidecar() =>
        Sidecar ?? throw new InvalidOperationException("This app has no analysis sidecar configured.");

    private void Remember(SidecarModelStatus status)
    {
        SidecarModels = status.Models;
        SidecarModelsChanged?.Invoke();
    }
}
