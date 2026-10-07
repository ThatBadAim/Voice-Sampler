using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;
using VoiceScan.App.Styles;

namespace VoiceScan.App;

public partial class MainWindow : Window
{
    private readonly MainAppViewModel _viewModel;
    private readonly Dictionary<AppNavigationPage, Button> _navButtons;

    // Needed by the XAML loader / previewer.
    public MainWindow() : this(AppServiceBootstrap.CreateMainViewModel(AppServiceBootstrap.LoadEmbeddingModel())) { }

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
            [AppNavigationPage.Incidents] = NavIncidents,
            [AppNavigationPage.Clips] = NavClips,
            [AppNavigationPage.Speakers] = NavSpeakers,
            [AppNavigationPage.Models] = NavModels,
        };

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainAppViewModel.CurrentPage)) ShowCurrentPage();
            if (e.PropertyName == nameof(MainAppViewModel.Theme)) ApplyTheme();
        };

        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        NavStack.SizeChanged += (_, _) => MoveNavIndicator();

        ApplyTheme();
        ShowCurrentPage();
    }

    private void ApplyLayout(double width)
    {
        bool compact = width < ResponsiveLayout.CompactWindowWidth;
        Classes.Set("compact", compact);
        Sidebar.Width = compact ? 76 : 264;
        Sidebar.Padding = compact ? new Thickness(8, 32, 8, 24) : new Thickness(16, 32, 16, 24);
        Wordmark.IsVisible = !compact;
        PageHost.Margin = new Thickness(compact ? 24 : 48);
    }

    private void ShowCurrentPage()
    {
        PageHost.Content = _viewModel.CurrentPage switch
        {
            AppNavigationPage.Scan => _viewModel.Scan,
            AppNavigationPage.Results => _viewModel.Results,
            AppNavigationPage.Review => _viewModel.Review,
            AppNavigationPage.Enrollment => _viewModel.Enrollment,
            AppNavigationPage.Incidents => _viewModel.Incidents,
            AppNavigationPage.Clips => _viewModel.Clips,
            AppNavigationPage.Speakers => _viewModel.Speakers,
            AppNavigationPage.Models => _viewModel.Models,
            _ => _viewModel.Scan
        };

        foreach (var (page, button) in _navButtons)
        {
            button.Classes.Set("active", page == _viewModel.CurrentPage);
        }

        Dispatcher.UIThread.Post(MoveNavIndicator, DispatcherPriority.Loaded);
    }

    private void MoveNavIndicator()
    {
        var button = _navButtons[_viewModel.CurrentPage];
        if (button.TranslatePoint(default, NavStack) is not { } origin || button.Bounds.Height <= 0) return;

        NavIndicator.Height = button.Bounds.Height;
        NavIndicator.RenderTransform = TransformOperations.Parse($"translateY({origin.Y.ToString(CultureInfo.InvariantCulture)}px)");

        // Placed once without animation so the indicator doesn't slide in from the top on startup.
        NavIndicator.Transitions ??=
        [
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() },
            new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() }
        ];
    }

    private void ApplyTheme()
    {
        Application.Current!.RequestedThemeVariant = AppThemeVariants.For(_viewModel.Theme);
    }

    // Brushes swap instantly, so dip the content's opacity around the swap to make the change read as a fade.
    private async void ThemeOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || id == _viewModel.Theme.Id) return;
        RootPanel.Opacity = 0.35;
        await Task.Delay(120);
        _viewModel.SelectTheme(id);
        RootPanel.Opacity = 1;
    }

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<AppNavigationPage>(tag, out var page))
        {
            _viewModel.NavigateTo(page);
        }
    }
}
