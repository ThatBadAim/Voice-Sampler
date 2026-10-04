using System.Windows.Forms;

namespace VoiceScan.Installer;

internal sealed class InstallForm : Form
{
    private readonly TextBox _dir = new() { Text = InstallActions.DefaultInstallDir, Dock = DockStyle.Fill };
    private readonly CheckBox _desktop = new() { Text = "Add a desktop shortcut", Checked = true, AutoSize = true };
    private readonly CheckBox _launch = new() { Text = "Start VoiceScan when setup finishes", Checked = true, AutoSize = true };
    private readonly Label _ffmpegNote = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly Button _install = new() { Text = "Install", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    private readonly bool _needsFfmpeg = !InstallActions.FfmpegOnPath();

    public InstallForm()
    {
        Text = "VoiceScan setup";
        ClientSize = new Size(540, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label { Text = "Install VoiceScan", Font = new Font(Font.FontFamily, 15, FontStyle.Bold), AutoSize = true };
        var intro = new Label
        {
            Text = "Find where a person speaks in your recordings. Everything runs on this computer.",
            AutoSize = true,
            MaximumSize = new Size(500, 0)
        };
        _ffmpegNote.Text = _needsFfmpeg
            ? "FFmpeg was not found on this computer. Setup will download FFmpeg 9.0.2 (about 115 MB, checked against a pinned checksum) and keep it inside the VoiceScan folder."
            : "FFmpeg is already installed. Nothing extra to download.";

        var dirRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirRow.Controls.Add(_dir, 0, 0);
        dirRow.Controls.Add(_browse, 1, 0);

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(20)
        };
        dirRow.Width = 500;
        _progress.Width = 500;
        layout.Controls.AddRange([title, intro, new Label { Text = "Install to", AutoSize = true }, dirRow, _desktop, _launch, _ffmpegNote, _progress, _status, _install]);
        Controls.Add(layout);

        _browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _dir.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK) _dir.Text = Path.Combine(dialog.SelectedPath, InstallActions.AppName);
        };
        _install.Click += async (_, _) => await RunInstallAsync();
    }

    private async Task RunInstallAsync()
    {
        string dir;
        try
        {
            dir = InstallActions.ValidateInstallDir(_dir.Text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "VoiceScan setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _install.Enabled = _browse.Enabled = _dir.Enabled = false;
        IProgress<(int Percent, string Text)> progress = new Progress<(int Percent, string Text)>(p =>
        {
            _progress.Value = Math.Clamp(p.Percent, 0, 100);
            _status.Text = p.Text;
        });

        try
        {
            await Task.Run(() => InstallActions.ExtractApp(dir, progress));

            if (_needsFfmpeg)
            {
                try
                {
                    await InstallActions.InstallFfmpegAsync(dir, progress);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
                {
                    MessageBox.Show(this,
                        $"VoiceScan was installed, but FFmpeg could not be set up:\n\n{ex.Message}\n\n" +
                        "Install it later with:  winget install Gyan.FFmpeg\nVoiceScan will remind you on first start.",
                        "FFmpeg not installed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            progress.Report((98, "Creating shortcuts..."));
            await Task.Run(() => InstallActions.Register(dir, _desktop.Checked));
            progress.Report((100, "VoiceScan is installed."));

            if (_launch.Checked) InstallActions.Launch(dir);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, $"Setup could not finish:\n\n{ex.Message}", "VoiceScan setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = _browse.Enabled = _dir.Enabled = true;
        }
    }
}
