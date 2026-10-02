#if WINDOWS
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Views;

namespace VoiceScan.App;

public sealed partial class MainWindow : Window
{
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _configurationSource;

    public MainAppViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        TrySetMicaBackdrop();

        // Initialize Services & ViewModels through Service Locator / Factory
        ViewModel = AppServiceBootstrap.CreateMainViewModel();
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(ScanDashboardView), ViewModel.Scan);
    }

    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            this.SystemBackdrop = new MicaBackdrop();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag?.ToString())
            {
                case "Scan":
                    ContentFrame.Navigate(typeof(ScanDashboardView), ViewModel.Scan);
                    break;
                case "Results":
                    ContentFrame.Navigate(typeof(ResultsView), ViewModel.Results);
                    break;
                case "Review":
                    ContentFrame.Navigate(typeof(ReviewView), ViewModel.Review);
                    break;
                case "Enrollment":
                    ContentFrame.Navigate(typeof(EnrollmentWizardView), ViewModel.Enrollment);
                    break;
                case "Benchmark":
                    ContentFrame.Navigate(typeof(BenchmarkView), ViewModel.Benchmark);
                    break;
            }
        }
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = root.RequestedTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            ViewModel.ToggleTheme();
        }
    }
}
#endif
