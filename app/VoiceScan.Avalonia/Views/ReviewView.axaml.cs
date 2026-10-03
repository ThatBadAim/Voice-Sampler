using Avalonia.Controls;
using Avalonia.Interactivity;
using VoiceScan.App.Core.ViewModels;

namespace VoiceScan.App.Views;

public partial class ReviewView : UserControl
{
    private ReviewViewModel? ViewModel => DataContext as ReviewViewModel;

    public ReviewView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (ViewModel is { } vm) await vm.InitializeAsync();
        };
    }

    private void PlaySnippet_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlaySelectedSnippet();

    private async void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.ConfirmSegmentAsync();
    }

    private async void Reject_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.RejectSegmentAsync();
    }
}
