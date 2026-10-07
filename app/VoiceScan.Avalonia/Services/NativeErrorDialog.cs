using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceScan.App.Services;

/// <summary>
/// Last-resort error display that does not depend on Avalonia, for failures before or after the UI is usable.
/// The app is a WinExe, so without this a startup failure would close with no visible trace.
/// </summary>
public static class NativeErrorDialog
{
    private const uint MbOk = 0x0, MbIconError = 0x10, MbTopmost = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    public static void Show(string title, string message)
    {
        Console.Error.WriteLine($"{title}\n{message}");
        WriteCrashFile(title, message);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MessageBox(IntPtr.Zero, message, title, MbOk | MbIconError | MbTopmost);
            }
            else if (OperatingSystem.IsLinux())
            {
                ShowOnLinux(title, message);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"(Native error dialog failed: {ex.Message})");
        }
    }

    /// <summary>Kept so the failure can still be found after a dialog was missed or no dialog tool exists.</summary>
    private static void WriteCrashFile(string title, string message)
    {
        try
        {
            var path = Path.Combine(VoiceScan.Core.AppPaths.DataRoot, "last-crash.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{DateTimeOffset.Now:O}\n{title}\n\n{message}\n");
        }
        catch
        {
            // already reporting a failure; nowhere left to write
        }
    }

    private static void ShowOnLinux(string title, string message)
    {
        // Tools are tried in order; the first one installed wins.
        (string Tool, string[] Args)[] candidates =
        [
            ("zenity", ["--error", "--no-markup", "--width=520", $"--title={title}", $"--text={message}"]),
            ("kdialog", ["--error", message, "--title", title]),
            ("xmessage", ["-center", $"{title}\n\n{message}"]),
            // Not a dialog, but on minimal desktops it is the only visible channel left.
            ("notify-send", ["--urgency=critical", "--expire-time=0", title, message]),
        ];

        foreach (var (tool, args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var process = Process.Start(psi);
                process?.WaitForExit();
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // not installed; try the next tool
            }
        }
    }
}
