using System.ComponentModel;

namespace Darkmount.App.Setup;

/// <summary>
/// "Get the most out of OverMount": shows which sensor apps are installed and running and, only after the user clicks
/// Set up, installs the missing ones (winget; each installer shows Windows' own permission prompt), makes Afterburner
/// start with Windows and starts them. Without winget it opens the download pages instead.
/// </summary>
public sealed class CompanionsDialog : Form
{
    const int Declined = 1223; // ERROR_CANCELLED: the user said No to Windows' permission prompt

    static CompanionsDialog? _open;

    readonly Dictionary<CompanionApp, (CheckBox Box, Label State)> _rows = [];
    readonly CheckBox _autostart = Ui.Check("Start MSI Afterburner with Windows (it brings RivaTuner along)");
    readonly Label _progress = new() { AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(0, 8, 0, 0), UseMnemonic = false };
    readonly Label _wingetNote;
    readonly Button _go, _close;
    readonly bool _winget;
    readonly List<string> _log = [];
    readonly HashSet<CompanionApp> _ready = [];
    readonly CheckBox _sensorBox = new()
    {
        Text = "OverMount CPU sensor (recommended)", AutoSize = true, ForeColor = Ui.Text, Font = new Font("Segoe UI Semibold", 10.5f),
        Margin = new Padding(0, 6, 0, 0), UseMnemonic = false,
    };
    readonly Label _sensorState = new() { AutoSize = true, Font = Ui.Body, Margin = new Padding(0, 8, 0, 0), UseMnemonic = false };
    readonly bool _cpuTempAvailable;
    bool _busy, _autostartOn, _sensorReady;

