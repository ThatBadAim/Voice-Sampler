using Avalonia.Controls;
using Avalonia.Interactivity;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ReviewView : UserControl
{
    private ReviewViewModel? ViewModel => DataContext as ReviewViewModel;

    public ReviewView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ResponsiveLayout.SplitPanes(Split, e.NewSize.Width < ResponsiveLayout.StackedPageWidth,
            380, ListPane, DetailPane);
        DataContextChanged += async (_, _) =>
        {
            if (ViewModel is { } vm) await vm.InitializeAsync();
        };
    }

    private void PlaySnippet_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlaySelectedSnippet();

    private void ScrubToStart_Click(object? sender, RoutedEventArgs e) => ViewModel?.SeekToSelectedStart();

    private void Queue_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is ReviewQueueItem)
        {
            ViewModel?.SeekToSelectedStart();
        }
    }

    private async void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.ConfirmSegmentAsync();
    }

    private async void Reject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.RejectSegmentAsync();
    }
}
