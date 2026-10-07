using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace VoiceScan.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (SetupWindow.IsSetupNeeded())
            {
                desktop.MainWindow = new SetupWindow(desktop);
            }
            else
            {
                var loading = CreateLoadingWindow();
                desktop.MainWindow = loading;
                _ = OpenMainWindowAsync(desktop, loading).ContinueWith(
                    t => Services.CrashHandler.Report(t.Exception!, "VoiceScan could not finish starting.", fatal: true),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Loads the models on a worker thread (verification, session creation and warm-up take seconds), then replaces
    /// <paramref name="current"/> with the main window, or with an error window if loading failed.
    /// </summary>
    public static async Task OpenMainWindowAsync(IClassicDesktopStyleApplicationLifetime desktop, Window current)
    {
        Window next;
        try
        {
            var model = await Task.Run(AppServiceBootstrap.LoadEmbeddingModel);
            next = await Dispatcher.UIThread.InvokeAsync(() => (Window)new MainWindow(AppServiceBootstrap.CreateMainViewModel(model)));
        }
        catch (Exception ex)
        {
            VoiceScan.Core.Logging.VoiceScanLogger.Fatal("App", "Startup failed", ex);
            next = await Dispatcher.UIThread.InvokeAsync(() =>
                Services.CrashHandler.CreateErrorWindow(ex, "VoiceScan could not start.", canContinue: false));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            desktop.MainWindow = next;
            next.Show();
            current.Close();
        });
    }

    private static Window CreateLoadingWindow() => new()
    {
        Title = "VoiceScan",
        Width = 360,
        Height = 140,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Content = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(24),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Loading and verifying voice models…" },
                new ProgressBar { IsIndeterminate = true },
            },
        },
    };
}
