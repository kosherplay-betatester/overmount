using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Darkmount.App.Input;
using Darkmount.App.Overlays;
using Darkmount.App.Sources;
using Darkmount.Dock;
using Darkmount.QLink;
using Darkmount.Screens;

namespace Darkmount.App;

/// <summary>Tray icon, menu and the lifetime of the dock connection and frame pipeline.</summary>
public sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _auto = new("Auto (stats in games)"), _stats = new("Stats"), _anim = new("Animation"),
        _dockDefault = new("be quiet! default screen"), _moreScreens = new("More screens"), _focusMenu = new("Focus timer"),
        _focusToggle = new("Start focus");
    readonly ToolStripMenuItem _pause = new("Pause dock"), _autostart = new("Start with Windows");
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    readonly System.Threading.Timer _tickTimer;
    readonly HotkeyWindow _hotkey = new(), _focusHotkey = new();
    readonly DockConnection _dock;
    readonly SynchronizationContext _ui;
    readonly KeyboardService _keyboard;
    readonly DockActivityWatcher _dockActivity = new();
    readonly RgbEngine _rgb;
    readonly Macros.MacroManager _macros = new();
    readonly ProfileManager _profiles;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly KeyPressFeed _keyFeed;
    readonly AudioStatus _audioStatus = new();
    readonly PomodoroTimer _pomodoro = new();
    readonly System.Windows.Forms.Timer _overlayTimer = new() { Interval = 150 };
    volatile OverlayState _overlayState = new();
    HeldModifier _heldModifier;
    double _heldSince;

    AppSettings _settings;
    FramePipeline _pipeline;
    SettingsForm? _settingsForm;
    bool _toldToWake, _toldAboutIoCenter, _exiting;
    Action? _balloonClick;
    readonly ToolStripMenuItem _takeControl = new("Take control back from IO Center"), _updateItem = new("Check for updates…");
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 20_000 };
    readonly System.Windows.Forms.Timer _companionsTimer = new();
    Setup.ReleaseInfo? _latestRelease;
    DateTime _lastUpdateCheck;
    bool _updating;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);

        _dock = new DockConnection(OpenKeyboard, new DockConfigGuard(DockConfigGuard.DefaultPath), IoCenterDetector.IsRunning);
        _dock.Log += Log.Write;
        _dock.StateChanged += s => _ui.Post(_ => OnDockState(s), null);

        ApplyFocusDurations();
        _pomodoro.PhaseChanged += phase => _ui.Post(_ => OnFocusPhase(phase), null);
        _pipeline = CreatePipeline();
        _keyboard = new KeyboardService(_dock);
        _profiles = new ProfileManager(_keyboard, () => _settings, ApplySettings);
        _keyFeed = new KeyPressFeed(_clock); // reactive effects: records only which key was pressed when, never text
        _rgb = new RgbEngine(() => _settings, () => _pipeline.LastSnapshot, () => _pipeline.LastAlerts.Count > 0,
            () => _overlayState, _keyFeed, _clock);
        _overlayTimer.Tick += (_, _) => UpdateOverlayState();
        _profiles.EdgeLights = () => _rgb.ReadEdgeLights() is { Count: > 0 } live ? live
            : _dock.Model == KeyboardModel.DarkMount ? Darkmount.Keyboard.Lamps.DarkmountKeys.DefaultEdgeLights : new Dictionary<int, int>();

        _tray = new NotifyIcon { Icon = AppIcon.Tray(TrayState.Idle), Text = "OverMount", Visible = true, ContextMenuStrip = BuildMenu() };
        _tray.DoubleClick += (_, _) => ShowSettings();
        _tray.BalloonTipClicked += (_, _) => { var action = _balloonClick; _balloonClick = null; action?.Invoke(); };

        _hotkey.Pressed += CycleScreen;
        if (!_hotkey.Register(_settings.Hotkey)) Log.Write($"Hotkey '{_settings.Hotkey}' could not be registered");
        _focusHotkey.Pressed += ToggleFocus;
        if (!_focusHotkey.Register(_settings.FocusHotkey)) Log.Write($"Hotkey '{_settings.FocusHotkey}' could not be registered");

        if (_settings.StartWithWindows != Autostart.IsEnabled()) TrySetAutostart(_settings.StartWithWindows);

        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(exitSignal, (_, _) => _ui.Post(_ => ExitThread(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        _tickTimer = new System.Threading.Timer(_ => _dock.Tick(), null, 0, 1000);
        _uiTimer.Tick += async (_, _) =>
        {
            UpdateStatus();
            await _profiles.OnGame(_pipeline.LastSnapshot?.GameName); // per-game profiles
        };
        _uiTimer.Start();
        _updateTimer.Tick += async (_, _) => await AutoCheckForUpdates();
        _updateTimer.Start();
        if (!_settings.CompanionsOffered)
        {
            // Once: offer the sensor apps if any is missing. Right after logon give Afterburner time to start on its own.
            _companionsTimer.Interval = Environment.TickCount64 < 5 * 60 * 1000 ? 90_000 : 6_000;
            _companionsTimer.Tick += async (_, _) => { _companionsTimer.Stop(); await OfferCompanionsOnce(); };
            _companionsTimer.Start();
        }
        _overlayTimer.Start();
        _pipeline.Start();
        _rgb.Start();
        Log.Write("OverMount started");
    }

    static IHidTransport? OpenKeyboard() => HidSharpTransport.TryOpen();

    ContextMenuStrip BuildMenu()
    {
        _auto.Click += (_, _) => SetMode(ScreenMode.Auto);
        _stats.Click += (_, _) => SetMode(ScreenMode.Stats);
        _anim.Click += (_, _) => SetMode(ScreenMode.Animation);
        _dockDefault.Click += (_, _) => SetMode(ScreenMode.DockDefault);
        foreach (var (mode, text) in new[] { (ScreenMode.NowPlaying, "Now playing"), (ScreenMode.Clock, "Clock & calendar"),
                     (ScreenMode.Network, "Network"), (ScreenMode.FocusTimer, "Focus timer") })
            _moreScreens.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => SetMode(mode)) { Tag = mode });

        _focusToggle.Click += (_, _) => ToggleFocus();
        _focusMenu.DropDownItems.AddRange([
            _focusToggle,
            new ToolStripMenuItem("Skip to next phase", null, (_, _) => { _pomodoro.Skip(); _pipeline.RefreshNow(); }),
            new ToolStripMenuItem("Reset", null, (_, _) => { _pomodoro.Reset(); _pipeline.RefreshNow(); }),
        ]);

        var animations = new ToolStripMenuItem("Animation source");
        foreach (var kind in new[] { AnimationKind.Plasma, AnimationKind.Matrix, AnimationKind.Starfield })
            animations.DropDownItems.Add(kind.ToString(), null, (_, _) => SetAnimation(kind, null));
        animations.DropDownItems.Add(new ToolStripSeparator());
        animations.DropDownItems.Add("GIF or video file…", null, (_, _) => ChooseAnimationFile());
        animations.DropDownItems.Add("Folder of pictures…", null, (_, _) => ChooseAnimationFolder());

        _pause.Click += (_, _) => { _dock.Paused = !_dock.Paused; _pause.Checked = _dock.Paused; _dock.Tick(); };
        _autostart.Click += (_, _) =>
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
            TrySetAutostart(_settings.StartWithWindows);
            SaveSettings();
        };

        _takeControl.Click += (_, _) => TakeControlFromIoCenter(ask: false);
        _updateItem.Click += async (_, _) =>
        {
            if (_latestRelease is { } known && Setup.UpdateChecker.IsNewer(known, Setup.Installer.CurrentVersion)) { OfferUpdate(known, userAsked: true); return; }
            var release = await CheckForUpdates();
            if (release is not null && Setup.UpdateChecker.IsNewer(release, Setup.Installer.CurrentVersion)) OfferUpdate(release, userAsked: true);
            else Balloon("OverMount", release is null ? "Couldn't reach GitHub to check for updates."
                : $"You have the latest version ({Setup.Installer.CurrentVersion.ToString(3)}).");
        };
        _menu.Items.AddRange([
            _status, _takeControl, new ToolStripSeparator(),
            _auto, _stats, _anim, _moreScreens, _dockDefault, animations, new ToolStripSeparator(),
            _focusMenu,
            _pause, new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()), _autostart,
            new ToolStripMenuItem("Stop all running macros", null, (_, _) => _macros.StopAll()),
            new ToolStripMenuItem("Set up sensor apps…", null, (_, _) => ShowCompanions()),
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenLogs()), _updateItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        _menu.Opening += (_, _) => UpdateMenuChecks();
        return _menu;
    }

    void UpdateMenuChecks()
    {
        _auto.Checked = _settings.Mode == ScreenMode.Auto;
        _stats.Checked = _settings.Mode == ScreenMode.Stats;
        _anim.Checked = _settings.Mode == ScreenMode.Animation;
        _dockDefault.Checked = _settings.Mode == ScreenMode.DockDefault;
        foreach (ToolStripMenuItem item in _moreScreens.DropDownItems) item.Checked = item.Tag is ScreenMode m && m == _settings.Mode;
        _moreScreens.Checked = _settings.Mode is ScreenMode.NowPlaying or ScreenMode.Clock or ScreenMode.Network or ScreenMode.FocusTimer;
        bool hasDock = _dock.Model.HasMediaDock;
        foreach (var item in new ToolStripItem[] { _auto, _stats, _anim, _moreScreens, _dockDefault, _pause })
            item.Visible = hasDock;
        _focusToggle.Text = _pomodoro.IsRunning ? "Pause" : _pomodoro.IsPaused ? "Resume" : "Start focus";
        _takeControl.Visible = _dock.State == DockState.PausedForIoCenter;
        _pause.Checked = _dock.Paused;
        _autostart.Checked = Autostart.IsEnabled();
        UpdateStatus();
    }

    void SetMode(ScreenMode mode)
    {
        _settings.Mode = mode;
        _pipeline.Switcher.ClearManual();
        SaveSettings();
        _pipeline.RefreshNow();
    }

    void SetAnimation(AnimationKind kind, string? path)
    {
        _settings.AnimationKind = kind;
        _settings.AnimationPath = path;
        if (_settings.Mode is ScreenMode.Stats or ScreenMode.DockDefault) _settings.Mode = ScreenMode.Animation;
        SaveSettings();
        _pipeline.RefreshNow();
    }

    void ChooseAnimationFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a GIF or video for the dock",
            Filter = "Animations and videos|*.gif;*.mp4;*.mkv;*.mov;*.wmv;*.avi;*.webm|All files|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var kind = Path.GetExtension(dlg.FileName).Equals(".gif", StringComparison.OrdinalIgnoreCase) ? AnimationKind.Gif : AnimationKind.Video;
        SetAnimation(kind, dlg.FileName);
    }

    void ChooseAnimationFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder of pictures to show on the dock" };
        if (dlg.ShowDialog() == DialogResult.OK) SetAnimation(AnimationKind.Folder, dlg.SelectedPath);
    }

    FramePipeline CreatePipeline()
    {
        var pipeline = new FramePipeline(_dock, () => _settings) { DockInUse = _dockActivity.ActiveWithin, Pomodoro = () => _pomodoro.Info() };
        pipeline.FrameRendered += OnFrame;
        return pipeline;
    }

    /// <summary>Focus hotkey / tray: start, pause or resume the focus timer.</summary>
    void ToggleFocus()
    {
        _pomodoro.Toggle();
        _pipeline.RefreshNow();
    }

    void ApplyFocusDurations()
    {
        _pomodoro.FocusDuration = TimeSpan.FromMinutes(Math.Clamp(_settings.FocusMinutes, 1, 180));
        _pomodoro.BreakDuration = TimeSpan.FromMinutes(Math.Clamp(_settings.BreakMinutes, 1, 60));
        _pomodoro.LongBreakDuration = TimeSpan.FromMinutes(Math.Clamp(_settings.LongBreakMinutes, 1, 120));
    }

    string _lastFocusPhase = PomodoroInfo.Ready;

    void OnFocusPhase(string phase)
    {
        _pipeline.RefreshNow();
        bool breakEnded = _lastFocusPhase is PomodoroInfo.Break or PomodoroInfo.LongBreak;
        _lastFocusPhase = phase;
        string? text = phase switch
        {
            PomodoroInfo.Focus => $"Focus for {_settings.FocusMinutes} minutes. You've got this.",
            PomodoroInfo.Break => $"Time for a {_settings.BreakMinutes}-minute break. Stand up and stretch!",
            PomodoroInfo.LongBreak => $"Great work! Take a {_settings.LongBreakMinutes}-minute break.",
            PomodoroInfo.Ready when breakEnded => $"Break over. Press {_settings.FocusHotkey} (or the tray menu) to start the next focus.",
            _ => null,
        };
        if (text is not null) Balloon("Focus timer", text);
    }

    /// <summary>Hotkey: Auto (dashboard) → Animation → be quiet! default screen → Auto.</summary>
    void CycleScreen() => SetMode(AutoSwitcher.NextMode(_settings.Mode));

    void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Maximized;
            open.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, ApplySettings, StatusReport, _keyboard, _dock, _macros,
            _profiles, () => _pipeline.LastSnapshot?.GameName, HomeStatus, SetMode,
            () => { _dock.Paused = !_dock.Paused; _dock.Tick(); }, liveSettings: () => _settings, rgb: _rgb,
            updates: new Pages.UpdateActions(CheckForUpdates, r => OfferUpdate(r, userAsked: true), () => _latestRelease));
        _settingsForm.WindowState = FormWindowState.Maximized; // the pages (keyboard pictures, gallery) use the room
        _settingsForm.Show();
    }

    DateTime _dynamicLightingCheckedAt;
    bool _dynamicLightingOn;

    Pages.HomeStatus HomeStatus()
    {
        var model = _dock.Model;
        bool connected = _dock.State is DockState.Connected or DockState.Ready or DockState.NoMediaDock or DockState.KeyboardOnly;
        var hints = _pipeline.LastSnapshot?.Hints ?? [];
        string dock = !model.HasMediaDock ? "No screen on this keyboard"
            : _dock.ShowingDockDefault || _settings.Mode == ScreenMode.DockDefault ? "be quiet! default screen"
            : _dock.State == DockState.Ready ? $"{_pipeline.CurrentScreen} (updates every ~5 s)"
            : _dock.State == DockState.NoMediaDock ? "Media dock not attached" : "Waiting for the keyboard";
        if (DateTime.UtcNow - _dynamicLightingCheckedAt > TimeSpan.FromSeconds(15))
        {
            _dynamicLightingCheckedAt = DateTime.UtcNow;
            try
            {
                var path = Darkmount.Keyboard.Lamps.HidSharpLampArrayTransport.Find(productId: model.ProductId)?.Device.DevicePath;
                _dynamicLightingOn = Darkmount.Keyboard.Lamps.DynamicLighting.Read(path).WindowsMayDrive;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        bool ioCenter = IoCenterDetector.IsRunning();
        var checks = new List<Pages.SetupCheck>
        {
            new("Keyboard connected", connected, false,
                connected ? $"{model.Name} is connected." : _dock.LastError ?? "Plug in the keyboard's USB cable."),
            new("IO Center is closed", !ioCenter, false,
                ioCenter ? "IO Center is running, so OverMount has paused. Right-click its tray icon → Exit." : "OverMount controls the keyboard.",
                "Close IO Center", () => TakeControlFromIoCenter(ask: true)),
            new("CPU temperature", _pipeline.LastSnapshot?.CpuTemp is not null, false,
                _pipeline.LastSnapshot?.CpuTemp is not null ? "CPU temperature is being read."
                    : "Windows can't read CPU temperature by itself. Set up OverMount's own CPU sensor (one Windows permission " +
                      "prompt), or run MSI Afterburner or HWiNFO.", "Set up", ShowCompanions),
            new("MSI Afterburner (optional)", !hints.Contains(Darkmount.Sensors.SensorHub.HintAfterburner), true,
                "Extra sensors (GPU watts on AMD and Intel cards) and it starts RivaTuner with Windows.", "Set up", ShowCompanions),
            new("RivaTuner Statistics Server is running", !hints.Contains(Darkmount.Sensors.SensorHub.HintRtss), false,
                "Detects the running game (FPS row, 1% lows, per-game profiles). MSI Afterburner starts it.", "Set up", ShowCompanions),
            new("HWiNFO shared memory (optional)", !hints.Contains(Darkmount.Sensors.SensorHub.HintHwInfo), true,
                "Optional, more precise sensors: HWiNFO → Settings → Shared Memory Support."),
            new("Windows Dynamic Lighting is off for the keyboard", !_dynamicLightingOn, false,
                _dynamicLightingOn ? "Windows is driving the keyboard LEDs, so RGB effects can't run. Turn off \"Use Dynamic Lighting on my devices\"." : "RGB effects can drive the LEDs.",
                "Open Windows lighting settings", () => Pages.HomePage.Open("ms-settings:personalization-lighting")),
        };
        if (model.HasMediaDock)
            checks.Add(new("Dock screen is awake", !_dock.DockUnresponsive, false,
                _dock.DockUnresponsive ? "Press any dock button once — the dock only accepts pictures while its screen is on." : "The dock is receiving pictures."));
        checks.Add(new("Starts with Windows", Autostart.IsEnabled(), true, "So your lighting, macros and dashboard are always on.",
            "Turn on", () => { _settings.StartWithWindows = true; TrySetAutostart(true); SaveSettings(); }));

        int macros = _macros.Macros.Count(m => m.Enabled);
        return new(
            connected ? model.Name : "Not connected",
            dock,
            _settings.RgbEnabled ? _rgb.Status : "Keyboard's own effect",
            _profiles.ActiveProfile ?? "No profile applied",
            macros == 0 ? "No macros yet" : $"{macros} active",
            checks);
    }

    /// <summary>
    /// Lock keys, held modifier (after 0.6 s, so quick shortcuts and gaming don't flash the helper), speaker volume
    /// (shown for 2 s after it changes), microphone mute and the focus timer, for the lighting overlays.
    /// </summary>
    void UpdateOverlayState()
    {
        var o = _settings.Overlays;
        if (!_settings.RgbEnabled) { _overlayState = new(); return; }
        if (o.VolumeBar || o.MicMute) _audioStatus.Poll();

        var mod = Down(0x5B) || Down(0x5C) ? HeldModifier.Win
            : Down(0x11) && !Down(0x12) ? HeldModifier.Ctrl
            : Down(0x12) && !Down(0x11) ? HeldModifier.Alt : HeldModifier.None;
        double now = _clock.Elapsed.TotalSeconds;
        if (mod != _heldModifier) { _heldModifier = mod; _heldSince = now; }

        var pomodoro = _pomodoro.Info();
        bool timing = pomodoro.Phase is PomodoroInfo.Focus or PomodoroInfo.Break or PomodoroInfo.LongBreak && pomodoro.Total > TimeSpan.Zero;
        _overlayState = new OverlayState
        {
            CapsLock = Control.IsKeyLocked(Keys.CapsLock),
            NumLock = Control.IsKeyLocked(Keys.NumLock),
            ScrollLock = Control.IsKeyLocked(Keys.Scroll),
            ShowVolume = _audioStatus.Volume is not null && DateTime.UtcNow - _audioStatus.VolumeChangedUtc < TimeSpan.FromSeconds(2),
            Volume = _audioStatus.Volume ?? 0,
            SpeakersMuted = _audioStatus.SpeakersMuted,
            MicMuted = _audioStatus.MicMuted == true,
            Modifier = now - _heldSince >= 0.6 ? mod : HeldModifier.None,
            Timer = timing ? (1 - pomodoro.Remaining / pomodoro.Total, pomodoro.Phase == PomodoroInfo.Focus) : null,
        };
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    /// <summary>
    /// IO Center keeps driving the keyboard from its tray icon even after its window is closed, so OverMount stays
    /// paused while IO_Center.exe runs. This closes it (politely first, then for sure), un-freezes lighting it left in
    /// real-time mode, and reconnects.
    /// </summary>
    async void TakeControlFromIoCenter(bool ask)
    {
        if (!IoCenterDetector.IsRunning()) { _dock.Tick(); return; }
        if (ask && MessageBox.Show("IO Center is running (also when only its tray icon is left), so OverMount has paused.\n\n" +
                "Close IO Center now and let OverMount take over the keyboard?", "OverMount",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        bool closed = await Task.Run(() =>
        {
            foreach (var p in Process.GetProcessesByName("IO_Center"))
            {
                using (p)
                {
                    try
                    {
                        if (p.CloseMainWindow() && p.WaitForExit(3000)) continue;
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        Log.Write($"Closing IO Center failed: {e.Message}");
                    }
                }
            }
            return !IoCenterDetector.IsRunning();
        });
        if (!closed)
        {
            MessageBox.Show("IO Center could not be closed automatically. Right-click its tray icon and choose Exit.", "OverMount",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Log.Write("IO Center closed; taking the keyboard back");
        await Task.Run(_dock.Tick);
        try
        {
            await _keyboard.Run(q =>
            {
                var lighting = new Darkmount.Keyboard.Lighting(q);
                if (lighting.GetMode() != Darkmount.Keyboard.LightingMode.Realtime) return;
                KeyboardBackupGuard.EnsureBackup(q);
                lighting.SetMode(Darkmount.Keyboard.LightingMode.General);
            });
        }
        catch (Exception e) when (e is KeyboardUnavailableException or IOException or TimeoutException or QLinkException)
        {
            Log.Write($"Lighting check after IO Center failed: {e.Message}");
        }
        _pipeline.RefreshNow();
        Balloon("OverMount", "IO Center is closed. OverMount is back in control.");
    }

    string StatusReport()
    {
        var s = _pipeline.LastSnapshot;
        static string V(double? v, string unit, string fmt = "F0") => v is { } x ? x.ToString(fmt) + unit : "--";
        var lines = new List<string>
        {
            $"Dock           {_dock.State}{(_dock.DockUnresponsive ? " (not responding: press a dock button)" : "")}",
            $"Screen         {_pipeline.CurrentScreen}",
            $"Frames         {_dock.FramesUploaded} uploaded, {_dock.FramesStalled} stalled",
            $"Last upload    {(_dock.LastUploadDuration.TotalMilliseconds > 0 ? $"{_dock.LastUploadDuration.TotalMilliseconds:F0} ms" : "--")}",
            $"RGB            {_rgb.Status}{(_rgb.Fps > 0 ? $" at {_rgb.Fps:F0} fps" : "")}",
            "",
            $"CPU            {V(s?.CpuTemp, " °C")}   {V(s?.CpuLoad, " %")}   {V(s?.CpuPower, " W")}",
            $"GPU            {V(s?.GpuTemp, " °C")}   {V(s?.GpuLoad, " %")}   {V(s?.GpuPower, " W")}",
            $"RAM            {V(s?.RamUsedMb / 1024, " GB", "F1")} / {V(s?.RamTotalMb / 1024, " GB", "F1")}",
            $"VRAM           {V(s?.VramUsedMb / 1024, " GB", "F1")} / {V(s?.VramTotalMb / 1024, " GB", "F1")}",
            $"Game           {s?.GameName ?? "none"}   FPS {V(s?.Fps, "")}   {s?.FpsLowLabel} {V(s?.FpsLow, "")}",
        };
        if (s?.Hints is { Count: > 0 } hints)
        {
            lines.Add("");
            lines.AddRange(hints.Select(h => "• " + h));
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Applies settings saved in the settings window.</summary>
    void ApplySettings(AppSettings updated)
    {
        bool sensorsChanged = System.Text.Json.JsonSerializer.Serialize(updated.Sensors)
                              != System.Text.Json.JsonSerializer.Serialize(_settings.Sensors);
        _settings = updated;
        SaveSettings();
        if (!_hotkey.Register(updated.Hotkey))
            Balloon("OverMount", $"The hotkey '{updated.Hotkey}' is not available.", ToolTipIcon.Warning);
        if (!_focusHotkey.Register(updated.FocusHotkey))
            Balloon("OverMount", $"The focus-timer hotkey '{updated.FocusHotkey}' is not available.", ToolTipIcon.Warning);
        TrySetAutostart(updated.StartWithWindows);
        ApplyFocusDurations();
        if (sensorsChanged) RestartPipeline();
        _pipeline.RefreshNow();
    }

    void RestartPipeline()
    {
        _pipeline.FrameRendered -= OnFrame;
        var old = _pipeline;
        Task.Run(old.Dispose); // may wait for an upload in progress; never on the UI thread
        _pipeline = CreatePipeline();
        _pipeline.Start();
    }

    void OnFrame(SkiaSharp.SKBitmap frame)
    {
        if (_settingsForm is { IsDisposed: false } form) form.ShowPreview(frame);
        else frame.Dispose();
    }

    // ---------------------------------------------------------------- sensor apps

    /// <summary>Opens the sensor-apps assistant (OverMount's CPU sensor; Afterburner, RivaTuner, HWiNFO).</summary>
    async void ShowCompanions()
    {
        try { await Setup.CompanionsDialog.OpenAsync(cpuTempAvailable: _pipeline.LastSnapshot?.CpuTemp is not null); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Log.Write($"Sensor apps window failed: {e.Message}");
        }
    }

    /// <summary>
    /// First start: if a sensor app is missing or not running, show the assistant once (it asks before doing anything).
    /// People who saw it before OverMount had its own CPU sensor get it once more when CPU temperature is missing.
    /// </summary>
    async Task OfferCompanionsOnce()
    {
        bool cpuTemp = _pipeline.LastSnapshot?.CpuTemp is not null;
        if (_exiting || (_settings.CompanionsOffered && (_settings.CpuSensorOffered || cpuTemp))) return;
        bool needed;
        try { needed = await Setup.CompanionsDialog.NeedsAttentionAsync(cpuTemp); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Log.Write($"Sensor apps check failed: {e.Message}");
            return;
        }
        _settings.CompanionsOffered = true;
        _settings.CpuSensorOffered = true;
        SaveSettings();
        if (needed && !_exiting) ShowCompanions();
    }

    // ---------------------------------------------------------------- updates

    async Task<Setup.ReleaseInfo?> CheckForUpdates()
    {
        _lastUpdateCheck = DateTime.UtcNow;
        var release = await Setup.UpdateChecker.GetLatestAsync();
        if (release is not null) _latestRelease = release;
        bool newer = release is not null && Setup.UpdateChecker.IsNewer(release, Setup.Installer.CurrentVersion);
        _updateItem.Text = newer ? $"Update to {release!.Version.ToString(3)}…" : "Check for updates…";
        _updateItem.Font = newer ? new Font(_menu.Font, FontStyle.Bold) : _menu.Font;
        return release;
    }

    /// <summary>First check 20 s after start, then twice a day (release builds only, and only if the user allows it).</summary>
    async Task AutoCheckForUpdates()
    {
        _updateTimer.Interval = 60 * 60 * 1000;
        if (!_settings.CheckForUpdates || Setup.Installer.IsDeveloperBuild || _updating) return;
        if (DateTime.UtcNow - _lastUpdateCheck < TimeSpan.FromHours(12)) return;
        var release = await CheckForUpdates();
        if (release is null || !Setup.UpdateChecker.IsNewer(release, Setup.Installer.CurrentVersion)) return;
        if (release.Version.ToString(3) == _settings.SkippedUpdate) return;
        Balloon($"OverMount {release.Version.ToString(3)} is available", "Click here to see what's new and update.",
            onClick: () => OfferUpdate(release, userAsked: false), ms: 10000);
    }

    async void OfferUpdate(Setup.ReleaseInfo release, bool userAsked)
    {
        if (_updating) return;
        switch (Setup.UpdateDialog.Offer(release, _settingsForm is { IsDisposed: false } f ? f : null))
        {
            case Setup.UpdateChoice.UpdateNow:
                _updating = true;
                try { await Setup.UpdateDialog.DownloadAndInstall(release); }
                finally { _updating = false; }
                break;
            case Setup.UpdateChoice.Skip:
                _settings.SkippedUpdate = release.Version.ToString(3);
                SaveSettings();
                break;
            default:
                if (!userAsked) _lastUpdateCheck = DateTime.UtcNow; // ask again in 12 hours
                break;
        }
    }

    /// <summary>Shows a tray notification; <paramref name="onClick"/> runs if the user clicks this one.</summary>
    void Balloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, Action? onClick = null, int ms = 5000)
    {
        _balloonClick = onClick;
        _tray.ShowBalloonTip(ms, title, text, icon);
    }

    void OnDockState(DockState state)
    {
        Log.Write($"Dock state: {state}");
        if (_exiting) return; // the tray icon is already gone
        UpdateStatus();
        if (state == DockState.PausedForIoCenter && !_toldAboutIoCenter)
        {
            _toldAboutIoCenter = true;
            Balloon("IO Center has the keyboard",
                "OverMount paused while IO Center runs. Click here to close IO Center and take control back.",
                onClick: () => TakeControlFromIoCenter(ask: false), ms: 8000);
        }
        else if (state != DockState.PausedForIoCenter) _toldAboutIoCenter = false;
        if (state == DockState.Ready && !_toldToWake)
        {
            _toldToWake = true;
            Balloon("OverMount is on the dock",
                "If the dock screen is dark, press a dock button once to wake it.");
        }
    }

    void UpdateStatus()
    {
        if (_exiting) return;
        string state = _dock.State switch
        {
            DockState.Connected or DockState.Ready when _dock.DockUnresponsive => "Dock not responding: press a dock button to wake it",
            DockState.Connected when _dock.ShowingDockDefault => "Dock shows its be quiet! screen (keyboard features active)",
            DockState.Ready => $"Showing {_pipeline.CurrentScreen} on the dock",
            DockState.Connected => "Connecting to the dock…",
            DockState.PausedForIoCenter => "Paused: IO Center is running",
            DockState.PausedByUser => "Paused",
            DockState.PausedForOtherApp => "Paused: another app is using the keyboard",
            DockState.NoMediaDock => "Media dock not attached",
            DockState.KeyboardOnly => $"{_dock.Model.Name} connected (no screen)",
            _ => _dock.LastError is { } err ? $"Keyboard not available ({err})" : "Keyboard not found",
        };
        _status.Text = state;
        var trayState = _pipeline.LastAlerts.Count > 0 ? TrayState.Alert
            : _dock.State == DockState.PausedForIoCenter ? TrayState.IoCenter
            : _dock.State is DockState.Connected or DockState.Ready or DockState.NoMediaDock or DockState.KeyboardOnly && !_dock.Paused
                ? TrayState.Active : TrayState.Idle;
        var icon = AppIcon.Tray(trayState);
        if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
        var hints = _pipeline.LastSnapshot?.Hints ?? [];
        var tip = hints.Count > 0 ? $"{state}\n{hints[0]}" : $"OverMount\n{state}";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    void TrySetAutostart(bool enabled)
    {
        try { Autostart.Set(enabled); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Autostart change failed: {e.Message}");
        }
    }

    void SaveSettings()
    {
        try { SettingsStore.Save(SettingsStore.DefaultPath, _settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"Saving settings failed: {e.Message}"); }
    }

    static void OpenLogs()
    {
        Directory.CreateDirectory(Log.Directory);
        Process.Start(new ProcessStartInfo("explorer.exe", Log.Directory) { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        Log.Write("Exiting");
        _uiTimer.Stop();
        _overlayTimer.Stop();
        _updateTimer.Stop();
        _companionsTimer.Stop();
        _tickTimer.Dispose();
        _pipeline.Dispose();
        _rgb.Dispose();
        _keyFeed.Dispose();
        _audioStatus.Dispose();
        _macros.Dispose();
        _dock.Dispose();
        _hotkey.Dispose();
        _focusHotkey.Dispose();
        _dockActivity.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _settingsForm?.Close();
        base.ExitThreadCore();
    }
}
