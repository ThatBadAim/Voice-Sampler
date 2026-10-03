using System.Windows.Forms;

namespace VoiceScan.Installer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new UninstallForm());
            return;
        }

        Application.Run(new InstallForm());
    }
}
