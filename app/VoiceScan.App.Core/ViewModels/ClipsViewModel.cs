using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core.Inference;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.ViewModels;

/// <summary>
/// Every analysed clip; selecting one lists the speakers heard in it, its transcript and its analysis history, and lets
/// the user re-analyse the clip or a section of it with the active models.
/// </summary>
public sealed class ClipsViewModel : INotifyPropertyChanged
{
    /// <summary>Context either side of a line for "Check in detail".</summary>
    public const double DetailContextSeconds = 5.0;

    private readonly ModerationStore _store;
    private readonly ReanalysisService _reanalysis;
    private readonly ModelManager _models;
    private readonly RelayCommand _reanalyseCommand;
    private List<ClipItem> _all = [];
    private AnalysisPreset _reanalysePreset = AnalysisPreset.Standard;
    private string _rangeFrom = string.Empty;
    private string _rangeTo = string.Empty;
    private string _modelsText = string.Empty;
    private ClipItem? _selectedClip;
    private string _searchText = string.Empty;
    private bool _onlyWithIncidents;
    private string? _statusMessage;
    private int _loadVersion;

    public ClipsViewModel(
        ModerationStore store, ModerationSettings settings, ClipPlayer player, ReanalysisService reanalysis, ModelManager models)
    {
        _store = store;
        _reanalysis = reanalysis;
        _models = models;
        Settings = settings;
        Player = player;

        RefreshCommand = new RelayCommand(_ => Run(RefreshAsync));
        OpenSpeakerCommand = new RelayCommand<AppearanceItem>(a => { if (a != null) SpeakerRequested?.Invoke(a.SpeakerId); });
        PlayUtteranceCommand = new RelayCommand<UtteranceItem>(Play, u => u != null && Player.IsAvailable);
        StopCommand = new RelayCommand(Player.Stop);
        _reanalyseCommand = new RelayCommand(_ => ReanalyseSelected(), _ => SelectedClip != null);
        CheckLineCommand = new RelayCommand<UtteranceItem>(CheckLine, u => u != null);
        CancelReanalysisCommand = new RelayCommand(_reanalysis.Cancel);

        _reanalysis.StateChanged += () =>
        {
            OnPropertyChanged(nameof(ReanalysisText));
            OnPropertyChanged(nameof(IsReanalysing));
        };
        _reanalysis.ClipFinished += _ => Run(RefreshAsync);
        _models.EmbeddingModel.Swapped += () => Run(RefreshAsync);
        _models.SidecarModelsChanged += () => Run(RefreshAsync);

        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModerationSettings.Sensitivity)) Run(RefreshAsync);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised with a speaker id when the user opens a speaker from the selected clip.</summary>
    public event Action<long>? SpeakerRequested;

    public ModerationSettings Settings { get; }
    public ClipPlayer Player { get; }

    public ObservableCollection<ClipItem> Clips { get; } = [];
    public ObservableCollection<AppearanceItem> ClipSpeakers { get; } = [];
    public ObservableCollection<UtteranceItem> ClipTranscript { get; } = [];
    public ObservableCollection<AnalysisRunItem> Runs { get; } = [];
    public IReadOnlyList<AnalysisPreset> Presets { get; } = [AnalysisPreset.Standard, AnalysisPreset.Detailed];

    public ICommand RefreshCommand { get; }
    public ICommand OpenSpeakerCommand { get; }
    public ICommand PlayUtteranceCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReanalyseCommand => _reanalyseCommand;
    public ICommand CheckLineCommand { get; }
    public ICommand CancelReanalysisCommand { get; }

    public AnalysisPreset ReanalysePreset
    {
        get => _reanalysePreset;
        set => SetField(ref _reanalysePreset, value);
    }

    /// <summary>Start of the section to re-analyse ("m:ss", "h:mm:ss" or seconds); blank with <see cref="RangeTo"/> blank means the whole clip.</summary>
    public string RangeFrom
    {
        get => _rangeFrom;
        set => SetField(ref _rangeFrom, value ?? string.Empty);
    }

    public string RangeTo
    {
        get => _rangeTo;
        set => SetField(ref _rangeTo, value ?? string.Empty);
    }

    /// <summary>Models of the selected clip's last whole-clip analysis.</summary>
    public string ModelsText
    {
        get => _modelsText;
        private set => SetField(ref _modelsText, value);
    }

    public string ReanalysisText => _reanalysis.StatusText;
    public bool IsReanalysing => _reanalysis.IsRunning;

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value ?? string.Empty)) ApplyFilter(); }
    }

    public bool OnlyWithIncidents
    {
        get => _onlyWithIncidents;
        set { if (SetField(ref _onlyWithIncidents, value)) ApplyFilter(); }
    }

    public ClipItem? SelectedClip
    {
        get => _selectedClip;
        set
        {
            if (!SetField(ref _selectedClip, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            _reanalyseCommand.RaiseCanExecuteChanged();
            Run(LoadSelectedClipAsync);
        }
    }

    public bool HasSelection => SelectedClip != null;
    public bool IsEmpty => _all.Count == 0;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public async Task RefreshAsync()
    {
        double sensitivity = Settings.Sensitivity;
        var clips = await Task.Run(() => _store.GetClipsAsync(sensitivity));
        _all = clips.Select(c => new ClipItem(c, _models.IsOutdated(c))).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        long? selectedId = SelectedClip?.Id;
        IEnumerable<ClipItem> items = _all;
        if (OnlyWithIncidents) items = items.Where(c => c.HasIncidents);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim();
            items = items.Where(c => c.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Clips.Clear();
        foreach (var clip in items) Clips.Add(clip);

        var reselected = Clips.FirstOrDefault(c => c.Id == selectedId);
        if (reselected != null && ReferenceEquals(reselected, SelectedClip)) return;
        // Assigning the refreshed instance reloads the detail lists, so counts stay in step with the clip list.
        _selectedClip = null;
        SelectedClip = reselected;
        if (reselected == null)
        {
            OnPropertyChanged(nameof(SelectedClip));
            OnPropertyChanged(nameof(HasSelection));
            ClearDetail();
        }
    }

    public async Task LoadSelectedClipAsync()
    {
        int version = ++_loadVersion;
        if (SelectedClip is not { } clip)
        {
            ClearDetail();
            return;
        }

        double sensitivity = Settings.Sensitivity;
        var (speakers, transcript, runs) = await Task.Run(async () =>
            (await _store.GetAppearancesAsync(clip.Id, null, sensitivity), await _store.GetUtterancesAsync(clipId: clip.Id),
             await _store.GetAnalysisRunsAsync(clip.Id)));
        if (version != _loadVersion) return; // a newer selection is loading

        ModelsText = clip.Summary.EmbeddingModel == null ? "Models not recorded"
            : ModelText.Describe(EmbeddingModelName(clip.Summary.EmbeddingModel), clip.Summary.SidecarModels);
        Runs.Clear();
        foreach (var run in runs) Runs.Add(new AnalysisRunItem(run, EmbeddingModelName(run.EmbeddingModel)));

        ClipSpeakers.Clear();
        foreach (var a in speakers) ClipSpeakers.Add(new AppearanceItem(a));
        ClipTranscript.Clear();
        foreach (var u in transcript.OrderBy(u => u.StartSeconds)) ClipTranscript.Add(new UtteranceItem(u, sensitivity));
    }

    private void ClearDetail()
    {
        ClipSpeakers.Clear();
        ClipTranscript.Clear();
        Runs.Clear();
        ModelsText = string.Empty;
    }

    /// <summary>
    /// Queues the selected clip (or the From–To section) for re-analysis with the active models and the chosen preset.
    /// Returns false, with the reason in <see cref="StatusMessage"/>, when the request is not valid.
    /// </summary>
    public bool ReanalyseSelected()
    {
        if (SelectedClip is not { } clip) return false;
        bool hasFrom = TimeFormat.TryParseClock(RangeFrom, out double from);
        bool hasTo = TimeFormat.TryParseClock(RangeTo, out double to);
        if ((RangeFrom.Trim().Length > 0 && !hasFrom) || (RangeTo.Trim().Length > 0 && !hasTo))
        {
            StatusMessage = "Enter times as seconds, m:ss or h:mm:ss.";
            return false;
        }

        SidecarAnalysisRequest request;
        if (!hasFrom && !hasTo)
        {
            request = new SidecarAnalysisRequest(ReanalysePreset);
        }
        else
        {
            double start = hasFrom ? from : 0.0;
            double end = hasTo ? Math.Min(to, clip.Summary.DurationSeconds) : clip.Summary.DurationSeconds;
            if (end <= start)
            {
                StatusMessage = "The section must end after it starts and within the clip.";
                return false;
            }
            if (clip.Summary.EmbeddingModel != null && clip.Summary.EmbeddingModel != _models.EmbeddingModel.ModelVersion)
            {
                StatusMessage = "This clip was analysed with another voice model, so a section cannot be added to it. Re-analyse the whole clip.";
                return false;
            }
            request = new SidecarAnalysisRequest(ReanalysePreset, start, end);
        }

        _reanalysis.Enqueue([new ReanalysisJob(clip.Id, clip.FilePath, clip.FileName, request)]);
        StatusMessage = null;
        return true;
    }

    /// <summary>Re-analyses the line with some context either side using the detailed preset.</summary>
    private void CheckLine(UtteranceItem? item)
    {
        if (item == null) return;
        ReanalysePreset = AnalysisPreset.Detailed;
        RangeFrom = TimeFormat.Clock(Math.Max(0.0, Math.Floor(item.Record.StartSeconds - DetailContextSeconds)));
        RangeTo = TimeFormat.Clock(Math.Ceiling(item.Record.EndSeconds + DetailContextSeconds));
        ReanalyseSelected();
    }

    private string EmbeddingModelName(string version)
    {
        string modelId = version.Split('@')[0];
        return _models.Catalog.Find(modelId)?.DisplayName ?? modelId;
    }

    private void Play(UtteranceItem? item)
    {
        if (item == null) return;
        var r = item.Record;
        if (!File.Exists(r.FilePath))
        {
            StatusMessage = $"Recording not found: {r.FilePath}";
            return;
        }
        Player.Play(r.Id, r.FilePath, r.ClipDurationSeconds, r.StartSeconds, r.EndSeconds);
    }

    private async void Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Error(nameof(ClipsViewModel), "Clips action failed", ex);
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
