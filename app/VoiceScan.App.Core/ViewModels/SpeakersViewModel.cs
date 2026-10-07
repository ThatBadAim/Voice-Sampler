using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.ViewModels;

/// <summary>
/// Every speaker heard across all clips. The selected speaker's page shows their name and notes, a voice sample,
/// every clip they appear in (with how confidently they were linked), their incidents and their full transcript.
/// </summary>
public sealed class SpeakersViewModel : INotifyPropertyChanged
{
    public const string NewSpeakerChoice = "New speaker";

    private readonly ModerationStore _store;
    private readonly RelayCommand _moveCommand;
    private readonly RelayCommand _playSampleCommand;
    private List<SpeakerItem> _all = [];
    private SpeakerItem? _selectedSpeaker;
    private SpeakerSummary? _detail;
    private string _searchText = string.Empty;
    private string _editName = string.Empty;
    private string _notes = string.Empty;
    private UtteranceItem? _voiceSample;
    private AppearanceItem? _selectedAppearance;
    private SpeakerChoice? _moveTarget;
    private string? _statusMessage;
    private int _loadVersion;

    public SpeakersViewModel(ModerationStore store, ModerationSettings settings, ClipPlayer player)
    {
        _store = store;
        Settings = settings;
        Player = player;

        RefreshCommand = new RelayCommand(_ => Run(RefreshAsync));
        SaveNameCommand = new RelayCommand(_ => Run(SaveNameAsync), _ => _detail != null);
        SaveNotesCommand = new RelayCommand(_ => Run(SaveNotesAsync), _ => _detail != null);
        _playSampleCommand = new RelayCommand(_ => Play(VoiceSample), _ => VoiceSample != null && Player.IsAvailable);
        PlayUtteranceCommand = new RelayCommand<UtteranceItem>(Play, u => u != null && Player.IsAvailable);
        StopCommand = new RelayCommand(Player.Stop);
        _moveCommand = new RelayCommand(_ => Run(MoveSelectedAppearanceAsync), _ => SelectedAppearance != null && MoveTarget != null);

        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModerationSettings.Sensitivity)) Run(RefreshAsync);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModerationSettings Settings { get; }
    public ClipPlayer Player { get; }

    public ObservableCollection<SpeakerItem> Speakers { get; } = [];
    public ObservableCollection<AppearanceItem> Appearances { get; } = [];
    public ObservableCollection<UtteranceItem> Incidents { get; } = [];
    public ObservableCollection<UtteranceItem> Transcript { get; } = [];
    public ObservableCollection<SpeakerChoice> MoveTargets { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand SaveNameCommand { get; }
    public ICommand SaveNotesCommand { get; }
    public ICommand PlaySampleCommand => _playSampleCommand;
    public ICommand PlayUtteranceCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand MoveAppearanceCommand => _moveCommand;

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value ?? string.Empty)) ApplyFilter(); }
    }

    public SpeakerItem? SelectedSpeaker
    {
        get => _selectedSpeaker;
        set
        {
            if (!SetField(ref _selectedSpeaker, value)) return;
            Run(LoadDetailAsync);
        }
    }

    /// <summary>The selected speaker as last loaded; null when nothing is selected.</summary>
    public SpeakerSummary? Detail
    {
        get => _detail;
        private set
        {
            if (!SetField(ref _detail, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(DetailSummaryText));
            OnPropertyChanged(nameof(ProfileText));
            ((RelayCommand)SaveNameCommand).RaiseCanExecuteChanged();
            ((RelayCommand)SaveNotesCommand).RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Detail != null;

    public string DetailSummaryText => Detail is { } d
        ? $"{d.ClipCount} clip(s) · {TimeFormat.Clock(d.TalkTimeSeconds)} talk time · {d.UtteranceCount} line(s) · {d.IncidentCount} incident(s)"
            + (d.FirstSeen is { } first && d.LastSeen is { } last
                ? $" · seen {first.ToLocalTime():yyyy-MM-dd} to {last.ToLocalTime():yyyy-MM-dd}"
                : string.Empty)
        : string.Empty;

    public string ProfileText => Detail?.LinkedProfileName is { } name ? $"Matches enrolled voice \"{name}\"" : string.Empty;

    public string EditName
    {
        get => _editName;
        set => SetField(ref _editName, value ?? string.Empty);
    }

    public string Notes
    {
        get => _notes;
        set => SetField(ref _notes, value ?? string.Empty);
    }

    /// <summary>The speaker's longest line, as a sample of their voice.</summary>
    public UtteranceItem? VoiceSample
    {
        get => _voiceSample;
        private set
        {
            if (SetField(ref _voiceSample, value)) _playSampleCommand.RaiseCanExecuteChanged();
        }
    }

    public AppearanceItem? SelectedAppearance
    {
        get => _selectedAppearance;
        set
        {
            if (SetField(ref _selectedAppearance, value)) _moveCommand.RaiseCanExecuteChanged();
        }
    }

    public SpeakerChoice? MoveTarget
    {
        get => _moveTarget;
        set
        {
            if (SetField(ref _moveTarget, value)) _moveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => _all.Count == 0;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public async Task RefreshAsync()
    {
        double sensitivity = Settings.Sensitivity;
        var speakers = await Task.Run(() => _store.GetSpeakersAsync(sensitivity));
        _all = speakers.Select(s => new SpeakerItem(s)).ToList();
        OnPropertyChanged(nameof(IsEmpty));
        ApplyFilter();
    }

    /// <summary>Selects a speaker (refreshing the list first), e.g. when opened from the Incidents or Clips page.</summary>
    public async Task ShowSpeakerAsync(long speakerId)
    {
        SearchText = string.Empty;
        await RefreshAsync();
        var item = Speakers.FirstOrDefault(s => s.Id == speakerId);
        if (item == null) return;
        _selectedSpeaker = item;
        OnPropertyChanged(nameof(SelectedSpeaker));
        await LoadDetailAsync();
    }

    private void ApplyFilter()
    {
        long? selectedId = SelectedSpeaker?.Id;
        IEnumerable<SpeakerItem> items = _all;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim();
            items = items.Where(s => s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Summary.Notes.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Speakers.Clear();
        foreach (var speaker in items) Speakers.Add(speaker);

        // Re-point the selection at the refreshed instance without reloading the page the user is editing.
        _selectedSpeaker = Speakers.FirstOrDefault(s => s.Id == selectedId);
        OnPropertyChanged(nameof(SelectedSpeaker));
        if (_selectedSpeaker == null) ClearDetail();
        else Detail = _selectedSpeaker.Summary;
        RefreshMoveTargets();
    }

    private async Task LoadDetailAsync()
    {
        int version = ++_loadVersion;
        if (SelectedSpeaker is not { } speaker)
        {
            ClearDetail();
            return;
        }

        double sensitivity = Settings.Sensitivity;
        var (summary, appearances, lines) = await Task.Run(async () => (
            await _store.GetSpeakerAsync(speaker.Id, sensitivity),
            await _store.GetAppearancesAsync(null, speaker.Id, sensitivity),
            await _store.GetUtterancesAsync(speakerId: speaker.Id)));
        if (version != _loadVersion) return; // a newer selection is loading
        if (summary == null)
        {
            ClearDetail();
            return;
        }

        Detail = summary;
        EditName = summary.CustomName ?? summary.DisplayName;
        Notes = summary.Notes;

        Appearances.Clear();
        foreach (var a in appearances) Appearances.Add(new AppearanceItem(a));
        SelectedAppearance = null;

        var items = lines.Select(u => new UtteranceItem(u, sensitivity)).ToList();
        Incidents.Clear();
        foreach (var i in items.Where(i => i.IsIncident).OrderByDescending(i => i.Severity)) Incidents.Add(i);
        Transcript.Clear();
        foreach (var t in items) Transcript.Add(t);
        VoiceSample = items.Where(i => i.Transcript.Length > 0)
            .OrderByDescending(i => i.Record.EndSeconds - i.Record.StartSeconds)
            .FirstOrDefault();

        RefreshMoveTargets();
    }

    private void ClearDetail()
    {
        Detail = null;
        EditName = string.Empty;
        Notes = string.Empty;
        VoiceSample = null;
        SelectedAppearance = null;
        Appearances.Clear();
        Incidents.Clear();
        Transcript.Clear();
    }

    private void RefreshMoveTargets()
    {
        long? current = Detail?.Id;
        MoveTargets.Clear();
        MoveTargets.Add(new SpeakerChoice(null, NewSpeakerChoice));
        foreach (var s in _all.Where(s => s.Id != current).OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            MoveTargets.Add(new SpeakerChoice(s.Id, s.DisplayName));
        }
        MoveTarget = null;
    }

    public async Task SaveNameAsync()
    {
        if (Detail is not { } d) return;
        // Clearing the box, or leaving the automatic name, reverts to the profile name or "Speaker N".
        string? name = string.IsNullOrWhiteSpace(EditName) || (d.CustomName == null && EditName.Trim() == d.DisplayName)
            ? null
            : EditName;
        await Task.Run(() => _store.RenameSpeakerAsync(d.Id, name));
        await RefreshAsync();
        await LoadDetailAsync();
        StatusMessage = "Name saved.";
    }

    public async Task SaveNotesAsync()
    {
        if (Detail is not { } d) return;
        string notes = Notes;
        await Task.Run(() => _store.SetSpeakerNotesAsync(d.Id, notes));
        await RefreshAsync();
        StatusMessage = "Notes saved.";
    }

    public async Task MoveSelectedAppearanceAsync()
    {
        if (SelectedAppearance is not { } appearance || MoveTarget is not { } target || Detail is not { } current) return;
        long movedTo = await Task.Run(() => _store.ReassignAppearanceAsync(appearance.Id, target.SpeakerId));
        StatusMessage = $"Moved {appearance.ClipName} ({appearance.LocalLabel}) to {(target.SpeakerId == null ? "a new speaker" : target.Name)}.";

        // The current speaker may have been removed if that was its only appearance; follow the clip instead.
        bool stillExists = await Task.Run(async () => await _store.GetSpeakerAsync(current.Id, Settings.Sensitivity) != null);
        await ShowSpeakerAsync(stillExists ? current.Id : movedTo);
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
            VoiceScanLogger.Error(nameof(SpeakersViewModel), "Speakers action failed", ex);
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
