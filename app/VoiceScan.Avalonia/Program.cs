using Avalonia;

namespace VoiceScan.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VoiceScan.Core.AppPaths.UseBundledTools();
        VoiceScan.Core.Logging.VoiceScanLogger.Initialize(
            Path.Combine(VoiceScan.Core.AppPaths.DataRoot, "logs", "voicescan.log"));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
