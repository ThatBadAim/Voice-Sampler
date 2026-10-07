using Avalonia.Controls;
using Avalonia.Interactivity;
using VoiceScan.App.Core.ViewModels;
using VoiceScan.App.Services;

namespace VoiceScan.App.Views;

public partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();
    }

    private async void ChooseModelFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelsViewModel vm) return;
        string? path = await FilePickers.PickFileAsync(this, "Voice-embedding model (.onnx)", "*.onnx");
        if (path != null) vm.ImportPath = path;
    }
}
