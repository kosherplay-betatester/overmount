using System.Text.Json;
using Darkmount.Screens;
using Darkmount.Sensors;
using SkiaSharp;

namespace Darkmount.App;

/// <summary>Main window: sidebar navigation, a live preview of the dock, and one page per settings area.</summary>
public sealed class SettingsForm : Form
{
    /// <summary>The settings the form-owned controls were last loaded from (Save compares against it).</summary>
    AppSettings _edit;
    readonly Action<AppSettings> _apply;
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Ui.Back, AutoScroll = true };
    readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Panel, Padding = new Padding(10, 12, 10, 0), AutoScroll = true };
    readonly PictureBox _preview = new() { Size = new Size(200, 150), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Margin = new Padding(30, 6, 10, 10) };
    readonly Label _status = new() { AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(0, 9, 16, 0) };
    readonly List<(Button Button, Control Page)> _pages = [];

    // Dock screen
    readonly ComboBox _mode = Ui.Combo<ScreenMode>(260), _default = Ui.Combo<ScreenKind>(260);
    readonly NumericUpDown _refresh = Ui.Number(1.5m, 60, 0.5m, 1);
    readonly TextBox _hotkey = new() { Width = 220, Font = Ui.Body }, _focusHotkey = new() { Width = 220, Font = Ui.Body };
    readonly CheckBox _smart = Ui.Check("Smart screens: Now playing when a song starts, the focus timer while it runs"),
        _rotate = Ui.Check("Take turns showing these screens (Auto mode, no game running):");
    readonly CheckedListBox _rotation = new()
    {
        CheckOnClick = true, Width = 260, Height = 150, Font = Ui.Body, BackColor = Ui.Panel, ForeColor = Ui.Text, BorderStyle = BorderStyle.None,
        FormattingEnabled = true,
    };
    readonly ComboBox _statsPrimary = Ui.Combo<StatsValue>(200);
    readonly CheckBox _graphLoad = Ui.Check("Load"), _graphTemp = Ui.Check("Temperature"), _graphPower = Ui.Check("Watts");
    readonly NumericUpDown _rotateSeconds = Ui.Number(5, 600, 5), _focusMin = Ui.Number(1, 180), _breakMin = Ui.Number(1, 60),
        _longBreakMin = Ui.Number(1, 120);
    readonly CheckBox _autostart = Ui.Check("Start OverMount with Windows");

    // Animation
    readonly ComboBox _animKind = Ui.Combo<AnimationKind>();
    readonly TextBox _animPath = new() { Width = 360, Font = Ui.Body };

    // Alerts
    readonly CheckBox _cpuOn = Ui.Check("CPU temperature at or above"), _gpuOn = Ui.Check("GPU temperature at or above"),
        _ramOn = Ui.Check("RAM usage at or above"), _vramOn = Ui.Check("VRAM usage at or above"), _fpsOn = Ui.Check("In games, FPS below");
    readonly NumericUpDown _cpuMax = Ui.Number(50, 110), _gpuMax = Ui.Number(50, 110), _ramMax = Ui.Number(50, 100),
        _vramMax = Ui.Number(50, 100), _fpsMin = Ui.Number(5, 240), _fpsSec = Ui.Number(1, 30), _hold = Ui.Number(0, 120);

    // Sensors
    readonly NumericUpDown _gpuIndex = Ui.Number(0, 8);
    readonly TextBox _labels = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f), Size = new Size(620, 300) };

    readonly Func<string>? _status2;
    readonly Func<AppSettings>? _current;
    readonly Label _statusText = new() { AutoSize = true, Font = new Font("Consolas", 10.5f), ForeColor = Ui.Text, Margin = new Padding(0, 4, 0, 0) };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };

    /// <param name="status">Returns a multi-line status report for the Status page (null hides the page).</param>
    public SettingsForm(AppSettings current, Action<AppSettings> apply, Func<string>? status = null,
        KeyboardService? keyboard = null, Darkmount.Dock.DockConnection? dock = null, Macros.MacroManager? macros = null,
        ProfileManager? profiles = null, Func<string?>? currentGame = null,
        Func<Pages.HomeStatus>? home = null, Action<ScreenMode>? setMode = null, Action? togglePause = null,
        Func<AppSettings>? liveSettings = null, RgbEngine? rgb = null, Pages.UpdateActions? updates = null)
    {
        _apply = apply;
        _status2 = status;
        _current = liveSettings;
        _edit = Clone(current);

        // Everything below is laid out in 96-DPI units and scaled to the monitor (175 % on a typical 4K screen) at the end.
        Ui.BeginLayout(this);
        Text = "OverMount";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1140, 720);
        Icon = AppIcon.Window;
        MinimumSize = new Size(960, 600);
        BackColor = Ui.Back;
        ForeColor = Ui.Text;
        Font = Ui.Body;

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 262, BackColor = Ui.Panel };
        var brand = new Label { Text = "OverMount", Font = Ui.Title, ForeColor = Ui.Text, AutoSize = true, Margin = new Padding(6, 4, 0, 14) };
        _nav.Controls.Add(brand);
        var previewBox = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 196, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Panel };
        previewBox.Controls.Add(new Label { Text = "LIVE DOCK", ForeColor = Ui.Dim, Font = new Font("Segoe UI Semibold", 8.5f), AutoSize = true, Margin = new Padding(12, 8, 0, 0) });
        previewBox.Controls.Add(_preview);
        sidebar.Controls.Add(_nav);
        sidebar.Controls.Add(previewBox);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12), BackColor = Ui.Back };
        footer.Controls.Add(Ui.Button("Close", (_, _) => Close()));
        footer.Controls.Add(Ui.Button("Save", (_, _) => Save(), primary: true));
        footer.Controls.Add(_status);

        // Light Mount keyboards have no media dock or display keys: their pages (and the dock preview) are hidden.
        bool hasDock = dock?.Model.HasMediaDock ?? true;
        if (home is not null)
            AddPage("Home", new Pages.HomePage(home, setMode ?? (_ => { }), SelectPage, togglePause ?? (() => { }), hasDock));
        if (keyboard is not null)
        {
            AddPage("Lighting", new Pages.LightingHubPage(liveSettings ?? (() => _edit), apply, keyboard, rgb,
                rgb is null ? null : () => rgb.Fps > 0 ? $"{rgb.Status}, {rgb.Fps:F0} fps" : rgb.Status));
            AddPage("Keys", new Pages.KeysPage(keyboard));
            if (macros is not null) AddPage("Macros", new Pages.MacrosPage(macros, keyboard));
            if (profiles is not null) AddPage("Profiles", new Pages.ProfilesPage(profiles, currentGame ?? (() => null), macros));
        }
        if (hasDock)
        {
            AddPage("Dock screen", DockPage());
            AddPage("Animation", AnimationPage());
        }
        AddPage("Alerts", AlertsPage());
        AddPage("Sensors", SensorsPage());
        if (hasDock)
        {
            if (keyboard is not null) AddPage("Display keys", new Pages.DisplayKeysPage(keyboard));
            if (dock is not null) AddPage("Dock settings", new Pages.DockSettingsPage(dock));
        }
        else previewBox.Visible = false;
        if (updates is not null) AddPage("About & updates", new Pages.AboutPage(updates, liveSettings ?? (() => _edit), apply));
        if (_status2 is not null)
        {
            var sp = new Ui.Page("Status", "Live diagnostics: connection, uploads and sensor readings.");
            sp.AddFull(_statusText);
            AddPage("Status", sp);
            _statusTimer.Tick += (_, _) => _statusText.Text = _status2();
            _statusTimer.Start();
            _statusText.Text = _status2();
        }

        Controls.Add(_content);
        Controls.Add(footer);
        Controls.Add(sidebar);
        LoadValues();
        Activated += (_, _) => RefreshFromLive();
        Select(0);
        Ui.EndLayout(this); // every page is part of the window by now, so all of them are scaled
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Adds a sidebar entry and its page (later phases add keyboard pages here).</summary>
    public void AddPage(string title, Control page)
    {
        var button = new Button
        {
            Text = "   " + title, TextAlign = ContentAlignment.MiddleLeft, Width = 236, Height = 36, FlatStyle = FlatStyle.Flat,
            ForeColor = Ui.Text, BackColor = Ui.Panel, Font = Ui.Body, Cursor = Cursors.Hand, Margin = new Padding(0, 2, 0, 2),
            UseMnemonic = false,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Ui.PanelHover;
        int index = _pages.Count;
        button.Click += (_, _) => Select(index);
        Ui.Add(_nav, button);
        _pages.Add((button, page));
        // Every page stays in the window (hidden until chosen), so the window's scaling reaches all of them.
        page.Visible = false;
        Ui.Add(_content, page);
    }

    /// <summary>Shows the page with this sidebar title (used by the Home page's quick actions).</summary>
    public void SelectPage(string title)
    {
        int i = _pages.FindIndex(p => p.Button.Text.Trim() == title);
        if (i >= 0) Select(i);
    }

    /// <remarks>Pages load their data when they become visible (VisibleChanged), so switching only toggles Visible.</remarks>
    void Select(int index)
    {
        var page = _pages[index].Page;
        // One layout pass for the whole switch (Batch resumes even if a page's show/hide handler fails).
        Ui.Batch(_content, () =>
        {
            foreach (var (_, other) in _pages)
                if (other != page) other.Visible = false;
            _content.AutoScrollPosition = Point.Empty;
            page.Visible = true;
        });
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Button.BackColor = i == index ? Ui.PanelHover : Ui.Panel;
            _pages[i].Button.ForeColor = i == index ? Ui.Accent : Ui.Text;
        }
    }

    /// <summary>Called from the pipeline thread with a frame copy; takes ownership.</summary>
    public void ShowPreview(SKBitmap frame)
    {
        using (frame)
        {
            if (IsDisposed || !IsHandleCreated) return;
            using var data = frame.Encode(SKEncodedImageFormat.Png, 100);
            var image = Image.FromStream(new MemoryStream(data.ToArray()));
            BeginInvoke(() =>
            {
                var old = _preview.Image;
                _preview.Image = image;
                old?.Dispose();
            });
        }
    }

    // ---------------------------------------------------------------- pages

    Control DockPage()
    {
        var p = new Ui.Page("Dock screen", "What the media dock shows and how OverMount behaves.");
        p.Row("Screen", _mode, "Auto shows stats while a game runs");
        p.Row("Default screen (Auto)", _default, "when no game is running");
        p.Row("", _smart);
        p.Row("", _rotate);
        foreach (var kind in Enum.GetValues<ScreenKind>()) _rotation.Items.Add(kind);
        _rotation.Format += (_, e) => { if (e.ListItem is Enum v) e.Value = Ui.Friendly(v); };
        p.Row("Rotation", _rotation);
        p.Row("Next screen every", _rotateSeconds, "seconds");
        p.Row("Refresh every", _refresh, "seconds (minimum ~5 s: each image takes ~2.2 s plus a 3 s rest for the dock)");
        p.Row("Switch-screen hotkey", _hotkey);
        p.Row("", _autostart);
        p.Heading("Stats screen layout");
        p.AddFull(Ui.Note("Choose the CPU/GPU value shown big (the other two are shown small underneath) and what the graphs draw.", 640));
        p.Row("Big number", _statsPrimary);
        var graphs = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        foreach (var c in new[] { _graphLoad, _graphTemp, _graphPower }) { c.Margin = new Padding(0, 4, 18, 0); graphs.Controls.Add(c); }
        p.Row("Graph shows", graphs);
        p.Heading("Focus timer");
        p.AddFull(Ui.Note("A Pomodoro timer for deep work: the dock shows the countdown and the F-keys fill up like a progress " +
                          "bar (Lighting → Studio → live extras). Start or pause it from the tray menu or with the hotkey.", 640));
        p.Row("Focus", _focusMin, "minutes");
        p.Row("Short break", _breakMin, "minutes");
        p.Row("Long break (every 4th)", _longBreakMin, "minutes");
        p.Row("Start / pause hotkey", _focusHotkey);
        p.Heading("Tips");
        p.AddFull(Ui.Note("• If the dock is dark, press a dock button once: the dock only accepts images while awake.\n" +
                          "• When IO Center runs, OverMount pauses and gives the dock back automatically.\n" +
                          "• Exiting restores your own dock settings.", 640));
        return p;
    }

    Control AnimationPage()
    {
        var p = new Ui.Page("Animation", "Built-in themes, your own GIF or video, or a folder of pictures. " +
            "The dock redraws about every 2 seconds, so animations play as an ambient slideshow.");
        p.Row("Source", _animKind);
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(_animPath);
        row.Controls.Add(Ui.Button("Browse…", (_, _) => Browse()));
        p.Row("File or folder", row);
        return p;
    }

    Control AlertsPage()
    {
        var p = new Ui.Page("Alerts", "A red banner appears on the dock (the stats move down so nothing is hidden) " +
            "and the dock refreshes immediately. Alerts are off until you tick the ones you want.");
        p.Row(_cpuOn, Unit(_cpuMax, "°C"));
        p.Row(_gpuOn, Unit(_gpuMax, "°C"));
        p.Row(_ramOn, Unit(_ramMax, "%"));
        p.Row(_vramOn, Unit(_vramMax, "%"));
        var fps = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        fps.Controls.AddRange([_fpsMin, Hint("FPS for at least"), _fpsSec, Hint("seconds")]);
        p.Row(_fpsOn, fps);
        p.Row("Keep alerts visible for", Unit(_hold, "seconds after recovery"));
        return p;
    }

    Control SensorsPage()
    {
        var p = new Ui.Page("Sensors", "Data comes from MSI Afterburner (CPU, GPU, RAM, FPS), RivaTuner (game detection) and, " +
            "optionally, HWiNFO with 'Shared Memory Support' enabled (preferred when available).");
        p.Row("GPU", _gpuIndex, "0 = automatic (the busiest GPU)");
        p.Heading("Advanced: sensor names (JSON)");
        p.AddFull(_labels);
        return p;
    }

    static FlowLayoutPanel Unit(Control c, string unit)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        f.Controls.Add(c);
        f.Controls.Add(Hint(unit));
        return f;
    }

    static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = Ui.Dim, Font = Ui.Body, Margin = new Padding(6, 6, 6, 0) };

    void Browse()
    {
        if ((AnimationKind)_animKind.SelectedItem! == AnimationKind.Folder)
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK) _animPath.Text = dlg.SelectedPath;
            return;
        }
        using var file = new OpenFileDialog { Filter = "Animations and videos|*.gif;*.mp4;*.mkv;*.mov;*.wmv;*.avi;*.webm|All files|*.*" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        _animPath.Text = file.FileName;
        _animKind.SelectedItem = Path.GetExtension(file.FileName).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? AnimationKind.Gif : AnimationKind.Video;
    }

    // ---------------------------------------------------------------- load / save

    void LoadValues()
    {
        _mode.SelectedItem = _edit.Mode;
        _default.SelectedItem = _edit.DefaultScreen;
        _refresh.Value = Math.Clamp(_edit.RefreshMs / 1000m, 1.5m, 60);
        _hotkey.Text = _edit.Hotkey;
        _autostart.Checked = _edit.StartWithWindows;
        _smart.Checked = _edit.SmartScreens;
        _rotate.Checked = _edit.RotateScreens;
        for (int i = 0; i < _rotation.Items.Count; i++) _rotation.SetItemChecked(i, _edit.Rotation.Contains((ScreenKind)_rotation.Items[i]));
        _rotateSeconds.Value = Ui.Clamp(_rotateSeconds, _edit.RotateSeconds);
        _focusMin.Value = Ui.Clamp(_focusMin, _edit.FocusMinutes);
        _breakMin.Value = Ui.Clamp(_breakMin, _edit.BreakMinutes);
        _longBreakMin.Value = Ui.Clamp(_longBreakMin, _edit.LongBreakMinutes);
        _focusHotkey.Text = _edit.FocusHotkey;
        var layout = _edit.StatsLayout ?? new();
        _statsPrimary.SelectedItem = layout.Primary;
        (_graphLoad.Checked, _graphTemp.Checked, _graphPower.Checked) = (layout.GraphLoad, layout.GraphTemperature, layout.GraphPower);
        _animKind.SelectedItem = _edit.AnimationKind;
        _animPath.Text = _edit.AnimationPath ?? "";

        var a = _edit.Alerts;
        (_cpuOn.Checked, _cpuMax.Value) = (a.CpuTempEnabled, Ui.Clamp(_cpuMax, a.CpuTempMax));
        (_gpuOn.Checked, _gpuMax.Value) = (a.GpuTempEnabled, Ui.Clamp(_gpuMax, a.GpuTempMax));
        (_ramOn.Checked, _ramMax.Value) = (a.RamEnabled, Ui.Clamp(_ramMax, a.RamMaxPercent));
        (_vramOn.Checked, _vramMax.Value) = (a.VramEnabled, Ui.Clamp(_vramMax, a.VramMaxPercent));
        (_fpsOn.Checked, _fpsMin.Value, _fpsSec.Value) = (a.FpsEnabled, Ui.Clamp(_fpsMin, a.FpsMin), Ui.Clamp(_fpsSec, a.FpsSeconds));
        _hold.Value = Ui.Clamp(_hold, a.HoldSeconds);


        _gpuIndex.Value = Ui.Clamp(_gpuIndex, _edit.Sensors.GpuIndex);
        _labels.Text = JsonSerializer.Serialize(_edit.Sensors, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Settings the controls on the Dock screen, Animation, Alerts and Sensors pages own.</summary>
    static readonly string[] FormFields =
    [
        nameof(AppSettings.Mode), nameof(AppSettings.DefaultScreen), nameof(AppSettings.RefreshMs), nameof(AppSettings.Hotkey),
        nameof(AppSettings.StartWithWindows), nameof(AppSettings.SmartScreens), nameof(AppSettings.RotateScreens),
        nameof(AppSettings.Rotation), nameof(AppSettings.RotateSeconds), nameof(AppSettings.FocusMinutes),
        nameof(AppSettings.BreakMinutes), nameof(AppSettings.LongBreakMinutes), nameof(AppSettings.FocusHotkey),
        nameof(AppSettings.AnimationKind), nameof(AppSettings.AnimationPath), nameof(AppSettings.Alerts), nameof(AppSettings.Sensors),
        nameof(AppSettings.StatsLayout),
    ];

    /// <summary><paramref name="basis"/> with the form-owned values taken from the controls; null if a value is invalid.</summary>
    AppSettings? FromControls(AppSettings basis, bool report)
    {
        if (!HotkeyWindow.TryParse(_hotkey.Text, out _, out _) || !HotkeyWindow.TryParse(_focusHotkey.Text, out _, out _))
        {
            if (report) MessageBox.Show(this, "Hotkeys must look like Ctrl+Alt+Shift+D.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        SensorOptions sensors;
        try { sensors = JsonSerializer.Deserialize<SensorOptions>(_labels.Text) ?? new(); }
        catch (JsonException e)
        {
            if (report) MessageBox.Show(this, $"The sensor names are not valid JSON:\n{e.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        sensors.GpuIndex = (int)_gpuIndex.Value;

        var s = Clone(basis);
        s.Mode = (ScreenMode)_mode.SelectedItem!;
        s.DefaultScreen = (ScreenKind)_default.SelectedItem!;
        s.RefreshMs = (int)(_refresh.Value * 1000);
        s.Hotkey = _hotkey.Text.Trim();
        s.StartWithWindows = _autostart.Checked;
        s.SmartScreens = _smart.Checked;
        s.RotateScreens = _rotate.Checked;
        s.Rotation = _rotation.CheckedItems.Cast<ScreenKind>().ToList();
        s.RotateSeconds = (int)_rotateSeconds.Value;
        s.FocusMinutes = (int)_focusMin.Value;
        s.BreakMinutes = (int)_breakMin.Value;
        s.LongBreakMinutes = (int)_longBreakMin.Value;
        s.FocusHotkey = _focusHotkey.Text.Trim();
        s.StatsLayout = new StatsLayout
        {
            Primary = (StatsValue)(_statsPrimary.SelectedItem ?? StatsValue.Temperature),
            GraphLoad = _graphLoad.Checked, GraphTemperature = _graphTemp.Checked, GraphPower = _graphPower.Checked,
        };
        s.AnimationKind = (AnimationKind)_animKind.SelectedItem!;
        s.AnimationPath = string.IsNullOrWhiteSpace(_animPath.Text) ? null : _animPath.Text.Trim();
        s.Alerts = new AlertSettings
        {
            CpuTempEnabled = _cpuOn.Checked, CpuTempMax = (double)_cpuMax.Value,
            GpuTempEnabled = _gpuOn.Checked, GpuTempMax = (double)_gpuMax.Value,
            RamEnabled = _ramOn.Checked, RamMaxPercent = (double)_ramMax.Value,
            VramEnabled = _vramOn.Checked, VramMaxPercent = (double)_vramMax.Value,
            FpsEnabled = _fpsOn.Checked, FpsMin = (double)_fpsMin.Value, FpsSeconds = (double)_fpsSec.Value,
            HoldSeconds = (double)_hold.Value,
        };
        s.Sensors = sensors;
        return s;
    }

    /// <summary>Names of the form-owned settings whose control values differ from what was loaded.</summary>
    static IEnumerable<string> Changed(AppSettings edited, AppSettings loaded) =>
        FormFields.Where(name =>
        {
            var p = typeof(AppSettings).GetProperty(name)!;
            return JsonSerializer.Serialize(p.GetValue(edited)) != JsonSerializer.Serialize(p.GetValue(loaded));
        });

    /// <remarks>
    /// Other pages, the tray menu and profiles change settings while this window is open, so Save writes only the
    /// values the user changed here, on top of the live settings, and never puts stale ones back.
    /// </remarks>
    void Save()
    {
        if (FromControls(_edit, report: true) is not { } edited) return;
        var s = Clone(_current?.Invoke() ?? _edit);
        foreach (var name in Changed(edited, _edit))
        {
            var p = typeof(AppSettings).GetProperty(name)!;
            p.SetValue(s, p.GetValue(edited));
        }
        s.DockIdleSeconds = 1;
        _apply(s);
        _edit = Clone(s);
        LoadValues();
        _status.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>When the window comes back to the front, show changes made elsewhere (unless there are unsaved edits).</summary>
    void RefreshFromLive()
    {
        if (_current is null || FromControls(_edit, report: false) is not { } edited || Changed(edited, _edit).Any()) return;
        _edit = Clone(_current());
        LoadValues();
    }

    static Color ToColor(string hex)
    {
        try { var c = Darkmount.Keyboard.Rgb.Parse(hex); return Color.FromArgb(c.R, c.G, c.B); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return Color.OrangeRed; }
    }

    static string Hex(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    static AppSettings Clone(AppSettings s) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Ui.UseDarkTitleBar(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _statusTimer.Dispose();
        _preview.Image?.Dispose();
        base.OnFormClosed(e);
    }
}


