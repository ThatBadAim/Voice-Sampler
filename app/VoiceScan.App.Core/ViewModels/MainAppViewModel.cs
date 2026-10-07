using System.ComponentModel;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using System.Runtime.CompilerServices;

namespace VoiceScan.App.Core.ViewModels;

public enum AppNavigationPage
{
    Enrollment,
    Scan,
    Results,
    Review,
    Incidents,
    Clips,
    Speakers,
    Models
}

public sealed class MainAppViewModel : INotifyPropertyChanged
{
    private AppNavigationPage _currentPage;
    private AppTheme _theme;
    private readonly UserSettingsStore? _settings;
    private bool _isMicaBackdropEnabled = true;
    private string? _gpuStatusMessage;
    private string _windowTitle = "VoiceScan";

    public event PropertyChangedEventHandler? PropertyChanged;

    public EnrollmentWizardViewModel Enrollment { get; }
    public ScanDashboardViewModel Scan { get; }
    public ResultsViewModel Results { get; }
    public ReviewViewModel Review { get; }
    public IncidentsViewModel Incidents { get; }
    public ClipsViewModel Clips { get; }
    public SpeakersViewModel Speakers { get; }
    public ModelsViewModel Models { get; }

    public AppNavigationPage CurrentPage
    {
        get => _currentPage;
        set => SetField(ref _currentPage, value);
    }

    public IReadOnlyList<AppTheme> Themes => AppTheme.All;

    public AppTheme Theme
    {
        get => _theme;
        private set
        {
            if (SetField(ref _theme, value)) OnPropertyChanged(nameof(IsDarkTheme));
        }
    }

    public bool IsDarkTheme => _theme.IsDark;

    public bool IsMicaBackdropEnabled
    {
        get => _isMicaBackdropEnabled;
        set => SetField(ref _isMicaBackdropEnabled, value);
    }

    /// <summary>Non-null when inference is not running on the GPU; shown as a banner.</summary>
    public string? GpuStatusMessage
    {
        get => _gpuStatusMessage;
        set => SetField(ref _gpuStatusMessage, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetField(ref _windowTitle, value);
    }

    public MainAppViewModel(
        EnrollmentWizardViewModel enrollment,
        ScanDashboardViewModel scan,
        ResultsViewModel results,
        ReviewViewModel review,
        IncidentsViewModel incidents,
        ClipsViewModel clips,
        SpeakersViewModel speakers,
        ModelsViewModel models,
        UserSettingsStore? settings = null)
    {
        _settings = settings;
        _theme = AppTheme.Find(settings?.Current.ThemeId);
        Enrollment = enrollment;
        Scan = scan;
        Results = results;
        Review = review;
        Incidents = incidents;
        Clips = clips;
        Speakers = speakers;
        Models = models;

        Incidents.SpeakerRequested += OpenSpeaker;
        Clips.SpeakerRequested += OpenSpeaker;

        // First run: no saved profile yet, so start with enrollment.
        _currentPage = Scan.HasProfiles ? AppNavigationPage.Scan : AppNavigationPage.Enrollment;

        Enrollment.ProfileCreated += summary =>
        {
            Scan.RefreshProfiles(summary.Path);
            NavigateTo(AppNavigationPage.Scan);
            Enrollment.Reset();
        };

        Enrollment.ProfilesChanged += () => Scan.RefreshProfiles();

        Scan.ScanStarted += settings => Results.BeginScan(settings);

        Scan.ScanFinished += () =>
        {
            if (Scan.CompletedFiles.Count > 0) NavigateTo(AppNavigationPage.Results);
            RefreshModerationPages();
        };

        // Auto-transfer completed scan results to Results and Review views
        Scan.CompletedFiles.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<Models.FileVerdictResult>())
                {
                    Results.AddResult(item);

                    // Review verifies the target voice (confirming adds the segment to the profile), so offensive lines
                    // from other speakers belong on the Incidents page, not here.
                    var reviewCandidates = item.Segments.Where(seg =>
                        seg.Verdict.Equals("Possible", StringComparison.OrdinalIgnoreCase) ||
                        seg.ReasonFlags.Any(f => f != "OFFENSIVE_CONTENT"));

                    Review.EnqueueSegments(reviewCandidates, item.ProfileName ?? "Unknown profile", item.FileName, item.ProfilePath);
                }
            }
        };
    }

    public void NavigateTo(AppNavigationPage page)
    {
        CurrentPage = page;
        // The moderation pages read the database, which scans update in the background.
        switch (page)
        {
            case AppNavigationPage.Incidents: Incidents.RefreshCommand.Execute(null); break;
            case AppNavigationPage.Clips: Clips.RefreshCommand.Execute(null); break;
            case AppNavigationPage.Speakers: Speakers.RefreshCommand.Execute(null); break;
            case AppNavigationPage.Models: Models.RefreshCommand.Execute(null); break;
        }
    }

    private async void OpenSpeaker(long speakerId)
    {
        CurrentPage = AppNavigationPage.Speakers;
        try
        {
            await Speakers.ShowSpeakerAsync(speakerId);
        }
        catch (Exception ex)
        {
            VoiceScan.Core.Logging.VoiceScanLogger.Error(nameof(MainAppViewModel), $"Could not open speaker {speakerId}", ex);
        }
    }

    /// <summary>After the voice model is switched: profile compatibility, default thresholds and the moderation pages change.</summary>
    public void OnEmbeddingModelChanged(string? gpuStatusMessage)
    {
        GpuStatusMessage = gpuStatusMessage;
        Scan.RefreshProfiles();
        Scan.OnEmbeddingModelChanged();
        RefreshModerationPages();
    }

    private void RefreshModerationPages()
    {
        Incidents.RefreshCommand.Execute(null);
        Clips.RefreshCommand.Execute(null);
        Speakers.RefreshCommand.Execute(null);
    }

    public void SelectTheme(string themeId)
    {
        Theme = AppTheme.Find(themeId);
        if (_settings is null) return;
        _settings.Current.ThemeId = Theme.Id;
        _settings.Save();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        return false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
