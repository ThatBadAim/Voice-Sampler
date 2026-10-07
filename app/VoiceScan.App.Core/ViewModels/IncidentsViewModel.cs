using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core.Logging;
using VoiceScan.Core.Storage;

namespace VoiceScan.App.Core.ViewModels;

/// <summary>Every potential offence across all analysed clips: sort, filter, listen back and mark as reviewed.</summary>
public sealed class IncidentsViewModel : INotifyPropertyChanged
{
    public const string AllCategories = "All categories";
    public const string AllSpeakers = "All speakers";
    public const string AllStatuses = "All statuses";

    public static IReadOnlyList<string> StatusFilters { get; } =
        [AllStatuses, nameof(IncidentStatus.Unreviewed), nameof(IncidentStatus.Confirmed), nameof(IncidentStatus.Dismissed)];

    public static IReadOnlyList<IncidentSortColumn> SortColumns { get; } = Enum.GetValues<IncidentSortColumn>();

    private readonly ModerationStore _store;
    private readonly RelayCommand _confirmCommand;
    private readonly RelayCommand _dismissCommand;
    private readonly RelayCommand _resetCommand;
    private readonly RelayCommand _playCommand;
    private readonly RelayCommand _openSpeakerCommand;
    private List<UtteranceRecord> _all = [];
    private string _categoryFilter = AllCategories;
    private string _speakerFilter = AllSpeakers;
    private string _statusFilter = AllStatuses;
    private string _searchText = string.Empty;
    private IncidentSortColumn _sortColumn = IncidentSortColumn.Score;
    private bool _sortDescending = true;
    private UtteranceItem? _selectedIncident;
    private string _reviewNote = string.Empty;
    private string _newPhrase = string.Empty;
    private string _newCategory = string.Empty;
    private string? _statusMessage;
    private bool _isBusy;
    private int _unreviewedCount;

