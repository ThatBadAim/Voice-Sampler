using Avalonia;

namespace VoiceScan.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            VoiceScan.Core.AppPaths.UseBundledTools();
            VoiceScan.Core.Logging.VoiceScanLogger.Initialize(
                Path.Combine(VoiceScan.Core.AppPaths.DataRoot, "logs", "voicescan.log"));
            VoiceScan.App.Services.CrashHandler.Install();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Nothing else can be relied on here, so use a dialog that does not need Avalonia.
            VoiceScan.App.Services.CrashHandler.ReportFatalWithoutUi(ex, "VoiceScan hit a fatal error and has to close.");
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
