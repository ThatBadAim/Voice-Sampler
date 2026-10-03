using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VoiceScan.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = SetupWindow.IsSetupNeeded()
                ? new SetupWindow(desktop)
                : new MainWindow(AppServiceBootstrap.CreateMainViewModel());
        }
        base.OnFrameworkInitializationCompleted();
    }
}