    CompanionsDialog(IReadOnlyList<CompanionStatus> status, bool autostartOn, bool winget, bool sensorRunning, bool cpuTempAvailable)
    {
        _winget = winget;
        _cpuTempAvailable = cpuTempAvailable;
        Ui.BeginLayout(this);
        Text = "OverMount — sensor apps";
        Icon = AppIcon.Window;
        ClientSize = new Size(660, 620);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Font = Ui.Body;

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(24, 18, 24, 6) };
        body.Controls.Add(new Label { Text = "Get the most out of OverMount", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Ui.Text, Margin = new Padding(0, 0, 0, 4), UseMnemonic = false });
        body.Controls.Add(Ui.Note("GPU temperature, load, VRAM, CPU load and CPU watts come from Windows itself. CPU temperature needs " +
                                  "a driver: OverMount's own CPU sensor or MSI Afterburner / HWiNFO. RivaTuner adds in-game FPS. " +
                                  "Nothing is installed or started unless you click Set up.", 600));

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var sensorStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 8, 4) };
        sensorStack.Controls.Add(_sensorBox);
        var sensorPurpose = Ui.Note("CPU temperature (and watts) read by OverMount itself, no extra app needed. Installs the " +
                                    "open-source PawnIO driver and a small helper that runs in the background.", 400);
        sensorPurpose.Margin = new Padding(20, 0, 0, 2);
        sensorStack.Controls.Add(sensorPurpose);
        table.Controls.Add(sensorStack);
        table.Controls.Add(_sensorState);
        foreach (var app in Companions.All)
        {
            var box = new CheckBox { Text = app.Name, AutoSize = true, ForeColor = Ui.Text, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 6, 0, 0), UseMnemonic = false };
            var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 8, 4) };
            stack.Controls.Add(box);
            var purpose = Ui.Note(app.Purpose, 400);
            purpose.Margin = new Padding(20, 0, 0, 2);
            stack.Controls.Add(purpose);
            var state = new Label { AutoSize = true, Font = Ui.Body, Margin = new Padding(0, 8, 0, 0), UseMnemonic = false };
            table.Controls.Add(stack);
            table.Controls.Add(state);
            _rows[app] = (box, state);
        }
        body.Controls.Add(table);
        _autostart.Margin = new Padding(0, 6, 0, 2);
        body.Controls.Add(_autostart);
        _wingetNote = Ui.Note(winget ? "Installs use winget, Windows' own package manager, from the official packages."
            : "winget (Windows' package manager) isn't available here, so Set up opens the download pages instead.", 600);
        body.Controls.Add(_wingetNote);
        body.Controls.Add(_progress);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10) };
        _close = Ui.Button("Not now", (_, _) => Close());
        _go = Ui.Button("Set up", async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception e) // other people's installers and tools: never let a surprise take the tray app down
            {
                Log.Write($"Sensor apps setup failed: {e}");
                if (!IsDisposed) Say($"Setup stopped: {e.Message}");
            }
        }, primary: true);
        buttons.Controls.AddRange([_close, _go]);
        AcceptButton = _go;

        Controls.Add(body);
        Controls.Add(buttons);
        ShowStatus(status, autostartOn, sensorRunning, initial: true);
        Ui.EndLayout(this);
        FormClosing += (_, e) => { if (_busy && e.CloseReason is CloseReason.UserClosing or CloseReason.FormOwnerClosing) e.Cancel = true; };
        FormClosed += (_, _) => { if (_open == this) _open = null; };
        HandleCreated += (_, _) => Ui.UseDarkTitleBar(this);
    }

    /// <summary>
    /// Opens the assistant (or brings the open one forward). Looking around takes a moment, so it runs in the background.
    /// It has no owner window on purpose: setup can take minutes and must not end when the settings window is closed.
    /// </summary>
    /// <param name="cpuTempAvailable">Some source already delivers CPU temperature (the CPU sensor row is then optional).</param>
    public static async Task OpenAsync(bool cpuTempAvailable)
    {
        if (_open is { IsDisposed: false } open) { open.Activate(); return; }
        var (status, autostart, winget, sensor) = await Task.Run(() =>
            (Companions.Check(), Companions.AfterburnerStartsWithWindows(), Companions.WingetAvailable(), CpuSensorSetup.IsRunning));
        if (_open is { IsDisposed: false } other) { other.Activate(); return; } // opened twice while looking
        _open = new CompanionsDialog(status, autostart, winget, sensor, cpuTempAvailable);
        _open.Show();
        _open.Activate();
    }

    /// <summary>
    /// True when a required app is missing or not running (and won't start with Windows), or nothing delivers CPU
    /// temperature. Runs in the background.
    /// </summary>
    public static Task<bool> NeedsAttentionAsync(bool cpuTempAvailable) => Task.Run(() =>
        !cpuTempAvailable || Companions.NeedsAttention(Companions.Check(), Companions.AfterburnerStartsWithWindows()));

    void ShowStatus(IReadOnlyList<CompanionStatus> status, bool autostartOn, bool sensorRunning, bool initial)
    {
        _sensorReady = sensorRunning;
        (_sensorState.Text, _sensorState.ForeColor) = sensorRunning ? ("\u2714 Running", Color.FromArgb(76, 217, 100))
            : CpuSensorSetup.IsSetUp ? ("Set up, not running", Color.FromArgb(255, 176, 32))
            : ("Not set up", _cpuTempAvailable ? Ui.Dim : Color.FromArgb(255, 176, 32));
        if (initial || sensorRunning) _sensorBox.Checked = !sensorRunning && !_cpuTempAvailable;
        _sensorBox.ForeColor = sensorRunning ? Ui.Dim : Ui.Text;
        bool anything = !sensorRunning && !_cpuTempAvailable;
        foreach (var s in status)
        {
            var (box, state) = _rows[s.App];
            bool ready = s.Installed && s.Running;
            (state.Text, state.ForeColor) = ready ? ("✔ Running", Color.FromArgb(76, 217, 100))
                : s.Installed ? ("Installed, not running", Color.FromArgb(255, 176, 32))
                : ("Not installed", s.App.Optional ? Ui.Dim : Color.FromArgb(255, 176, 32));
            // Done rows stay readable (a disabled check box is drawn etched grey) but can't be ticked.
            if (ready) _ready.Add(s.App); else _ready.Remove(s.App);
            if (initial || ready) box.Checked = !ready && !s.App.Optional;
            box.ForeColor = ready ? Ui.Dim : Ui.Text;
            anything |= !ready;
        }
        bool hasAfterburner = status.Any(s => s.App == Companions.Afterburner && s.Installed);
        _autostartOn = autostartOn;
        if (initial || autostartOn) _autostart.Checked = true;
        _autostart.ForeColor = autostartOn ? Ui.Dim : Ui.Text;
        _autostart.Text = autostartOn ? "MSI Afterburner starts with Windows (it brings RivaTuner along)"
            : hasAfterburner ? "Start MSI Afterburner with Windows (it brings RivaTuner along)"
            : "Start MSI Afterburner with Windows once it's installed (it brings RivaTuner along)";
        Lock(false);
        bool todo = anything || !autostartOn;
        _go.Visible = todo;
        _close.Text = todo && initial ? "Not now" : "Close";
        if (!todo && initial) _progress.Text = "✔ Everything is set up. Nothing to do here.";
    }

    /// <summary>While setting up nothing can be ticked; afterwards only what's still to do.</summary>
    void Lock(bool busy)
    {
        foreach (var (app, (box, _)) in _rows) box.AutoCheck = !busy && !_ready.Contains(app);
        _sensorBox.AutoCheck = !busy && !_sensorReady;
        _autostart.AutoCheck = !busy && !_autostartOn;
        _go.Enabled = _close.Enabled = !busy;
        UseWaitCursor = busy;
    }

    void Say(string line)
    {
        if (IsDisposed) return;
        _log.Add(line);
        _progress.Text = string.Join(Environment.NewLine, _log.TakeLast(8));
        Log.Write($"Sensor apps: {line}");
    }

    async Task RunAsync()
    {
        if (_busy) return;
        var chosen = Companions.All.Where(a => !_ready.Contains(a) && _rows[a].Box.Checked).ToList();
        bool autostart = !_autostartOn && _autostart.Checked;
        bool sensor = !_sensorReady && _sensorBox.Checked;
        if (chosen.Count == 0 && !autostart && !sensor) { Say("Nothing selected."); return; }

        _busy = true;
        Lock(true);
        _log.Clear();
        bool needsHwInfoSetting = false;
        try
        {
            // 0. OverMount's own CPU sensor: PawnIO driver + helper, one permission prompt.
            if (sensor)
            {
                Say("Setting up OverMount's CPU sensor... Windows asks for permission once; the driver install can take a minute.");
                switch (await Task.Run(CpuSensorSetup.SetUpAsync)) // off the UI thread: the prompt blocks the caller
                {
                    case CpuSensorSetup.Result.Running: Say("\u2714 CPU sensor running: CPU temperature shows up on the dock."); break;
                    case CpuSensorSetup.Result.Declined: Say("CPU sensor skipped (permission declined)."); break;
                    case CpuSensorSetup.Result.NeedsPawnIo:
                        Say("The CPU sensor needs the PawnIO driver, which couldn't be installed automatically. Opening its page: " +
                            "install it, then click Set up again.");
                        OpenPage(CpuSensorSetup.PawnIoPage);
                        break;
                    default: Say("The CPU sensor couldn't be started (details in the log: tray icon, Open log folder)."); break;
                }
            }

            // 1. Install what's missing (Afterburner first: its installer may bring RivaTuner along).
            foreach (var app in chosen)
            {
                if (await Task.Run(() => Companions.FindExe(app)) is not null) continue;
                if (!_winget)
                {
                    Say($"Opening the {app.Name} download page. Install it, then click Set up again.");
                    OpenPage(app.DownloadPage);
                    continue;
                }
                Say($"Installing {app.Name}… Windows asks for permission; this can take a minute.");
                bool installed;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    installed = await Companions.InstallAsync(app, timeout.Token);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or OperationCanceledException)
                {
                    Log.Write($"Installing {app.Name} failed: {e.Message}");
                    installed = false;
                }
                if (installed) Say($"✔ {app.Name} installed.");
                else
                {
                    Say($"{app.Name} wasn't installed (permission declined or no internet?). Opening its download page.");
                    OpenPage(app.DownloadPage);
                }
            }

            // 2. Afterburner starts with Windows, the way its own "Start with Windows" does it.
            if (autostart && await Task.Run(() => Companions.FindExe(Companions.Afterburner)) is not null
                && !await Task.Run(Companions.AfterburnerStartsWithWindows))
            {
                Say("Making MSI Afterburner start with Windows… Windows asks for permission once.");
                try
                {
                    // Off the UI thread: the permission prompt blocks the caller until it's answered.
                    Say(await Task.Run(Companions.EnableAfterburnerAutostartAsync)
                        ? "✔ MSI Afterburner starts with Windows." : "MSI Afterburner's autostart couldn't be set up.");
                }
                catch (Win32Exception e) when (e.NativeErrorCode == Declined) { Say("Autostart skipped (permission declined)."); }
                catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
                {
                    Say($"MSI Afterburner's autostart couldn't be set up: {e.Message}");
                }
            }

            // 3. Start them. Afterburner starts RivaTuner itself, so give it a moment before starting RivaTuner directly.
            bool startedAfterburner = false;
            foreach (var app in chosen)
            {
                if (await Task.Run(() => Companions.FindExe(app)) is null || Companions.IsRunning(app)) continue;
                if (app == Companions.Rtss && startedAfterburner && await WaitUntilRunning(app, TimeSpan.FromSeconds(10))) continue;
                Say($"Starting {app.Name}…");
                try
                {
                    await Task.Run(() => Companions.Start(app));
                    startedAfterburner |= app == Companions.Afterburner;
                    needsHwInfoSetting |= app == Companions.HwInfo;
                }
                catch (Win32Exception e) when (e.NativeErrorCode == Declined) { Say($"{app.Name} wasn't started (permission declined)."); }
                catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException)
                {
                    Say($"{app.Name} couldn't be started: {e.Message}");
                }
            }
            if (startedAfterburner) await WaitUntilRunning(Companions.Afterburner, TimeSpan.FromSeconds(10));
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                var (status, autostartOn, sensorOn) = await Task.Run(() =>
                    (Companions.Check(), Companions.AfterburnerStartsWithWindows(), CpuSensorSetup.IsRunning));
                if (!IsDisposed)
                {
                    ShowStatus(status, autostartOn, sensorOn, initial: false);
                    bool ready = !Companions.NeedsAttention(status, afterburnerAutostarts: false) && (sensorOn || _cpuTempAvailable);
                    Say(ready ? "All set! CPU temperature, watts and FPS show up within a few seconds."
                        : "Some apps still aren't running — see above. You can come back here from Home → Setup check.");
                    if (needsHwInfoSetting) Say("In HWiNFO, open Settings and tick \"Shared Memory Support\" so OverMount can read it.");
                    _close.Text = "Close";
                }
            }
        }
    }

    static async Task<bool> WaitUntilRunning(CompanionApp app, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < until)
        {
            if (Companions.IsRunning(app)) return true;
            await Task.Delay(500);
        }
        return Companions.IsRunning(app);
    }

    void OpenPage(string url)
    {
        try { Pages.HomePage.Open(url); }
        catch (Win32Exception e) { Say($"Couldn't open {url}: {e.Message}"); }
    }
}
