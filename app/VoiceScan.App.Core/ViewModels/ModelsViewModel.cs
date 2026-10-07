using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.ViewModels;

/// <summary>
/// Models page: switch or import the voice-embedding model, hot-swap the sidecar's models, and re-analyse clips that
/// were analysed with other models.
/// </summary>
public sealed class ModelsViewModel : INotifyPropertyChanged
{
    private readonly ModelManager _models;
    private readonly ModerationStore _store;
    private readonly ReanalysisService _reanalysis;
    private readonly RelayCommand _useCommand;
    private readonly RelayCommand _removeCommand;
    private readonly RelayCommand _importCommand;
    private readonly RelayCommand _applySidecarCommand;
    private readonly RelayCommand _reanalyseOutdatedCommand;
    private EmbeddingModelItem? _selectedModel;
    private string _importPath = string.Empty;
    private string _importName = string.Empty;
    private FrontEndChoice _importFrontEnd = FrontEndChoice.All[0];
    private AnalysisPreset _reanalysePreset = AnalysisPreset.Standard;
    private string _activeModelText = string.Empty;
    private string _reenrollText = string.Empty;
    private string _sidecarStatusText = "Not checked yet.";
    private string? _statusMessage;
    private List<ClipSummary> _outdated = [];
    private bool _isBusy;

