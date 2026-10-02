using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoiceScan.App.Core.ViewModels;

public enum AppNavigationPage
{
    Enrollment,
    Scan,
    Results,
    Review,
    Benchmark
}

public sealed class MainAppViewModel : INotifyPropertyChanged
{
    private AppNavigationPage _currentPage = AppNavigationPage.Scan;
    private bool _isDarkTheme = true;
    private bool _isMicaBackdropEnabled = true;
    private string _windowTitle = "VoiceScan — Biometric Voice Locator (100% Offline)";

    public event PropertyChangedEventHandler? PropertyChanged;

    public EnrollmentWizardViewModel Enrollment { get; }
    public ScanDashboardViewModel Scan { get; }
    public ResultsViewModel Results { get; }
    public ReviewViewModel Review { get; }
    public BenchmarkViewModel Benchmark { get; }

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
        BenchmarkViewModel benchmark)
    {
        Enrollment = enrollment;
        Scan = scan;
        Results = results;
        Review = review;
        Benchmark = benchmark;

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