    public IncidentsViewModel(ModerationStore store, ModerationSettings settings, ClipPlayer player)
    {
        _store = store;
        Settings = settings;
        Player = player;

        _confirmCommand = new RelayCommand(_ => Run(() => ReviewSelectedAsync(IncidentStatus.Confirmed)), _ => SelectedIncident != null && !IsBusy);
        _dismissCommand = new RelayCommand(_ => Run(() => ReviewSelectedAsync(IncidentStatus.Dismissed)), _ => SelectedIncident != null && !IsBusy);
        _resetCommand = new RelayCommand(_ => Run(() => ReviewSelectedAsync(IncidentStatus.Unreviewed)), _ => SelectedIncident != null && !IsBusy);
        _playCommand = new RelayCommand(_ => PlaySelected(), _ => SelectedIncident != null && Player.IsAvailable);
        _openSpeakerCommand = new RelayCommand(_ => SpeakerRequested?.Invoke(SelectedIncident!.Record.SpeakerId), _ => SelectedIncident != null);
        StopCommand = new RelayCommand(Player.Stop);
        RefreshCommand = new RelayCommand(_ => Run(RefreshAsync));
        ToggleSortDirectionCommand = new RelayCommand(() => SortDescending = !SortDescending);
        AddWordCommand = new RelayCommand(_ => Run(AddWordAsync));
        RemoveWordCommand = new RelayCommand<WordListEntry>(entry => { if (entry != null) Run(() => RemoveWordAsync(entry)); });

        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModerationSettings.Sensitivity)) Run(RefreshAsync);
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised with a speaker id when the user opens the speaker of the selected incident.</summary>
    public event Action<long>? SpeakerRequested;

    public ModerationSettings Settings { get; }
    public ClipPlayer Player { get; }

    public ObservableCollection<UtteranceItem> Incidents { get; } = [];
    public ObservableCollection<string> Categories { get; } = [AllCategories];
    public ObservableCollection<string> Speakers { get; } = [AllSpeakers];
    public ObservableCollection<WordListEntry> WordList { get; } = [];

    public ICommand ConfirmCommand => _confirmCommand;
    public ICommand DismissCommand => _dismissCommand;
    public ICommand ResetCommand => _resetCommand;
    public ICommand PlayCommand => _playCommand;
    public ICommand OpenSpeakerCommand => _openSpeakerCommand;
    public ICommand StopCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleSortDirectionCommand { get; }
    public ICommand AddWordCommand { get; }
    public ICommand RemoveWordCommand { get; }

    public string CategoryFilter
    {
        get => _categoryFilter;
        set { if (SetField(ref _categoryFilter, value ?? AllCategories)) ApplyFilter(); }
    }

    public string SpeakerFilter
    {
        get => _speakerFilter;
        set { if (SetField(ref _speakerFilter, value ?? AllSpeakers)) ApplyFilter(); }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set { if (SetField(ref _statusFilter, value ?? AllStatuses)) ApplyFilter(); }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (SetField(ref _searchText, value ?? string.Empty)) ApplyFilter(); }
    }

    public IncidentSortColumn SortColumn
    {
        get => _sortColumn;
        set { if (SetField(ref _sortColumn, value)) ApplyFilter(); }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (SetField(ref _sortDescending, value))
            {
                OnPropertyChanged(nameof(SortDirectionText));
                ApplyFilter();
            }
        }
    }

    public string SortDirectionText => SortDescending ? "Descending" : "Ascending";

    public UtteranceItem? SelectedIncident
    {
        get => _selectedIncident;
        set
        {
            if (!SetField(ref _selectedIncident, value)) return;
            ReviewNote = value?.Note ?? string.Empty;
            OnPropertyChanged(nameof(HasSelection));
            RaiseCanExecute();
        }
    }

    public bool HasSelection => SelectedIncident != null;

    public string ReviewNote
    {
        get => _reviewNote;
        set => SetField(ref _reviewNote, value ?? string.Empty);
    }

    public string NewPhrase
    {
        get => _newPhrase;
        set => SetField(ref _newPhrase, value ?? string.Empty);
    }

    public string NewCategory
    {
        get => _newCategory;
        set => SetField(ref _newCategory, value ?? string.Empty);
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
            if (SetField(ref _isBusy, value)) RaiseCanExecute();
        }
    }

    public int TotalCount => _all.Count;
    public int ShownCount => Incidents.Count;

    public int UnreviewedCount
    {
        get => _unreviewedCount;
        private set => SetField(ref _unreviewedCount, value);
    }

    public bool IsEmpty => _all.Count == 0;

    public async Task RefreshAsync()
    {
        double sensitivity = Settings.Sensitivity;
        var (incidents, words) = await Task.Run(async () =>
            (await _store.GetUtterancesAsync(incidentSensitivity: sensitivity), await _store.GetWordListAsync()));

        _all = incidents.ToList();
        UnreviewedCount = _all.Count(i => i.Status == IncidentStatus.Unreviewed);
        Replace(WordList, words);
        Replace(Categories, new[] { AllCategories }.Concat(_all
            .SelectMany(i => i.Categories(sensitivity)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)));
        Replace(Speakers, new[] { AllSpeakers }.Concat(_all.Select(i => i.SpeakerName).Distinct().Order(StringComparer.OrdinalIgnoreCase)));
        if (!Categories.Contains(CategoryFilter)) CategoryFilter = AllCategories;
        if (!Speakers.Contains(SpeakerFilter)) SpeakerFilter = AllSpeakers;

        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyFilter()
    {
        double sensitivity = Settings.Sensitivity;
        long? selectedId = SelectedIncident?.Id;
        IEnumerable<UtteranceItem> items = _all.Select(r => new UtteranceItem(r, sensitivity));

        if (CategoryFilter != AllCategories)
            items = items.Where(i => i.Categories.Contains(CategoryFilter, StringComparer.OrdinalIgnoreCase));
        if (SpeakerFilter != AllSpeakers)
            items = items.Where(i => i.SpeakerName == SpeakerFilter);
        if (StatusFilter != AllStatuses && Enum.TryParse<IncidentStatus>(StatusFilter, out var status))
            items = items.Where(i => i.Record.Status == status);
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string q = SearchText.Trim();
            items = items.Where(i => i.Transcript.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.ClipName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.SpeakerName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Note.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Replace(Incidents, Sort(items));
        OnPropertyChanged(nameof(ShownCount));
        SelectedIncident = Incidents.FirstOrDefault(i => i.Id == selectedId);
    }

    private IEnumerable<UtteranceItem> Sort(IEnumerable<UtteranceItem> items)
    {
        IOrderedEnumerable<UtteranceItem> ordered = SortColumn switch
        {
            IncidentSortColumn.Clip => By(items, i => i.ClipName, StringComparer.OrdinalIgnoreCase),
            IncidentSortColumn.Time => By(items, i => i.Record.RecordedAt ?? DateTimeOffset.MinValue),
            IncidentSortColumn.Speaker => By(items, i => i.SpeakerName, StringComparer.OrdinalIgnoreCase),
            IncidentSortColumn.Category => By(items, i => i.Categories.FirstOrDefault() ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            IncidentSortColumn.Status => By(items, i => i.Record.Status),
            _ => By(items, i => i.Severity),
        };
        // Ties keep a stable, readable order: within a clip, by time.
        return ordered.ThenBy(i => i.ClipName, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Record.StartSeconds);
    }

    private IOrderedEnumerable<UtteranceItem> By<TKey>(IEnumerable<UtteranceItem> items, Func<UtteranceItem, TKey> key, IComparer<TKey>? comparer = null) =>
        SortDescending ? items.OrderByDescending(key, comparer) : items.OrderBy(key, comparer);

    public async Task ReviewSelectedAsync(IncidentStatus status)
    {
        if (SelectedIncident is not { } item) return;
        int index = Incidents.IndexOf(item);
        await Task.Run(() => _store.SetIncidentReviewAsync(item.Id, status, ReviewNote.Trim()));
        await RefreshAsync();

        // Move on to the next line so a reviewer can work down the list.
        int stillThere = Incidents.ToList().FindIndex(i => i.Id == item.Id);
        int next = stillThere >= 0 ? stillThere + 1 : index;
        SelectedIncident = next >= 0 && next < Incidents.Count ? Incidents[next]
            : stillThere >= 0 ? Incidents[stillThere] : Incidents.LastOrDefault();
        StatusMessage = $"Marked as {status}.";
    }

    private void PlaySelected()
    {
        if (SelectedIncident is not { } item) return;
        var r = item.Record;
        if (!File.Exists(r.FilePath))
        {
            StatusMessage = $"Recording not found: {r.FilePath}";
            return;
        }
        Player.Play(r.Id, r.FilePath, r.ClipDurationSeconds, r.StartSeconds, r.EndSeconds);
    }

    public async Task AddWordAsync()
    {
        string phrase = NewPhrase.Trim();
        if (phrase.Length == 0) return;
        string category = NewCategory.Trim();
        await Task.Run(() => _store.AddWordAsync(phrase, category));
        NewPhrase = string.Empty;
        await RefreshAsync();
        StatusMessage = $"Added \"{phrase}\" to the word list.";
    }

    public async Task RemoveWordAsync(WordListEntry entry)
    {
        await Task.Run(() => _store.RemoveWordAsync(entry.Id));
        await RefreshAsync();
        StatusMessage = $"Removed \"{entry.Phrase}\" from the word list.";
    }

    /// <summary>Runs a UI action; failures are shown and logged rather than escaping an async void handler.</summary>
    private async void Run(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            VoiceScanLogger.Error(nameof(IncidentsViewModel), "Moderation action failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCanExecute()
    {
        _confirmCommand.RaiseCanExecuteChanged();
        _dismissCommand.RaiseCanExecuteChanged();
        _resetCommand.RaiseCanExecuteChanged();
        _playCommand.RaiseCanExecuteChanged();
        _openSpeakerCommand.RaiseCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
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
