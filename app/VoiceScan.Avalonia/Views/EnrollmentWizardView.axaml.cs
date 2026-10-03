using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class EnrollmentWizardView : UserControl
{
    private EnrollmentWizardViewModel? ViewModel => DataContext as EnrollmentWizardViewModel;

    public EnrollmentWizardView() => InitializeComponent();

    private async void BrowseAudio_Click(object? sender, RoutedEventArgs e)
    {
        var path = await FilePickers.PickFileAsync(this, "Voice sample",
            "*.wav", "*.mp3", "*.flac", "*.ogg", "*.m4a", "*.mp4", "*.mkv");
        if (path is null || ViewModel is not { } vm) return;

        vm.SelectedAudioPath = path;
        await vm.RunQualityCheckAsync();
    }

    private void DeleteVoice_Click(object? sender, RoutedEventArgs e) => ViewModel?.DeleteSelectedProfile();

    private void VoicesList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        ViewModel?.DeleteSelectedProfile();
        e.Handled = true;
    }

    private async void CreateProfile_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CreateProfileAsync();
    }
}
