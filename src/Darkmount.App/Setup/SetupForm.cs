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
    // Extras that need Windows' permission (asked once, after installing): only offered when they'd help on this PC.
    internal static Func<string?>? HardwareForScreenshots; // the render tool shows the extras on any PC
    readonly CheckBox _openRgb = Check(""), _cpuSensor = Check("Show CPU temperature (installs PawnIO, a small signed driver)");
    readonly Label _extrasNote = Ui.Note("Windows asks for permission once for these. You can also set them up later in OverMount.", 540);
    readonly Label _progress = new() { AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(0, 10, 0, 0) };
    readonly Button _install, _portable, _cancel;
    SetupResult _result = SetupResult.Cancelled;
    bool _working; // installing or setting up extras: the window stays open until it's done

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
                                  "profiles and more for the be quiet! Dark Mount and Light Mount keyboards, plus lighting and " +
                                  "macros for other RGB keyboards and gaming laptops.", 540));
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

        // A laptop or keyboard that OpenRGB lights (and OpenRGB isn't serving it yet), and the CPU sensor if it isn't set up.
        string? hardware = null;
        bool beQuiet = false;
        try
        {
            hardware = HardwareForScreenshots?.Invoke() ?? OpenRgbSetup.FindHardware();
            beQuiet = HidSharp.DeviceList.Local.GetHidDevices(0x373F).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        _openRgb.Text = "Light up this keyboard with OverMount (installs the free OpenRGB)";
        bool preview = HardwareForScreenshots is not null;
        _openRgb.Visible = _openRgb.Checked = hardware is not null && (preview || !OpenRgbSetup.ServerReachable());
        _cpuSensor.Visible = preview || !CpuSensorSetup.IsSetUp;
        _cpuSensor.Checked = beQuiet; // the dock's dashboard shows it; elsewhere it only feeds temperature lighting
        _extrasNote.Visible = _openRgb.Visible || _cpuSensor.Visible;
        if (_openRgb.Visible) _extrasNote.Text = $"Found: {hardware}. " + _extrasNote.Text;
        _extrasNote.Margin = new Padding(22, 0, 0, 4);
        if (_extrasNote.Visible) ClientSize = new Size(600, 560);
        body.Controls.AddRange([_autostart, _desktop, _launch, _openRgb, _cpuSensor, _extrasNote, _progress]);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10) };
        _cancel = Ui.Button("Cancel", (_, _) => Close());
        _install = Ui.Button(action, async (_, _) => await Install(), primary: true);
        _portable = Ui.Button("Run without installing", (_, _) => { _result = SetupResult.RunPortable; Close(); });
        buttons.Controls.AddRange([_cancel, _install, _portable]);
        AcceptButton = _install;

        Controls.Add(body);
        Controls.Add(buttons);
        FormClosing += (_, e) => { if (_working && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
        Ui.EndLayout(this);
    }

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Ui.Text, Font = Ui.Body, Margin = new Padding(0, 4, 0, 4) };

    async Task Install()
    {
        var all = new Control[] { _install, _portable, _cancel, _autostart, _desktop, _launch, _openRgb, _cpuSensor };
        foreach (var c in all) c.Enabled = false;
        bool openRgb = _openRgb.Visible && _openRgb.Checked, cpuSensor = _cpuSensor.Visible && _cpuSensor.Checked;
        bool extras = openRgb || cpuSensor;
        var options = new Installer.Options(_autostart.Checked, _desktop.Checked, _launch.Checked && !extras); // extras first
        var progress = new Progress<string>(text => _progress.Text = text);
        _working = true;
        try
        {
            await Task.Run(() => Installer.Install(options, progress));
            _result = SetupResult.Installed;
            if (extras)
            {
                _progress.Text = "Setting up the extras… Windows asks for permission once" + (openRgb ? " (downloading OpenRGB takes a minute)." : ".");
                var outcome = await Extras.SetUpAsync(Installer.InstalledExe, openRgb, cpuSensor);
                _progress.Text = outcome switch
                {
                    Extras.Result.Done => "Extras set up.",
                    Extras.Result.Declined => "Extras skipped (permission declined). OverMount offers them again on its Home page.",
                    _ => "Some extras could not be set up. OverMount offers them again on its Home page.",
                };
                if (_launch.Checked)
                {
                    Installer.Launch();
                    _working = false;
                    Close();
                    return;
                }
            }
            _working = false;
            if (options.Launch) { Close(); return; }
            _progress.Text = (extras ? _progress.Text + " " : "") + "Done. Find OverMount in the Start menu.";
            _cancel.Text = "Close";
            _cancel.Enabled = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            _working = false;
            Log.Write($"Install failed: {e}");
            _progress.ForeColor = Color.FromArgb(255, 120, 90);
            _progress.Text = $"Setup failed: {e.Message}";
            foreach (var c in all) c.Enabled = true;
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
