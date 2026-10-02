#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App.Views;

public sealed partial class ReviewView : Page
{
    public ReviewViewModel? ViewModel { get; private set; }

    public ReviewView()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ReviewViewModel vm)
        {
            ViewModel = vm;
            QueueListView.ItemsSource = ViewModel.PendingQueue;
            HistoryListView.ItemsSource = ViewModel.DecisionHistory;
            await ViewModel.InitializeAsync();
        }
        base.OnNavigatedTo(e);
    }

    private void QueueListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel != null && QueueListView.SelectedItem is ReviewQueueItem item)
        {
            ViewModel.SelectedItem = item;
            SelectedSegmentTitleText.Text = $"{item.FileName} [{item.Segment.StartTimeSeconds:F1}s - {item.Segment.EndTimeSeconds:F1}s]";
            SelectedSegmentDetailText.Text = $"Target: {item.ProfileName} | Duration: {item.Segment.DurationSeconds:F1}s";
            ConfidenceText.Text = $"{item.Segment.Confidence:F3}";

            ReasonFlagsPanel.Children.Clear();
            foreach (var flag in item.Segment.ReasonFlags)
            {
                var border = new Border
                {
                    Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(50, 216, 59, 1)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2)
                };
                border.Child = new TextBlock
                {
                    Text = flag,
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["VerdictPossibleBrush"]
                };
                ReasonFlagsPanel.Children.Add(border);
            }

            PlaySnippetButton.IsEnabled = true;
            ConfirmButton.IsEnabled = true;
            RejectButton.IsEnabled = true;
            ActionStatusText.Text = "";
        }
    }

    private void PlaySnippetButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.PlaySelectedSnippet();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        ViewModel.ReviewerNotes = ReviewNotesInput.Text;
        ViewModel.AddToProfileOnConfirm = AddToProfileCheckBox.IsChecked == true;

        ConfirmButton.IsEnabled = false;
        RejectButton.IsEnabled = false;

        await ViewModel.ConfirmSegmentAsync();

        ActionStatusText.Text = ViewModel.StatusMessage ?? "Segment confirmed.";
        ResetSelection();
    }

    private async void RejectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        ViewModel.ReviewerNotes = ReviewNotesInput.Text;

        ConfirmButton.IsEnabled = false;
        RejectButton.IsEnabled = false;

        await ViewModel.RejectSegmentAsync();

        ActionStatusText.Text = ViewModel.StatusMessage ?? "Segment rejected and recorded as negative.";
        ResetSelection();
    }

    private void ResetSelection()
    {
        ReviewNotesInput.Text = "";
        PlaySnippetButton.IsEnabled = false;
        ConfirmButton.IsEnabled = false;
        RejectButton.IsEnabled = false;
        SelectedSegmentTitleText.Text = "Select a segment from the review queue.";
        SelectedSegmentDetailText.Text = "";
        ConfidenceText.Text = "-- ";
        ReasonFlagsPanel.Children.Clear();
    }
}
#endif
