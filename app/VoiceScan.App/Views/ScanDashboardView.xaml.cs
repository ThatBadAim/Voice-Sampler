#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App.Views;

public sealed partial class ScanDashboardView : Page
{
    public ScanDashboardViewModel? ViewModel { get; private set; }

    public ScanDashboardView()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ScanDashboardViewModel vm)
        {
            ViewModel = vm;
            ScannedFilesListView.ItemsSource = ViewModel.CompletedFiles;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
        base.OnNavigatedTo(e);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel == null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName == nameof(ViewModel.Progress))
            {
                var p = ViewModel.Progress;
                OverallProgressBar.Value = p.OverallProgressPercent;
                OverallPercentText.Text = $"{p.OverallProgressPercent:F0}%";
                SpeedMultipleText.Text = $"{p.RealtimeMultiple:F1}x";
                EtaText.Text = p.EstimatedTimeRemaining.HasValue ? $"{p.EstimatedTimeRemaining.Value:mm\\:ss}" : "--:--";
                GpuUtilText.Text = $"{p.GpuUtilizationPercent:F0} %";
                FilesCompletedText.Text = $"{p.ProcessedFiles} / {p.TotalFiles}";
                CurrentFileStatusText.Text = p.CurrentFileName != null ? $"Scanning: {p.CurrentFileName}" : "Ready.";

                MatchCountText.Text = p.TotalMatchesFound.ToString();
                PossibleCountText.Text = p.TotalPossibleFound.ToString();
                NoMatchCountText.Text = p.TotalNoMatchFound.ToString();
            }
            else if (e.PropertyName == nameof(ViewModel.IsScanning) || e.PropertyName == nameof(ViewModel.IsPaused))
            {
                StartScanButton.IsEnabled = !ViewModel.IsScanning;
                CancelButton.IsEnabled = ViewModel.IsScanning;
                PauseResumeButton.IsEnabled = ViewModel.IsScanning;
                PauseResumeButton.Content = ViewModel.IsPaused ? "▶️ Resume" : "⏸️ Pause";
            }
        });
    }

    private async void StartScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;

        ViewModel.SelectedTargetFolderPath = TargetFolderInput.Text.Trim();
        ViewModel.SelectedProfilePath = ProfilePathInput.Text.Trim();
        ViewModel.UseClustering = ClusteringCheckBox.IsChecked == true;
        ViewModel.UseTemporalSmoothing = TemporalSmoothingCheckBox.IsChecked == true;

        await ViewModel.StartScanAsync();
    }

    private void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        if (ViewModel.IsPaused) ViewModel.ResumeScan();
        else ViewModel.PauseScan();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.CancelScan();
    }
}
#endif
