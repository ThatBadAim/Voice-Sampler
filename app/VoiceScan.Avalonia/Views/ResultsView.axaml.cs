using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ResultsView : UserControl
{
    private ResultsViewModel? ViewModel => DataContext as ResultsViewModel;

    public ResultsView()
    {
        InitializeComponent();
        FilterCombo.ItemsSource = Enum.GetValues<VerdictFilter>();
        SortCombo.ItemsSource = Enum.GetValues<ResultSortColumn>();
        Timeline.SeekRequested += (_, pos) => ViewModel?.SeekTo(pos);

        DataContextChanged += (_, _) =>
        {
            if (ViewModel is not { } vm) return;
            vm.PropertyChanged += (_, e) => Dispatcher.UIThread.Post(() => OnViewModelChanged(vm, e.PropertyName));
            RefreshSelectedFile(vm);
        };
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await FilePickers.PickFolderAsync(this, "Folder for the report");
        if (folder is not null && ViewModel is { } vm) await vm.ExportReportAsync(folder);
    }

    private void OnViewModelChanged(ResultsViewModel vm, string? property)
    {
        switch (property)
        {
            case nameof(ResultsViewModel.PlaybackPosition):
                Timeline.UpdatePlaybackCursor(vm.PlaybackPosition);
                var total = vm.SelectedFile?.DurationSeconds ?? 0.0;
                PositionText.Text = $"{TimeSpan.FromSeconds(vm.PlaybackPosition):mm\\:ss} / {TimeSpan.FromSeconds(total):mm\\:ss}";
                break;
            case nameof(ResultsViewModel.IsPlaying):
                {
                    PlayPauseText.Text = vm.IsPlaying ? "Pause" : "Play";
                    PlayPauseIcon.Data = (Avalonia.Media.Geometry)this.FindResource(
                        vm.IsPlaying ? "IconPause" : "IconPlay")!;
                }
                break;
            case nameof(ResultsViewModel.SelectedFile):
                RefreshSelectedFile(vm);
                break;
        }
    }

    private void RefreshSelectedFile(ResultsViewModel vm)
    {
        if (vm.SelectedFile is { } file)
        {
            Timeline.SetData(file.Waveform, file.Segments, file.DurationSeconds);
        }
    }

    private void PlayPause_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlayPause();

    private void Segments_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is HitSegmentResult segment)
        {
            ViewModel?.PlayHitSegment(segment);
        }
    }
}
