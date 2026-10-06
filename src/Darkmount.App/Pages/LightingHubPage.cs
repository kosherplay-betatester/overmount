using System.Text.Json;
using Darkmount.Keyboard;

namespace Darkmount.App.Pages;

/// <summary>
/// One place for all keyboard lighting. Exactly one thing drives the LEDs at a time — the Lighting studio (OverMount
/// draws layers, per-key colours, reactive/audio/screen effects), the keyboard's built-in effect (runs without the app),
/// or off — and the switch at the top always shows which. Editing either side makes it the active one. The studio
/// lights every RGB keyboard OverMount finds (be quiet!, the Windows Dynamic Lighting standard, OpenRGB); the device list
/// shows them and lets each be switched off.
/// </summary>
public sealed class LightingHubPage : Ui.Page
{
    public enum LightingSource { Studio, BuiltIn, Off }

    readonly Func<AppSettings> _get;
    readonly Action<AppSettings> _apply;
    readonly KeyboardService _keyboard;
    readonly Func<string>? _engineStatus;
    readonly IconTile _studioTile = new('', "Studio"), _builtInTile = new('', "Built-in effect"), _offTile = new('', "Off");
    readonly Label _now = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Ui.Text, Margin = new Padding(0, 4, 0, 2) };
    readonly Label _explain = Ui.Note("", 900);
    readonly FlowLayoutPanel _host = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
    readonly LightingStudioPage _studio;
    readonly LightingPage _builtIn;
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    readonly RgbEngine? _rgb;
    readonly bool _beQuiet; // false on other makers' keyboards: no built-in effect editor, no keyboard commands
    readonly FlowLayoutPanel _devices = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
    readonly Label _devicesNote = Ui.Note("", 900);
    readonly Button _openRgb;
    string _devicesShown = "";
    string? _setupResult; // the last OpenRGB setup outcome, shown until the page is left
    bool _openRgbHelps, _settingUpOpenRgb;
    long _openRgbCheckedAt = long.MinValue / 2;
    LightingSource _source;
    bool _busy;

    /// <param name="beQuiet">False without a be quiet! keyboard: the switch is Studio / the keyboard's own effect only.</param>
    public LightingHubPage(Func<AppSettings> get, Action<AppSettings> apply, KeyboardService keyboard, RgbEngine? rgb,
        Func<string>? engineStatus = null, bool beQuiet = true)
        : base("Lighting", "Choose what lights up your keyboard. Only one runs at a time, so nothing fights over the LEDs.")
    {
        _get = get;
        _apply = apply;
        _keyboard = keyboard;
        _engineStatus = engineStatus;
        _rgb = rgb;
        _beQuiet = beQuiet;
        _studio = new LightingStudioPage(get, apply, () => rgb?.Layout ?? [], () => rgb?.LastFrame ?? new Dictionary<int, Keyboard.Lamps.LampColor>(),
            activated: () => ShowSource(LightingSource.Studio));
        _builtIn = new LightingPage(keyboard, beforeApply: () => { if (_source != LightingSource.BuiltIn) UseSource(LightingSource.BuiltIn, writeKeyboard: false); });
        foreach (var page in new Ui.Page[] { _studio, _builtIn })
        {
            page.Dock = DockStyle.None;
            page.Padding = new Padding(0, 8, 0, 0);
            page.Margin = new Padding(0);
        }

        foreach (var tile in new[] { _studioTile, _builtInTile, _offTile })
        {
            tile.Size = new Size(170, 96);
            tile.Margin = new Padding(0, 0, 14, 8);
        }
        new ToolTip().SetToolTip(_studioTile, "OverMount draws the lighting: layers, per-key colours, typing, music and screen effects.");
        if (!beQuiet) _builtInTile.Text = "Its own effect";
        new ToolTip().SetToolTip(_builtInTile, beQuiet ? "The keyboard's own effect. Keeps running when OverMount is closed."
            : "Your keyboard's own lighting, as set in its maker's app or OpenRGB.");
        new ToolTip().SetToolTip(_offTile, "All keyboard lighting off.");
        _studioTile.Click += (_, _) => UseSource(LightingSource.Studio);
        _builtInTile.Click += (_, _) => UseSource(LightingSource.BuiltIn);
        _offTile.Click += (_, _) => UseSource(LightingSource.Off);

        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        tiles.Controls.AddRange([_studioTile, _builtInTile]);
        if (beQuiet) tiles.Controls.Add(_offTile); // "Off" is a be quiet! keyboard setting
        AddFull(tiles);
        AddFull(_now);
        AddFull(_explain);
        AddFull(new Label
        {
            Text = "YOUR RGB DEVICES", AutoSize = true, ForeColor = Ui.Dim, Font = new Font("Segoe UI Semibold", 8.5f),
            Margin = new Padding(2, 10, 0, 2), UseMnemonic = false,
        });
        AddFull(_devices);
        _openRgb = Ui.Button("Set up OpenRGB…", (_, _) => SetUpOpenRgb());
        _openRgb.Visible = false;
        new ToolTip().SetToolTip(_openRgb, "Installs OpenRGB (free, open source) and starts it with Windows, so OverMount can light keyboards and laptops without Windows Dynamic Lighting.");
        var openRgbRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        openRgbRow.Controls.AddRange([_openRgb, _devicesNote]);
        _devicesNote.Margin = new Padding(8, 8, 0, 0);
        AddFull(openRgbRow);
        AddFull(_host);

        _source = get().RgbEnabled ? LightingSource.Studio : LightingSource.BuiltIn;
        ShowSource(_source);
        _statusTimer.Tick += (_, _) => { UpdateNow(); UpdateDevices(); };
        VisibleChanged += async (_, _) =>
        {
            if (!Visible) { _statusTimer.Stop(); _setupResult = null; return; }
            _statusTimer.Start();
            UpdateDevices();
            await DetectSource();
        };
        Disposed += (_, _) => _statusTimer.Dispose();
    }

    /// <summary>Works out what drives the LEDs now (the studio setting, else the keyboard's own mode).</summary>
    async Task DetectSource()
    {
        if (_get().RgbEnabled) { ShowSource(LightingSource.Studio); return; }
        if (!_beQuiet) { ShowSource(LightingSource.BuiltIn); return; } // other keyboards: nothing to ask the keyboard
        try
        {
            var mode = await _keyboard.Run(q => new Lighting(q).GetMode());
            if (IsDisposed) return; // the window closed while the keyboard answered
            ShowSource(mode == LightingMode.Off ? LightingSource.Off : LightingSource.BuiltIn);
            if (_source == LightingSource.BuiltIn) await _builtIn.Reload();
        }
        catch (Exception e) when (e is KeyboardUnavailableException or IOException or TimeoutException or Darkmount.QLink.QLinkException)
        {
            if (!IsDisposed) ShowSource(LightingSource.BuiltIn);
        }
    }

    /// <summary>Makes <paramref name="source"/> the one that drives the LEDs.</summary>
    async void UseSource(LightingSource source, bool writeKeyboard = true)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_get()))!;
            s.RgbEnabled = source == LightingSource.Studio;
            _apply(s);
            ShowSource(source);
            if (!writeKeyboard || !_beQuiet || source == LightingSource.Studio) return;
            try
            {
                await _keyboard.Run(q =>
                {
                    KeyboardBackupGuard.EnsureBackup(q);
                    var lighting = new Lighting(q);
                    var want = source == LightingSource.Off ? LightingMode.Off : LightingMode.General;
                    if (lighting.GetMode() != want) lighting.SetMode(want);
                });
                if (source == LightingSource.BuiltIn && !IsDisposed) await _builtIn.Reload();
            }
            catch (Exception e) when (e is KeyboardUnavailableException or IOException or TimeoutException or Darkmount.QLink.QLinkException)
            {
                if (!IsDisposed) _explain.Text = e is KeyboardUnavailableException ? e.Message : $"The keyboard didn't respond: {e.Message}";
            }
        }
        finally { _busy = false; }
    }

    void ShowSource(LightingSource source) => Ui.Batch(this, () => ShowSourceCore(source));

    void ShowSourceCore(LightingSource source)
    {
        _source = source;
        _studioTile.Selected = source == LightingSource.Studio;
        _builtInTile.Selected = source == LightingSource.BuiltIn;
        _offTile.Selected = source == LightingSource.Off;
        _explain.Text = source switch
        {
            LightingSource.Studio when !_beQuiet => "OverMount is drawing your scene on the keyboards below (up to 30 frames a second). " +
                                                    "When the app closes, each one goes back to its own effect.",
            LightingSource.BuiltIn when !_beQuiet => "Your keyboards show their own effect (set in their maker's app or OpenRGB). " +
                                                     "Pick Studio to light them with OverMount.",
            LightingSource.Studio => "OverMount is drawing your scene (up to 30 frames a second). When the app closes, the keyboard " +
                                     "goes back to its built-in effect.",
            LightingSource.BuiltIn => "The keyboard runs its own effect, even without OverMount. Changes below are saved to the keyboard.",
            _ => "The keyboard's lighting is off. Pick Studio or Built-in effect to turn it back on.",
        };
        Control? page = source switch { LightingSource.Studio => _studio, LightingSource.BuiltIn when _beQuiet => _builtIn, _ => null };
        if (_host.Controls.Count != (page is null ? 0 : 1) || (page is not null && _host.Controls[0] != page))
        {
            _host.SuspendLayout();
            _host.Controls.Clear();
            if (page is not null) Ui.Add(_host, page);
            _host.ResumeLayout();
        }
        UpdateNow();
    }

    void UpdateNow()
    {
        var s = _get();
        _now.Text = _source switch
        {
            LightingSource.Studio => $"Now: {s.Scene?.Name ?? "Studio scene"}  ·  {_engineStatus?.Invoke() ?? "Lighting studio"}",
            LightingSource.BuiltIn => _beQuiet ? "Now: the keyboard's built-in effect" : "Now: your keyboard's own effect",
            _ => "Now: lighting off",
        };
    }

    /// <summary>Lists the lighting devices the engine found (rebuilt only when something changed).</summary>
    void UpdateDevices()
    {
        var list = _rgb?.Devices ?? [];
        var off = new HashSet<string>(_get().LightingDevicesOff ?? [], StringComparer.OrdinalIgnoreCase);
        bool beQuietOnly = list.All(d => d.Via == LightDevices.LightVia.BeQuiet);
        if (Environment.TickCount64 - _openRgbCheckedAt > 15_000 && !_settingUpOpenRgb)
        {
            _openRgbCheckedAt = Environment.TickCount64;
            _ = Task.Run(() => beQuietOnly && !Setup.OpenRgbSetup.ServerReachable()).ContinueWith(t =>
            {
                if (IsDisposed || t.IsFaulted || t.Result == _openRgbHelps) return;
                _openRgbHelps = t.Result;
                _devicesShown = ""; // redraw
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        string signature = string.Join("|", list.Select(d => $"{d.Id}:{d.Active}:{d.State}:{off.Contains(d.Id)}")) +
                           $"|{_openRgbHelps}|{_settingUpOpenRgb}|{_setupResult}";
        if (signature == _devicesShown) return;
        _devicesShown = signature;
        Ui.Batch(this, () =>
        {
            foreach (Control c in _devices.Controls.Cast<Control>().ToList()) { _devices.Controls.Remove(c); c.Dispose(); }
            if (list.Count == 0)
                Ui.Add(_devices, Ui.Note("No RGB keyboard found yet. OverMount lights be quiet! keyboards, keyboards and laptops with " +
                                         "Windows Dynamic Lighting, and many more through OpenRGB.", 900));
            foreach (var d in list)
            {
                var box = Ui.Check($"{d.Name}   ·   {d.ViaText}   ·   {d.State}");
                box.Checked = !off.Contains(d.Id);
                box.ForeColor = d.Active ? Ui.Text : Ui.Dim;
                string id = d.Id;
                box.CheckedChanged += (_, _) => SetDeviceOn(id, box.Checked);
                Ui.Add(_devices, box);
            }
            _openRgb.Visible = _openRgbHelps || _settingUpOpenRgb;
            _openRgb.Enabled = !_settingUpOpenRgb;
            _devicesNote.Text = _settingUpOpenRgb ? "Setting up OpenRGB… (Windows asks for permission once)"
                : _setupResult ?? (_openRgbHelps ? "Laptop or other keyboard not listed? OpenRGB lights hundreds more (ASUS TUF/ROG, MSI, Razer, Corsair…)." : "");
        });
    }

    void SetDeviceOn(string id, bool on)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_get()))!;
        s.LightingDevicesOff ??= [];
        s.LightingDevicesOff.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (!on) s.LightingDevicesOff.Add(id);
        _apply(s);
    }

    async void SetUpOpenRgb()
    {
        if (_settingUpOpenRgb) return;
        _settingUpOpenRgb = true;
        _devicesShown = "";
        UpdateDevices();
        try
        {
            var result = await Task.Run(Setup.OpenRgbSetup.SetUpAsync); // off the UI thread: the prompt blocks the caller
            if (IsDisposed) return;
            _setupResult = result switch
            {
                Setup.OpenRgbSetup.Result.Running => "OpenRGB is running. Its keyboards show up above within a few seconds.",
                Setup.OpenRgbSetup.Result.Declined => "OpenRGB not set up (permission declined).",
                Setup.OpenRgbSetup.Result.NoWinget => "Windows' package manager (winget) isn't available: install OpenRGB from openrgb.org.",
                _ => "OpenRGB could not be set up (see the log). You can install it from openrgb.org.",
            };
        }
        finally
        {
            _settingUpOpenRgb = false;
            _openRgbCheckedAt = long.MinValue / 2;
            _devicesShown = "";
            if (!IsDisposed) UpdateDevices();
        }
    }

    /// <summary>Reloads both editors (after a profile was applied).</summary>
    public void Reload()
    {
        _studio.Reload();
        _ = DetectSource();
    }
}
