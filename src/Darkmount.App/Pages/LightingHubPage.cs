using System.Text.Json;
using Darkmount.Keyboard;

namespace Darkmount.App.Pages;

/// <summary>
/// One place for all keyboard lighting. Exactly one thing drives the LEDs at a time — the Lighting studio (OverMount
/// draws layers, per-key colours, reactive/audio/screen effects), the keyboard's built-in effect (runs without the app),
/// or off — and the switch at the top always shows which. Editing either side makes it the active one.
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
    LightingSource _source;
    bool _busy;

    public LightingHubPage(Func<AppSettings> get, Action<AppSettings> apply, KeyboardService keyboard, RgbEngine? rgb, Func<string>? engineStatus = null)
        : base("Lighting", "Choose what lights up your keyboard. Only one runs at a time, so nothing fights over the LEDs.")
    {
        _get = get;
        _apply = apply;
        _keyboard = keyboard;
        _engineStatus = engineStatus;
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
        new ToolTip().SetToolTip(_builtInTile, "The keyboard's own effect. Keeps running when OverMount is closed.");
        new ToolTip().SetToolTip(_offTile, "All keyboard lighting off.");
        _studioTile.Click += (_, _) => UseSource(LightingSource.Studio);
        _builtInTile.Click += (_, _) => UseSource(LightingSource.BuiltIn);
        _offTile.Click += (_, _) => UseSource(LightingSource.Off);

        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        tiles.Controls.AddRange([_studioTile, _builtInTile, _offTile]);
        AddFull(tiles);
        AddFull(_now);
        AddFull(_explain);
        AddFull(_host);

        _source = get().RgbEnabled ? LightingSource.Studio : LightingSource.BuiltIn;
        ShowSource(_source);
        _statusTimer.Tick += (_, _) => UpdateNow();
        VisibleChanged += async (_, _) =>
        {
            if (!Visible) { _statusTimer.Stop(); return; }
            _statusTimer.Start();
            await DetectSource();
        };
        Disposed += (_, _) => _statusTimer.Dispose();
    }

    /// <summary>Works out what drives the LEDs now (the studio setting, else the keyboard's own mode).</summary>
    async Task DetectSource()
    {
        if (_get().RgbEnabled) { ShowSource(LightingSource.Studio); return; }
        try
        {
            var mode = await _keyboard.Run(q => new Lighting(q).GetMode());
            ShowSource(mode == LightingMode.Off ? LightingSource.Off : LightingSource.BuiltIn);
            if (_source == LightingSource.BuiltIn) await _builtIn.Reload();
        }
        catch (Exception e) when (e is KeyboardUnavailableException or IOException or TimeoutException or Darkmount.QLink.QLinkException)
        {
            ShowSource(LightingSource.BuiltIn);
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
            if (!writeKeyboard || source == LightingSource.Studio) return;
            try
            {
                await _keyboard.Run(q =>
                {
                    KeyboardBackupGuard.EnsureBackup(q);
                    var lighting = new Lighting(q);
                    var want = source == LightingSource.Off ? LightingMode.Off : LightingMode.General;
                    if (lighting.GetMode() != want) lighting.SetMode(want);
                });
                if (source == LightingSource.BuiltIn) await _builtIn.Reload();
            }
            catch (Exception e) when (e is KeyboardUnavailableException or IOException or TimeoutException or Darkmount.QLink.QLinkException)
            {
                _explain.Text = e is KeyboardUnavailableException ? e.Message : $"The keyboard didn't respond: {e.Message}";
            }
        }
        finally { _busy = false; }
    }

    void ShowSource(LightingSource source)
    {
        _source = source;
        _studioTile.Selected = source == LightingSource.Studio;
        _builtInTile.Selected = source == LightingSource.BuiltIn;
        _offTile.Selected = source == LightingSource.Off;
        _explain.Text = source switch
        {
            LightingSource.Studio => "OverMount is drawing your scene (up to 30 frames a second). When the app closes, the keyboard " +
                                     "goes back to its built-in effect.",
            LightingSource.BuiltIn => "The keyboard runs its own effect, even without OverMount. Changes below are saved to the keyboard.",
            _ => "The keyboard's lighting is off. Pick Studio or Built-in effect to turn it back on.",
        };
        Control? page = source switch { LightingSource.Studio => _studio, LightingSource.BuiltIn => _builtIn, _ => null };
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
            LightingSource.BuiltIn => "Now: the keyboard's built-in effect",
            _ => "Now: lighting off",
        };
    }

    /// <summary>Reloads both editors (after a profile was applied).</summary>
    public void Reload()
    {
        _studio.Reload();
        _ = DetectSource();
    }
}
