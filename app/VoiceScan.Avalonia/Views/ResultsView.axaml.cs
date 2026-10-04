using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ResultsView : UserControl
{
    private ResultsViewModel? _subscribed;
    private bool _attached;

    private ResultsViewModel? ViewModel => DataContext as ResultsViewModel;

    public ResultsView()
    {
        InitializeComponent();
        FilterCombo.ItemsSource = Enum.GetValues<VerdictFilter>();
        SortCombo.ItemsSource = Enum.GetValues<ResultSortColumn>();
        Timeline.SeekRequested += (_, pos) => ViewModel?.SeekTo(pos);
        SizeChanged += (_, e) => ResponsiveLayout.SplitPanes(Split, e.NewSize.Width < ResponsiveLayout.StackedPageWidth,
            360, ListPane, DetailPane, EmptyText);
        DataContextChanged += (_, _) => Resubscribe();
    }

    // Pages are recreated on every navigation; subscribing only while attached keeps the long-lived view model
    // from accumulating handlers that hold on to (and keep updating) views that are no longer shown.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Resubscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Resubscribe();
    }

    private void Resubscribe()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = _attached ? ViewModel : null;
        if (_subscribed is not { } vm) return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        RefreshSelectedFile(vm);
        RefreshPlayState(vm);
        RefreshPosition(vm);
    }

    // Playback events arrive on a timer thread.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, _subscribed)) OnViewModelChanged(_subscribed!, e.PropertyName);
        });

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
                RefreshPosition(vm);
                break;
            case nameof(ResultsViewModel.IsPlaying):
                RefreshPlayState(vm);
                break;
            case nameof(ResultsViewModel.SelectedFile):
                RefreshSelectedFile(vm);
                break;
        }
    }

    private void RefreshPosition(ResultsViewModel vm)
    {
        Timeline.UpdatePlaybackCursor(vm.PlaybackPosition);
        var total = vm.SelectedFile?.DurationSeconds ?? 0.0;
        PositionText.Text = $"{TimeFormat.Clock(vm.PlaybackPosition)} / {TimeFormat.Clock(total)}";
    }

    private void RefreshPlayState(ResultsViewModel vm)
    {
        PlayPauseText.Text = vm.IsPlaying ? "Pause" : "Play";
        PlayPauseIcon.Data = (Avalonia.Media.Geometry)this.FindResource(vm.IsPlaying ? "IconPause" : "IconPlay")!;
    }

    private void RefreshSelectedFile(ResultsViewModel vm)
    {
        if (vm.SelectedFile is { } file)
        {
            Timeline.SetData(file.Waveform, file.Segments, file.DurationSeconds);
        }
    }

    private void PlayPause_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlayPause();

    private void PlaySelectedSegment_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedSegment is { } seg)
        {
            ViewModel.PlayHitSegment(seg);
        }
    }

    private void ScrubToSegmentStart_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedSegment is { } seg)
        {
            ViewModel.SeekToSegment(seg);
        }
    }

    private void Segments_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is HitSegmentResult segment)
        {
            ViewModel?.PlayHitSegment(segment);
        }
    }
}
