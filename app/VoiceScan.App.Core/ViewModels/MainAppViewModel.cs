using System.ComponentModel;
using VoiceScan.App.Core.Services;
using VoiceScan.Core;
using System.Runtime.CompilerServices;

namespace VoiceScan.App.Core.ViewModels;

public enum AppNavigationPage
{
    Enrollment,
    Scan,
    Results,
    Review
}

public sealed class MainAppViewModel : INotifyPropertyChanged
{
    private AppNavigationPage _currentPage;
    private bool _isDarkTheme = true;
    private bool _isMicaBackdropEnabled = true;
    private string? _gpuStatusMessage;
    private string _windowTitle = "VoiceScan";

    public event PropertyChangedEventHandler? PropertyChanged;

    public EnrollmentWizardViewModel Enrollment { get; }
    public ScanDashboardViewModel Scan { get; }
    public ResultsViewModel Results { get; }
    public ReviewViewModel Review { get; }

    public AppNavigationPage CurrentPage
    {
        get => _currentPage;
        set => SetField(ref _currentPage, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => SetField(ref _isDarkTheme, value);
    }

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
        ReviewViewModel review)
    {
        Enrollment = enrollment;
        Scan = scan;
        Results = results;
        Review = review;

        // First run: no saved profile yet, so start with enrollment.
        _currentPage = Scan.HasProfiles ? AppNavigationPage.Scan : AppNavigationPage.Enrollment;

        Enrollment.ProfileCreated += summary =>
        {
            Scan.RefreshProfiles(summary.Path);
            NavigateTo(AppNavigationPage.Scan);
            Enrollment.Reset();
        };

        Results.ExportSettingsProvider = BuildExportSettings;

        Enrollment.ProfilesChanged += () => Scan.RefreshProfiles();

        Scan.ScanFinished += () =>
        {
            if (Scan.CompletedFiles.Count > 0) NavigateTo(AppNavigationPage.Results);
        };

        // Auto-transfer completed scan results to Results and Review views
        Scan.CompletedFiles.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<Models.FileVerdictResult>())
                {
                    Results.AddResult(item);

                    // Add segments needing review (Possible or Match with reason flags) to Review queue
                    var reviewCandidates = item.Segments.Where(seg =>
                        seg.Verdict.Equals("Possible", StringComparison.OrdinalIgnoreCase) ||
                        seg.ReasonFlags.Count > 0);

                    Review.EnqueueSegments(reviewCandidates, Scan.SelectedProfilePath != null ? Path.GetFileNameWithoutExtension(Scan.SelectedProfilePath) : "UnknownProfile", item.FileName);
                }
            }
        };
    }

    private ReportExportSettings BuildExportSettings()
    {
        string modelId = "wespeaker-resnet34";
        try
        {
            if (Scan.SelectedProfilePath is { } path) modelId = VoiceProfile.LoadFromFile(path).ModelId;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // The report still records the default model id when the profile can no longer be read.
        }

        return new ReportExportSettings(
            ProfileName: Scan.SelectedProfile?.Name ?? "Unknown",
            ModelId: modelId,
            EngineVersion: typeof(VoiceProfile).Assembly.GetName().Version?.ToString() ?? "0.1.0",
            Threshold: new PipelineScanOptions().Threshold,
            ClusterThreshold: Scan.ClusterThreshold,
            TemporalSmoothing: Scan.UseTemporalSmoothing,
            ScanDateUtc: DateTimeOffset.UtcNow);
    }

    public void NavigateTo(AppNavigationPage page)
    {
        CurrentPage = page;
    }

    public void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
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