    public ModelsViewModel(ModelManager models, ModerationStore store, ReanalysisService reanalysis, string? startupWarning = null)
    {
        _models = models;
        _store = store;
        _reanalysis = reanalysis;
        _statusMessage = startupWarning;

        RefreshCommand = new RelayCommand(_ => Run(RefreshAsync));
        _useCommand = new RelayCommand(_ => Run(UseSelectedModelAsync), _ => !_isBusy && SelectedModel is { IsActive: false });
        _removeCommand = new RelayCommand(_ => Run(RemoveSelectedModelAsync), _ => !_isBusy && SelectedModel is { IsImported: true, IsActive: false });
        _importCommand = new RelayCommand(_ => Run(ImportAsync), _ => !_isBusy && ImportPath.Length > 0);
        _applySidecarCommand = new RelayCommand(_ => Run(ApplySidecarModelsAsync), _ => !_isBusy);
        _reanalyseOutdatedCommand = new RelayCommand(_ => ReanalyseOutdated(), _ => _outdated.Count > 0);
        CancelReanalysisCommand = new RelayCommand(_reanalysis.Cancel);

        SidecarStages =
        [
            new(SidecarModelStages.Vad, "Speech detection", "pyannote model id or local checkpoint, e.g. pyannote/segmentation-3.0"),
            new(SidecarModelStages.Diarization, "Diarization", "Hugging Face model id or local folder, e.g. nvidia/Nemotron-3-Diarization"),
            new(SidecarModelStages.Asr, "Transcription (Whisper)", "faster-whisper name or local CTranslate2 folder, e.g. large-v3, large-v3-turbo, distil-large-v3"),
            new(SidecarModelStages.Alignment, "Alignment", "Language code (en) or a wav2vec2 model id"),
            new(SidecarModelStages.Moderation, "Offence classifier (Detoxify)", "original, unbiased, multilingual, or a local checkpoint file"),
        ];

        _reanalysis.StateChanged += () =>
        {
            OnPropertyChanged(nameof(ReanalysisText));
            OnPropertyChanged(nameof(IsReanalysing));
        };
        _reanalysis.ClipFinished += _ => Run(RefreshOutdatedAsync);
        _models.EmbeddingModel.Swapped += RefreshEmbeddingModels;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<EmbeddingModelItem> EmbeddingModels { get; } = [];
    public IReadOnlyList<SidecarStageItem> SidecarStages { get; }
    public IReadOnlyList<FrontEndChoice> FrontEnds => FrontEndChoice.All;
    public IReadOnlyList<AnalysisPreset> Presets { get; } = [AnalysisPreset.Standard, AnalysisPreset.Detailed];

    public ICommand RefreshCommand { get; }
    public ICommand UseSelectedModelCommand => _useCommand;
    public ICommand RemoveSelectedModelCommand => _removeCommand;
    public ICommand ImportCommand => _importCommand;
    public ICommand ApplySidecarModelsCommand => _applySidecarCommand;
    public ICommand ReanalyseOutdatedCommand => _reanalyseOutdatedCommand;
    public ICommand CancelReanalysisCommand { get; }

    public EmbeddingModelItem? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetField(ref _selectedModel, value)) return;
            _useCommand.RaiseCanExecuteChanged();
            _removeCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Set by the view's file picker.</summary>
    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (!SetField(ref _importPath, value?.Trim() ?? string.Empty)) return;
            if (ImportName.Length == 0 && _importPath.Length > 0) ImportName = Path.GetFileNameWithoutExtension(_importPath);
            _importCommand.RaiseCanExecuteChanged();
        }
    }

    public string ImportName
    {
        get => _importName;
        set => SetField(ref _importName, value ?? string.Empty);
    }

    public FrontEndChoice ImportFrontEnd
    {
        get => _importFrontEnd;
        set => SetField(ref _importFrontEnd, value ?? FrontEndChoice.All[0]);
    }

    public AnalysisPreset ReanalysePreset
    {
        get => _reanalysePreset;
        set => SetField(ref _reanalysePreset, value);
    }

    public string ActiveModelText
    {
        get => _activeModelText;
        private set => SetField(ref _activeModelText, value);
    }

    /// <summary>Enrolled voices made with another model; empty when every voice works with the active model.</summary>
    public string ReenrollText
    {
        get => _reenrollText;
        private set => SetField(ref _reenrollText, value);
    }

    public string SidecarStatusText
    {
        get => _sidecarStatusText;
        private set => SetField(ref _sidecarStatusText, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            _useCommand.RaiseCanExecuteChanged();
            _removeCommand.RaiseCanExecuteChanged();
            _importCommand.RaiseCanExecuteChanged();
            _applySidecarCommand.RaiseCanExecuteChanged();
        }
    }

    public int OutdatedCount => _outdated.Count;
    public string OutdatedText => _outdated.Count == 0
        ? "Every analysed clip was analysed with the active models."
        : $"{_outdated.Count} clip(s) were analysed with other models.";
    public string ReanalysisText => _reanalysis.StatusText;
    public bool IsReanalysing => _reanalysis.IsRunning;

    public async Task RefreshAsync()
    {
        RefreshEmbeddingModels();
        await RefreshSidecarAsync();
        await RefreshOutdatedAsync();
    }

    public async Task UseSelectedModelAsync()
    {
        if (SelectedModel is not { } item) return;
        await Busy(async () =>
        {
            StatusMessage = $"Loading {item.DisplayName}…";
            await _models.SwitchEmbeddingModelAsync(item.Entry.ModelId);
            StatusMessage = $"Now using {item.DisplayName}." + (item.Entry.IsEvaluated ? string.Empty
                : " Its threshold is not evaluated; check matches by listening before relying on them.");
            await RefreshOutdatedAsync();
        });
    }

    public async Task ImportAsync()
    {
        string path = ImportPath;
        await Busy(async () =>
        {
            StatusMessage = $"Checking {Path.GetFileName(path)}…";
            var entry = await _models.ImportEmbeddingModelAsync(path, ImportName, ImportFrontEnd.FrontEnd);
            ImportPath = string.Empty;
            ImportName = string.Empty;
            RefreshEmbeddingModels();
            SelectedModel = EmbeddingModels.FirstOrDefault(m => m.Entry.ModelId == entry.ModelId);
            StatusMessage = $"Imported {entry.DisplayName}. Select it and choose \"Use this model\" to switch.";
        });
    }

    public async Task RemoveSelectedModelAsync()
    {
        if (SelectedModel is not { } item) return;
        await Busy(() =>
        {
            _models.RemoveEmbeddingModel(item.Entry.ModelId);
            RefreshEmbeddingModels();
            StatusMessage = $"Removed {item.DisplayName}.";
            return Task.CompletedTask;
        });
    }

    /// <summary>Sends every stage whose value differs from the active model to the sidecar.</summary>
    public async Task ApplySidecarModelsAsync()
    {
        var active = _models.SidecarModels ?? new Dictionary<string, string>();
        var changes = SidecarStages
            .Where(s => s.Value.Trim().Length > 0 && (!active.TryGetValue(s.Stage, out var current) || current != s.Value.Trim()))
            .ToDictionary(s => s.Stage, s => s.Value.Trim());
        if (changes.Count == 0)
        {
            StatusMessage = "No sidecar model was changed.";
            return;
        }

        await Busy(async () =>
        {
            StatusMessage = $"Loading {string.Join(", ", changes.Values)} in the sidecar… large models can take minutes.";
            var status = await _models.SetSidecarModelsAsync(changes);
            ShowSidecar(status);
            var failed = changes.Keys.Where(stage => status.Errors.GetValueOrDefault(stage) != null).ToList();
            StatusMessage = failed.Count == 0
                ? "Sidecar models switched."
                : $"Could not load: {string.Join(", ", failed)}. Those stages kept their previous model.";
            await RefreshOutdatedAsync();
        });
    }

    public void ReanalyseOutdated()
    {
        _reanalysis.Enqueue(_outdated.Select(c => new ReanalysisJob(c.Id, c.FilePath, c.FileName, new SidecarAnalysisRequest(ReanalysePreset))));
    }

    private void RefreshEmbeddingModels()
    {
        string activeId = _models.EmbeddingModel.ModelId;
        string? selectedId = SelectedModel?.Entry.ModelId;
        EmbeddingModels.Clear();
        foreach (var entry in _models.Catalog.List())
        {
            EmbeddingModels.Add(new EmbeddingModelItem(entry, entry.ModelId == activeId));
        }
        SelectedModel = EmbeddingModels.FirstOrDefault(m => m.Entry.ModelId == selectedId);

        var active = EmbeddingModels.FirstOrDefault(m => m.IsActive);
        ActiveModelText = $"Active: {active?.DisplayName ?? activeId} ({_models.EmbeddingModel.ActiveProvider})";
        var stale = _models.ProfilesNeedingReenrollment();
        ReenrollText = stale.Count == 0 ? string.Empty
            : $"Enrolled with another model, so they must be enrolled again before scanning: {string.Join(", ", stale)}.";
    }

    private async Task RefreshSidecarAsync()
    {
        if (_models.Sidecar == null)
        {
            SidecarStatusText = "No analysis sidecar is configured.";
            return;
        }
        try
        {
            ShowSidecar(await _models.GetSidecarModelsAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            SidecarStatusText = $"The sidecar is not answering ({ex.Message}). Start it to see and change its models.";
        }
    }

    private void ShowSidecar(SidecarModelStatus status)
    {
        foreach (var stage in SidecarStages)
        {
            stage.Update(
                status.Models.GetValueOrDefault(stage.Stage) ?? string.Empty,
                status.Loaded.GetValueOrDefault(stage.Stage),
                status.Errors.GetValueOrDefault(stage.Stage));
        }
        int loaded = SidecarStages.Count(s => status.Loaded.GetValueOrDefault(s.Stage));
        SidecarStatusText = $"Sidecar connected: {loaded} of {SidecarStages.Count} stages loaded.";
    }

    private async Task RefreshOutdatedAsync()
    {
        var clips = await Task.Run(() => _store.GetClipsAsync(1.0));
        _outdated = clips.Where(_models.IsOutdated).ToList();
        OnPropertyChanged(nameof(OutdatedCount));
        OnPropertyChanged(nameof(OutdatedText));
        _reanalyseOutdatedCommand.RaiseCanExecuteChanged();
    }

    private async Task Busy(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VoiceScanLogger.Error(nameof(ModelsViewModel), "Model action failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Error(nameof(ModelsViewModel), "Models action failed", ex);
            StatusMessage = ex.Message;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
