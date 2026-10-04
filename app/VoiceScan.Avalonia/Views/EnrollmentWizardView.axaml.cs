using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class EnrollmentWizardView : UserControl
{
    private EnrollmentWizardViewModel? ViewModel => DataContext as EnrollmentWizardViewModel;

    public EnrollmentWizardView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);
        DropZone.AddHandler(DragDrop.DropEvent, DropZone_Drop);
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FilePickers.PickFilesAsync(this, "Voice samples", allowMultiple: true, MediaFileCollector.PickerPatterns);
        if (files.Count > 0 && ViewModel is { } vm) await vm.AddSamplesAsync(files);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = await FilePickers.PickFolderAsync(this, "Select folder of voice samples");
        if (path is not null && ViewModel is { } vm) await vm.AddSamplesAsync([path]);
    }

    private void ClearSamples_Click(object? sender, RoutedEventArgs e) => ViewModel?.ClearSamples();

    private void RemoveSample_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is EnrollmentSampleItem sample) ViewModel?.RemoveSample(sample);
    }

    private async void ToggleWaveform_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MediaFileItem sample && ViewModel is { } vm)
            await vm.ToggleWaveformAsync(sample);
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = FilePickers.HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) await vm.AddSamplesAsync(FilePickers.DroppedPaths(e));
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
