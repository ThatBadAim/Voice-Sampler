using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VoiceScan.Core;
using VoiceScan.Core.Logging;

namespace VoiceScan.App.Services;

/// <summary>
/// Routes every unhandled exception to the log and an error dialog so the app stays open.
/// </summary>
public static class CrashHandler
{
    private static int _dialogOpen;

    public static string LogPath => Path.Combine(AppPaths.DataRoot, "logs", "voicescan.log");

    public static void Install()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception, "An unexpected error occurred. The application is still running.", fatal: false);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            Report(e.Exception, "A background task failed. The application is still running.", fatal: false);
        };

        // The runtime terminates the process after this handler returns, so for
        // non-UI threads block until the user has dismissed the dialog.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString());
            Report(ex, "A background thread crashed.", fatal: e.IsTerminating, wait: true);
        };
    }

    /// <summary>Logs the exception and shows the dialog. Safe to call from any thread.</summary>
    public static void Report(Exception ex, string headline, bool fatal, bool wait = false)
    {
        try { VoiceScanLogger.Fatal("CrashHandler", headline, ex); }
        catch { /* logging must never mask the original error */ }

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                if (wait) return; // blocking the UI thread would freeze the dialog itself
                ShowDialog(ex, headline, fatal, null);
                return;
            }

            using var done = new ManualResetEventSlim();
            Dispatcher.UIThread.Post(() => ShowDialog(ex, headline, fatal, done));
            if (wait) done.Wait();
        }
        catch (Exception dialogEx)
        {
            Console.Error.WriteLine($"{headline}\n{ex}\n(Error dialog failed: {dialogEx.Message})");
        }
    }

    public static Window CreateErrorWindow(Exception ex, string headline, bool canContinue, Action? onClose = null)
    {
        var details = $"{ex.GetType().Name}: {ex.Message}\n\n{ex}";

        var detailBox = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace"),
            MinHeight = 160,
        };

        var quit = new Button { Content = "Quit" };
        var cont = new Button { Content = "Continue", IsVisible = canContinue };
        var copy = new Button { Content = "Copy details" };

        var window = new Window
        {
            Title = "VoiceScan — Error",
            Width = 640,
            Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new DockPanel
            {
                Margin = new Thickness(16),
                LastChildFill = true,
            },
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { copy, cont, quit },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);

        var header = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new TextBlock { Text = headline, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Details were written to {LogPath}", Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
            },
        };
        DockPanel.SetDock(header, Dock.Top);

        var panel = (DockPanel)window.Content!;
        panel.Children.Add(header);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = detailBox,
        });

        copy.Click += async (_, _) =>
        {
            if (window.Clipboard is { } clipboard) await clipboard.SetTextAsync(details);
        };
        cont.Click += (_, _) => window.Close();
        quit.Click += (_, _) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1);
            else
                Environment.Exit(1);
        };
        window.Closed += (_, _) => onClose?.Invoke();
        return window;
    }

    private static void ShowDialog(Exception ex, string headline, bool fatal, ManualResetEventSlim? done)
    {
        // Avoid a dialog storm when one fault raises many exceptions.
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1)
        {
            done?.Set();
            return;
        }

        void Closed()
        {
            Interlocked.Exchange(ref _dialogOpen, 0);
            done?.Set();
        }

        try
        {
            var window = CreateErrorWindow(ex, headline, canContinue: !fatal, Closed);
            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is { IsVisible: true }) window.ShowDialog(owner).ContinueWith(_ => { }, TaskScheduler.Default);
            else window.Show();
        }
        catch
        {
            Closed();
            throw;
        }
    }
}
