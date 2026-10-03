using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App;

public partial class MainWindow : Window
{
    private readonly MainAppViewModel _viewModel;
    private readonly Dictionary<AppNavigationPage, Button> _navButtons;

    // Needed by the XAML loader / previewer.
    public MainWindow() : this(AppServiceBootstrap.CreateMainViewModel()) { }

    public MainWindow(MainAppViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _navButtons = new()
        {
            [AppNavigationPage.Scan] = NavScan,
            [AppNavigationPage.Results] = NavResults,
            [AppNavigationPage.Review] = NavReview,
            [AppNavigationPage.Enrollment] = NavEnrollment,
        };

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainAppViewModel.CurrentPage)) ShowCurrentPage();
            if (e.PropertyName == nameof(MainAppViewModel.IsDarkTheme)) ApplyTheme();
        };

        ApplyTheme();
        ShowCurrentPage();
    }

    private void ShowCurrentPage()
    {
        PageHost.Content = _viewModel.CurrentPage switch
        {
            AppNavigationPage.Scan => _viewModel.Scan,
            AppNavigationPage.Results => _viewModel.Results,
            AppNavigationPage.Review => _viewModel.Review,
            AppNavigationPage.Enrollment => _viewModel.Enrollment,
            _ => _viewModel.Scan
        };

        foreach (var (page, button) in _navButtons)
        {
            button.Classes.Set("active", page == _viewModel.CurrentPage);
        }
    }

    private void ApplyTheme() =>
        Application.Current!.RequestedThemeVariant =
            _viewModel.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<AppNavigationPage>(tag, out var page))
        {
            _viewModel.NavigateTo(page);
        }
    }

    private void ThemeToggle_Click(object? sender, RoutedEventArgs e) => _viewModel.ToggleTheme();
}
