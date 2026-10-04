using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VoiceScan.App.Core.Models;
using VoiceScan.App.Core.Services;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ScanDashboardView : UserControl
{
    private ScanDashboardViewModel? ViewModel => DataContext as ScanDashboardViewModel;

    public ScanDashboardView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Metrics.Columns = e.NewSize.Width < 640 ? 2 : 4;
        DropZone.AddHandler(DragDrop.DragOverEvent, DropZone_DragOver);
        DropZone.AddHandler(DragDrop.DropEvent, DropZone_Drop);
        DataContextChanged += (_, _) => Resubscribe();
    }

    private ScanDashboardViewModel? _subscribed;
    private bool _attached;

    // Pages are recreated on every navigation; subscribe only while attached so old views are not kept alive.
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
        RefreshPauseButton(vm);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanDashboardViewModel.IsPaused) && sender is ScanDashboardViewModel vm)
        {
            RefreshPauseButton(vm);
        }
    }

    private void RefreshPauseButton(ScanDashboardViewModel vm)
    {
        PauseResumeText.Text = vm.IsPaused ? "Resume" : "Pause";
        PauseResumeIcon.Data = (Avalonia.Media.Geometry)this.FindResource(vm.IsPaused ? "IconPlay" : "IconPause")!;
    }

    private async void AddFiles_Click(object? sender, RoutedEventArgs e)
    {
        var files = await FilePickers.PickFilesAsync(
            this, "Recordings", allowMultiple: true, ViewModel?.LastBrowseFolder, MediaFileCollector.PickerPatterns);
        if (files.Count == 0 || ViewModel is not { } vm) return;
        vm.LastBrowseFolder = Path.GetDirectoryName(files[0]);
        vm.AddPaths(files);
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = await FilePickers.PickFolderAsync(this, "Select recordings folder", ViewModel?.LastBrowseFolder);
        if (path is null || ViewModel is not { } vm) return;
        vm.LastBrowseFolder = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) ?? path;
        vm.AddPaths([path]);
    }

    private void ClearFiles_Click(object? sender, RoutedEventArgs e) => ViewModel?.ClearFiles();

    private void RemoveFile_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MediaFileItem file) ViewModel?.RemoveFile(file);
    }

    private async void ToggleWaveform_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MediaFileItem file && ViewModel is { } vm)
            await vm.ToggleWaveformAsync(file);
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel is { CanEditFiles: true } && FilePickers.HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void DropZone_Drop(object? sender, DragEventArgs e)
    {
        if (ViewModel is { CanEditFiles: true } vm) vm.AddPaths(FilePickers.DroppedPaths(e));
    }

    private async void StartScan_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.StartScanAsync();
    }

    private void PauseResume_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (vm.IsPaused) vm.ResumeScan();
        else vm.PauseScan();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => ViewModel?.CancelScan();
}
