using System.Windows.Forms;

namespace VoiceScan.Installer;

/// <summary>Runs the uninstall straight away with one question about user data, then exits.</summary>
internal sealed class UninstallForm : Form
{
    public UninstallForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Load += (_, _) => Run();
    }

    private void Run()
    {
        string? dir = InstallActions.InstalledLocation();
        if (dir is null)
        {
            MessageBox.Show("VoiceScan is not installed for this user.", "VoiceScan uninstall");
            Close();
            return;
        }

        if (MessageBox.Show("Remove VoiceScan from this computer?", "VoiceScan uninstall",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            Close();
            return;
        }

        bool deleteData = MessageBox.Show(
            "Also delete your saved voices, scan cache and review decisions?\n\nChoose No to keep them in case you reinstall.",
            "VoiceScan uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

        InstallActions.Uninstall(dir, deleteData);
        Close();
    }
}
