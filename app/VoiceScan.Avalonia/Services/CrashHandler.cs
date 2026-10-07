using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VoiceScan.App.Core.Services;
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

    /// <summary>
    /// For failures where Avalonia may not be running (setup, XAML load, platform init): log and show a native dialog.
    /// </summary>
    public static void ReportFatalWithoutUi(Exception ex, string headline)
    {
        try { VoiceScanLogger.Fatal("CrashHandler", headline, ex); }
        catch { /* logging must never mask the original error */ }

        var explanation = ErrorExplainer.Explain(ex);
        NativeErrorDialog.Show("VoiceScan — Error",
            $"{headline}\n\n{explanation.Cause}\n\nWhat you can do: {explanation.Remedy}\n\n" +
            $"Technical details: {LogPath}\n\n{ErrorExplainer.Unwrap(ex).GetType().Name}: {ErrorExplainer.Unwrap(ex).Message}");
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
                if (wait)
                {
                    // Blocking the UI thread would freeze the dialog itself, and the process may be about to die.
                    ReportFatalWithoutUi(ex, headline);
                    return;
                }
                ShowDialog(ex, headline, fatal, null, null);
                return;
            }

            using var shown = new ManualResetEventSlim();
            using var done = new ManualResetEventSlim();
            Dispatcher.UIThread.Post(() => ShowDialog(ex, headline, fatal, shown, done));

            // If the UI thread is dead or shutting down the dialog never appears; never fail silently.
            if (!shown.Wait(TimeSpan.FromSeconds(5)))
            {
                ReportFatalWithoutUi(ex, headline);
                return;
            }
            if (wait) done.Wait();
        }
        catch (Exception dialogEx)
        {
            Console.Error.WriteLine($"{headline}\n{ex}\n(Error dialog failed: {dialogEx.Message})");
            ReportFatalWithoutUi(ex, headline);
        }
    }

    public static Window CreateErrorWindow(Exception ex, string headline, bool canContinue, Action? onClose = null)
    {
        var explanation = ErrorExplainer.Explain(ex);
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
            Width = 680,
            Height = 520,
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
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new TextBlock { Text = headline, FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = explanation.Cause, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "What you can do: " + explanation.Remedy, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"Technical details were written to {LogPath}", Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
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

    private static void ShowDialog(Exception ex, string headline, bool fatal, ManualResetEventSlim? shown, ManualResetEventSlim? done)
    {
        // Avoid a dialog storm when one fault raises many exceptions; the first dialog already informs the user.
        if (Interlocked.Exchange(ref _dialogOpen, 1) == 1)
        {
            shown?.Set();
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
            shown?.Set();
        }
        catch
        {
            Closed();
            throw;
        }
    }
}
