using Avalonia.Controls;
using Avalonia.Interactivity;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ScanDashboardView : UserControl
{
    private ScanDashboardViewModel? ViewModel => DataContext as ScanDashboardViewModel;

    public ScanDashboardView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                TargetFolderInput.Text = vm.SelectedTargetFolderPath;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ScanDashboardViewModel.IsPaused))
                    {
                        PauseResumeText.Text = vm.IsPaused ? "Resume" : "Pause";
                        PauseResumeIcon.Data = (Avalonia.Media.Geometry)this.FindResource(
                            vm.IsPaused ? "IconPlay" : "IconPause")!;
                    }
                };
            }
        };
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = await FilePickers.PickFolderAsync(this, "Select recordings folder");
        if (path is null || ViewModel is null) return;
        TargetFolderInput.Text = path;
        ViewModel.SelectedTargetFolderPath = path;
    }

    private async void StartScan_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var folder = TargetFolderInput.Text?.Trim();
        if (folder != vm.SelectedTargetFolderPath) vm.SelectedTargetFolderPath = folder;

        await vm.StartScanAsync();
    }

    private void PauseResume_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (vm.IsPaused) vm.ResumeScan();
        else vm.PauseScan();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => ViewModel?.CancelScan();
}
