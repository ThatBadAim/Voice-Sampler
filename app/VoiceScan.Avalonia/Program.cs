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
        VoiceScan.App.Services.CrashHandler.Install();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            VoiceScan.Core.Logging.VoiceScanLogger.Fatal("Program", "Fatal error in UI loop", ex);
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
