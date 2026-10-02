#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App.Views;

public sealed partial class ResultsView : Page
{
    public ResultsViewModel? ViewModel { get; private set; }

    public ResultsView()
    {
        this.InitializeComponent();
        TimelineControl.SeekRequested += (s, pos) => ViewModel?.SeekTo(pos);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ResultsViewModel vm)
        {
            ViewModel = vm;
            FilesListView.ItemsSource = ViewModel.FilteredFiles;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
        base.OnNavigatedTo(e);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel == null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (e.PropertyName == nameof(ViewModel.PlaybackPosition))
            {
                TimelineControl.UpdatePlaybackCursor(ViewModel.PlaybackPosition);
                double total = ViewModel.SelectedFile?.DurationSeconds ?? 0.0;
                PlaybackPositionText.Text = $"{TimeSpan.FromSeconds(ViewModel.PlaybackPosition):mm\\:ss} / {TimeSpan.FromSeconds(total):mm\\:ss}";
            }
            else if (e.PropertyName == nameof(ViewModel.IsPlaying))
            {
                PlayPauseButton.Content = ViewModel.IsPlaying ? "⏸️ Pause" : "▶️ Play";
            }
            else if (e.PropertyName == nameof(ViewModel.SelectedFile))
            {
                UpdateSelectedFileUI();
            }
        });
    }

    private void UpdateSelectedFileUI()
    {
        if (ViewModel?.SelectedFile == null) return;

        var file = ViewModel.SelectedFile;
        SelectedFileNameText.Text = file.FileName;
        SelectedFilePathText.Text = file.FilePath;
        SelectedVerdictText.Text = file.OverallVerdict;
        SegmentsListView.ItemsSource = file.Segments;

        TimelineControl.SetData(file.Waveform, file.Segments, file.DurationSeconds);
    }

    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && FilesListView.SelectedItem is FileVerdictResult file)
        {
            ViewModel.SelectedFile = file;
        }
    }

    private void SegmentsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && SegmentsListView.SelectedItem is HitSegmentResult segment)
        {
            ViewModel.PlayHitSegment(segment);
        }
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.PlayPause();
    }

    private void PlaySegmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HitSegmentResult segment)
        {
            ViewModel?.PlayHitSegment(segment);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SearchQuery = SearchBox.Text;
        }
    }

    private void VerdictFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && VerdictFilterComboBox.SelectedItem is ComboBoxItem item && Enum.TryParse<VerdictFilter>(item.Tag?.ToString(), out var filter))
        {
            ViewModel.SelectedFilter = filter;
        }
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && SortComboBox.SelectedItem is ComboBoxItem item && Enum.TryParse<ResultSortColumn>(item.Tag?.ToString(), out var sort))
        {
            ViewModel.SelectedSort = sort;
        }
    }
}
#endif
