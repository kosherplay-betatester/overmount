namespace Darkmount.App.Setup;

public enum SetupResult { Installed, RunPortable, Cancelled }

/// <summary>
/// The setup window shown when the downloaded file is started from anywhere but the install folder: install / update,
/// or just run it without installing.
/// </summary>
public sealed class SetupForm : Form
{
    readonly CheckBox _autostart = Check("Start OverMount with Windows"), _desktop = Check("Put a shortcut on the desktop"),
        _launch = Check("Start OverMount when setup finishes");
    readonly Label _progress = new() { AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(0, 10, 0, 0) };
    readonly Button _install, _portable, _cancel;
    SetupResult _result = SetupResult.Cancelled;

    SetupForm()
    {
        Ui.BeginLayout(this);
        Text = "OverMount Setup";
        Icon = AppIcon.Window;
        ClientSize = new Size(600, 470);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Font = Ui.Body;

        var installed = Installer.InstalledVersion;
        var current = Installer.CurrentVersion;
        string action = installed is null ? "Install" : installed < current ? "Update" : "Reinstall";

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(26, 22, 26, 10) };
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        using (var big = new Icon(AppIcon.Window, 64, 64))
            header.Controls.Add(new PictureBox { Image = big.ToBitmap(), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(0, 0, 14, 0) });
        var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 2, 0, 0) };
        titles.Controls.Add(new Label { Text = $"OverMount {current.ToString(3)}", AutoSize = true, Font = new Font("Segoe UI Semibold", 18f), ForeColor = Ui.Text, Margin = new Padding(0) });
        titles.Controls.Add(new Label { Text = "Take over your Mount.", AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Ui.Accent, Margin = new Padding(2, 0, 0, 0) });
        header.Controls.Add(titles);
        body.Controls.Add(header);
        body.Controls.Add(Ui.Note("Live PC stats on your Dark Mount's dock, IO Center-style lighting with per-key colours, macros, " +
                                  "profiles and more — for the be quiet! Dark Mount and Light Mount keyboards.", 540));
        body.Controls.Add(new Label { Height = 8 });
        body.Controls.Add(Ui.Note(installed switch
        {
            null => "Installs for your Windows account only, no administrator rights needed.",
            _ when installed < current => $"Version {installed.ToString(3)} is installed. This updates it to {current.ToString(3)}; your settings, profiles and macros stay.",
            _ => $"Version {installed.ToString(3)} is already installed. Reinstalling keeps your settings, profiles and macros.",
        }, 540));
        body.Controls.Add(Ui.Note(@"Location: %LOCALAPPDATA%\Programs\OverMount", 540));
        body.Controls.Add(new Label { Height = 6 });
        _autostart.Checked = installed is null || Autostart.IsEnabledFor(Installer.InstalledExe);
        _desktop.Checked = Installer.DesktopShortcutExists;
        _launch.Checked = true;
        body.Controls.AddRange([_autostart, _desktop, _launch, _progress]);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10) };
        _cancel = Ui.Button("Cancel", (_, _) => Close());
        _install = Ui.Button(action, async (_, _) => await Install(), primary: true);
        _portable = Ui.Button("Run without installing", (_, _) => { _result = SetupResult.RunPortable; Close(); });
        buttons.Controls.AddRange([_cancel, _install, _portable]);
        AcceptButton = _install;

        Controls.Add(body);
        Controls.Add(buttons);
        Ui.EndLayout(this);
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Ui.Text, Font = Ui.Body, Margin = new Padding(0, 4, 0, 4) };

    async Task Install()
    {
        foreach (var c in new Control[] { _install, _portable, _cancel, _autostart, _desktop, _launch }) c.Enabled = false;
        var options = new Installer.Options(_autostart.Checked, _desktop.Checked, _launch.Checked);
        var progress = new Progress<string>(text => _progress.Text = text);
        try
        {
            await Task.Run(() => Installer.Install(options, progress));
            _result = SetupResult.Installed;
            if (options.Launch) { Close(); return; }
            _progress.Text = "Done. Find OverMount in the Start menu.";
            _cancel.Text = "Close";
            _cancel.Enabled = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Install failed: {e}");
            _progress.ForeColor = Color.FromArgb(255, 120, 90);
            _progress.Text = $"Setup failed: {e.Message}";
            foreach (var c in new Control[] { _install, _portable, _cancel, _autostart, _desktop, _launch }) c.Enabled = true;
        }
    }

    /// <summary>Shows the setup window; returns what the user chose.</summary>
    public static SetupResult Run()
    {
        using var form = new SetupForm();
        Application.Run(form);
        return form._result;
    }

    /// <summary>"--install --silent": used by the in-app updater. Keeps the current choices and restarts the app.</summary>
    public static void InstallSilently()
    {
        try
        {
            Installer.Install(new Installer.Options(
                StartWithWindows: Installer.InstalledVersion is null || Autostart.IsEnabledFor(Installer.InstalledExe),
                DesktopShortcut: Installer.DesktopShortcutExists, Launch: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Silent install failed: {e}");
            MessageBox.Show($"Updating OverMount failed: {e.Message}\n\nDownload the latest version from {Installer.RepoUrl}/releases",
                "OverMount", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>"--uninstall" from Windows' installed apps (or the About page).</summary>
    public static void Uninstall(bool silent)
    {
        bool removeData = false;
        if (!silent)
        {
            if (MessageBox.Show("Remove OverMount from this PC?\n\nYour keyboard keeps its current lighting and key settings.",
                    "Uninstall OverMount", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            removeData = MessageBox.Show("Also delete your OverMount settings, profiles, macros and keyboard backup?\n\n" +
                                         "Choose No to keep them for a later reinstall.", "Uninstall OverMount",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        try
        {
            Installer.Uninstall(removeData);
            if (!silent) MessageBox.Show("OverMount was removed. Thanks for trying it!", "OverMount", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"Uninstall failed: {e}");
            if (!silent) MessageBox.Show($"Uninstalling failed: {e.Message}", "OverMount", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
